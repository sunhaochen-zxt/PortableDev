using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Core.Model;

namespace PortableDev.Core.Update;

/// <summary>
/// Asks each tool's upstream for its newest version.
///
/// No credentials, no scraping of pages that need JS: every source here answers a
/// plain GET with JSON or text. Anything that cannot be determined reliably returns
/// null and is reported as "unknown", never as "up to date" — a silent false
/// negative is worse than an honest gap.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly string? _stickRoot;

    /// <param name="stickRoot">
    /// Where the bucket manifest cache may live. Null keeps it in %TEMP%, which is fine
    /// for a one-off check but means the next run fetches again.
    /// </param>
    public UpdateChecker(HttpClient? http = null, string? stickRoot = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _stickRoot = stickRoot;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "PortableDev/1.0");
    }

    public async Task<UpdateInfo?> CheckAsync(
        ToolManifest tool, string currentVersion, CancellationToken ct = default)
    {
        var spec = tool.Update;
        if (spec is null || string.IsNullOrWhiteSpace(spec.Kind)) return null;

        try
        {
            return spec.Kind.ToLowerInvariant() switch
            {
                "github" => await CheckGitHubAsync(tool, spec, currentVersion, ct),
                "node" => await CheckNodeAsync(tool, spec, currentVersion, ct),
                "vscode" => await CheckVsCodeAsync(tool, spec, currentVersion, ct),
                "sevenzip" => await CheckSevenZipAsync(tool, spec, currentVersion, ct),
                "scoop" => await CheckScoopAsync(tool, spec, currentVersion, ct),
                _ => null,
            };
        }
        catch
        {
            // Network trouble must never break the page; the caller shows "unknown".
            return null;
        }
    }

    // ---------------------------------------------------------------- github ----
    private async Task<UpdateInfo?> CheckGitHubAsync(
        ToolManifest tool, UpdateSpec spec, string currentVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.Repo)) return null;

        var json = await _http.GetStringAsync(
            "https://api.github.com/repos/" + spec.Repo + "/releases/latest", ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var version = NormalizeVersion(tag, spec);

        if (!string.IsNullOrWhiteSpace(spec.SkipVersionPattern)
            && Regex.IsMatch(tag, spec.SkipVersionPattern, RegexOptions.IgnoreCase))
            return null;

        var assetRe = string.IsNullOrWhiteSpace(spec.Pattern) ? null : new Regex(spec.Pattern, RegexOptions.IgnoreCase);

        string? url = null;
        long? size = null;
        Match? assetMatch = null;
        string? assetName = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (name.Length == 0) continue;
                if (assetRe is not null && !assetRe.IsMatch(name)) continue;

                url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes)) size = bytes;
                assetName = name;

                // A version embedded in the asset name is more precise than the tag
                // (python-build-standalone tags a build date, not the python version).
                if (assetRe is not null)
                {
                    var m = assetRe.Match(name);
                    assetMatch = m;
                    var g = FirstVersionGroup(m);
                    if (!string.IsNullOrEmpty(g)) version = NormalizeVersion(g, spec);
                }
                break;
            }
        }

        if (url is null) return null;

        if (!string.IsNullOrWhiteSpace(spec.Url))
            url = Fill(spec.Url!, version, assetUrl: url, groups: assetMatch);

        // Locate the checksum sidecar while the asset list is already in hand. Fetching its
        // body is left to the download step: a read-only "update" run must not add a
        // request per tool just to look at version numbers.
        var sha256Url = ResolveSha256Url(root, spec, version, url, assetName, assetMatch);

        return new UpdateInfo
        {
            ToolId = tool.Tool.Id,
            CurrentVersion = currentVersion,
            AvailableVersion = version,
            DownloadUrl = url,
            SizeBytes = size,
            Sha256Url = sha256Url,
        };
    }

    /// <summary>
    /// The checksum URL, if the source publishes one: an explicit [update].sha256_url
    /// template wins, then a "&lt;asset&gt;.sha256" sibling in the same release.
    /// </summary>
    private static string? ResolveSha256Url(
        JsonElement root, UpdateSpec spec, string version, string assetUrl,
        string? assetName, Match? assetMatch)
    {
        if (!string.IsNullOrWhiteSpace(spec.Sha256Url))
        {
            var filled = Fill(spec.Sha256Url!, version, assetUrl, assetMatch);
            return string.IsNullOrWhiteSpace(filled) ? null : filled;
        }

        if (assetName is null) return null;

        // python-build-standalone and several other projects publish "<asset>.sha256".
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (!string.Equals(name, assetName + ".sha256", StringComparison.OrdinalIgnoreCase)) continue;
                return a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetch and parse the published checksum for an update, when there is one.
    ///
    /// Candidates in order: the [update].sha256_url template, a sidecar discovered next to
    /// the asset, then the "&lt;download-url&gt;.sha256" convention (jq, 7-Zip and others
    /// publish exactly that). Offline, 404 or an unparseable body all yield null — an
    /// unknown checksum, never a guessed one.
    /// </summary>
    public async Task<string?> ResolveSha256Async(
        ToolManifest tool, UpdateInfo info, CancellationToken ct = default)
    {
        // A bucket manifest carries the reviewed hash; never go looking for a sidecar that
        // could disagree with it.
        if (!string.IsNullOrWhiteSpace(info.Sha256)) return info.Sha256;

        var spec = tool.Update;
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(spec?.Sha256Url))
        {
            var filled = Fill(spec!.Sha256Url!, info.AvailableVersion, info.DownloadUrl, groups: null);
            if (!string.IsNullOrWhiteSpace(filled)) candidates.Add(filled);
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256Url)) candidates.Add(info.Sha256Url!);
        if (!string.IsNullOrWhiteSpace(info.DownloadUrl)) candidates.Add(info.DownloadUrl + ".sha256");

        foreach (var url in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var body = await _http.GetStringAsync(url, ct);
                var hash = Sha256Text.Parse(body);
                if (hash is not null) return hash;
            }
            catch
            {
                // Missing or unreachable sidecar: try the next candidate, then give up.
            }
        }

        return null;
    }

    // ----------------------------------------------------------------- scoop ----
    private async Task<UpdateInfo?> CheckScoopAsync(
        ToolManifest tool, UpdateSpec spec, string currentVersion, CancellationToken ct)
    {
        var bucket = string.IsNullOrWhiteSpace(spec.Bucket) ? "Main" : spec.Bucket!.Trim();
        var app = string.IsNullOrWhiteSpace(spec.App) ? tool.Tool.Id : spec.App!.Trim();

        var source = new Packages.ScoopSource(_http, _stickRoot);
        var (package, _) = await source.GetAsync(bucket + "/" + app, false, ct);
        if (package is null) return null;

        var version = package.Version;
        if (!string.IsNullOrWhiteSpace(spec.StripPrefix)
            && version.StartsWith(spec.StripPrefix!, StringComparison.OrdinalIgnoreCase))
            version = version[spec.StripPrefix!.Length..];

        return new UpdateInfo
        {
            ToolId = tool.Tool.Id,
            CurrentVersion = currentVersion,
            AvailableVersion = version,
            DownloadUrl = package.Url,
            Sha256 = package.Sha256,
            ExtractDir = package.ExtractDir,
            RenameTo = package.RenameTo,
        };
    }

    // ------------------------------------------------------------------ node ----
    private async Task<UpdateInfo?> CheckNodeAsync(
        ToolManifest tool, UpdateSpec spec, string currentVersion, CancellationToken ct)
    {
        var index = spec.Index ?? "https://npmmirror.com/mirrors/node/index.json";
        var json = await _http.GetStringAsync(index, ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? best = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("version", out var v)) continue;
            var raw = v.GetString() ?? string.Empty;
            var ver = raw.TrimStart('v', 'V');

            if (!string.IsNullOrWhiteSpace(spec.Pattern) && !Regex.IsMatch(ver, spec.Pattern)) continue;
            if (!string.IsNullOrWhiteSpace(spec.SkipVersionPattern)
                && Regex.IsMatch(ver, spec.SkipVersionPattern, RegexOptions.IgnoreCase)) continue;

            if (best is null || VersionOrder.Compare(ver, best) > 0) best = ver;
        }

        if (best is null) return null;

        var url = Fill(spec.Url ?? string.Empty, best, assetUrl: null, groups: null);
        if (url.Length == 0) return null;

        return new UpdateInfo
        {
            ToolId = tool.Tool.Id,
            CurrentVersion = currentVersion,
            AvailableVersion = best,
            DownloadUrl = url,
        };
    }

    // ---------------------------------------------------------------- vscode ----
    private async Task<UpdateInfo?> CheckVsCodeAsync(
        ToolManifest tool, UpdateSpec spec, string currentVersion, CancellationToken ct)
    {
        var index = spec.Index ?? "https://update.code.visualstudio.com/api/releases/stable";
        var json = await _http.GetStringAsync(index, ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? best = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var ver = e.GetString();
            if (string.IsNullOrWhiteSpace(ver)) continue;
            if (!string.IsNullOrWhiteSpace(spec.Pattern) && !Regex.IsMatch(ver, spec.Pattern)) continue;
            if (best is null || VersionOrder.Compare(ver, best) > 0) best = ver;
        }

        if (best is null) return null;

        var url = Fill(spec.Url ?? string.Empty, best, assetUrl: null, groups: null);
        if (url.Length == 0) return null;

        return new UpdateInfo
        {
            ToolId = tool.Tool.Id,
            CurrentVersion = currentVersion,
            AvailableVersion = best,
            DownloadUrl = url,
        };
    }

    // -------------------------------------------------------------- sevenzip ----
    private async Task<UpdateInfo?> CheckSevenZipAsync(
        ToolManifest tool, UpdateSpec spec, string currentVersion, CancellationToken ct)
    {
        var index = spec.Index ?? "https://www.7-zip.org/download.html";
        var html = await _http.GetStringAsync(index, ct);

        var re = new Regex(string.IsNullOrWhiteSpace(spec.Pattern) ? @"7z(\d)(\d+)-x64\.exe" : spec.Pattern,
            RegexOptions.IgnoreCase);

        string? best = null;
        foreach (Match m in re.Matches(html))
        {
            var v = FirstVersionGroup(m);
            if (string.IsNullOrEmpty(v)) continue;
            if (best is null || VersionOrder.Compare(v, best) > 0) best = v;
        }

        if (best is null) return null;

        var url = Fill(spec.Url ?? string.Empty, best, assetUrl: null, groups: null);
        if (url.Length == 0) return null;

        return new UpdateInfo
        {
            ToolId = tool.Tool.Id,
            CurrentVersion = currentVersion,
            AvailableVersion = best,
            DownloadUrl = url,
        };
    }

    // ---------------------------------------------------------------- helpers ----
    private static string NormalizeVersion(string raw, UpdateSpec spec)
    {
        var v = (raw ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(spec.StripPrefix) &&
            v.StartsWith(spec.StripPrefix!, StringComparison.OrdinalIgnoreCase))
            v = v[spec.StripPrefix!.Length..];

        return v.TrimStart('v', 'V');
    }

    private static string FirstVersionGroup(Match m)
    {
        if (m.Groups["ver"].Success) return m.Groups["ver"].Value;
        for (var i = 1; i < m.Groups.Count; i++)
            if (m.Groups[i].Success) return m.Groups[i].Value;
        return string.Empty;
    }

    private static string Fill(string template, string version, string? assetUrl, Match? groups)
    {
        var s = template
            .Replace("{version_compact}", version.Replace(".", string.Empty))
            .Replace("{version}", version);
        if (groups is not null)
        {
            foreach (var name in groups.Groups.Keys)
            {
                if (int.TryParse(name, out _)) continue;
                s = s.Replace("{" + name + "}", groups.Groups[name].Value);
            }
        }
        return s;
    }
}
