using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortableDev.Core.Packages;

/// <summary>
/// Reads Scoop bucket manifests.
///
/// Two endpoints are tried, because the plain raw file host is blocked on some networks
/// while the GitHub API is not: the raw URL first, the API (base64 content) second. A
/// successful read is cached on the stick, so reinstalling or resolving dependencies does
/// not hit the network again; <c>--refresh</c> overrides that.
/// </summary>
public sealed class ScoopSource
{
    private static readonly string[] DefaultBuckets = { "Main", "Extras" };

    private readonly HttpClient _http;
    private readonly string _cacheRoot;

    public ScoopSource(HttpClient http, string stickRoot)
    {
        _http = http;
        _cacheRoot = Path.Combine(stickRoot, "cache", "scoop");
    }

    public static IReadOnlyList<string> Buckets => DefaultBuckets;

    /// <summary>
    /// Fetch one manifest. <paramref name="resource"/> is "Bucket/app" or just "app";
    /// a bare name is looked up in Main first, then Extras.
    /// </summary>
    public async Task<(ScoopPackage? Package, string? Error)> GetAsync(
        string resource, bool refresh, CancellationToken ct)
    {
        var (bucket, app) = Split(resource, "Main");
        var candidates = resource.Contains('/')
            ? new[] { (bucket, app) }
            : new[] { ("Main", app), ("Extras", app) };

        var errors = new List<string>();
        foreach (var (b, a) in candidates)
        {
            var (package, error) = await GetOneAsync(b, a, refresh, ct);
            if (package is not null) return (package, null);
            errors.Add($"{b}/{a}: {error}");
        }

        return (null, string.Join("; ", errors));
    }

    private async Task<(ScoopPackage?, string?)> GetOneAsync(
        string bucket, string app, bool refresh, CancellationToken ct)
    {
        var cacheFile = Path.Combine(_cacheRoot, bucket, app + ".json");
        if (!refresh && File.Exists(cacheFile))
        {
            var cached = await File.ReadAllTextAsync(cacheFile, ct);
            var (cachedPackage, cachedError) = ScoopManifest.Parse(cached, app, bucket);
            if (cachedPackage is not null) return (cachedPackage, null);
            if (cachedError is not null) return (null, cachedError);
        }

        var raw = $"https://raw.githubusercontent.com/ScoopInstaller/{bucket}/master/bucket/{app}.json";
        var body = await TryGetStringAsync(raw, parseApiEnvelope: false, ct);
        var via = "raw";

        if (body is null)
        {
            var api = $"https://api.github.com/repos/ScoopInstaller/{bucket}/contents/bucket/{app}.json";
            body = await TryGetStringAsync(api, parseApiEnvelope: true, ct);
            via = "api";
        }

        if (body is null)
        {
            // Network down but a cache exists: a stale manifest beats no manifest. The
            // version and hash are then verified against the download like any other.
            if (File.Exists(cacheFile))
            {
                var stale = await File.ReadAllTextAsync(cacheFile, ct);
                return ScoopManifest.Parse(stale, app, bucket);
            }
            return (null, "取不到清单（raw 和 api 都失败）");
        }

        var (package, error) = ScoopManifest.Parse(body, app, bucket);
        if (package is null) return (null, error);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllTextAsync(cacheFile, body, new UTF8Encoding(false), ct);
        }
        catch
        {
            // A read-only cache only costs a network round-trip next time.
        }

        _ = via;
        return (package, null);
    }

    private async Task<string?> TryGetStringAsync(string url, bool parseApiEnvelope, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);

            if (!parseApiEnvelope) return text;

            // GitHub contents API: {"content":"<base64>","encoding":"base64",...}
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("content", out var content)) return null;
            var encoded = content.GetString();
            if (string.IsNullOrEmpty(encoded)) return null;
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace("\n", "")));
        }
        catch
        {
            return null;
        }
    }

    private static (string Bucket, string App) Split(string resource, string defaultBucket)
    {
        var slash = resource.IndexOf('/');
        if (slash < 0) return (defaultBucket, resource.Trim());

        var bucket = resource[..slash].Trim();
        var app = resource[(slash + 1)..].Trim();
        if (app.Length == 0) return (defaultBucket, bucket);
        return (bucket, app);
    }
}
