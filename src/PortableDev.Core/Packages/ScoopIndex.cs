using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortableDev.Core.Packages;

/// <summary>
/// The searchable list of bucket apps.
///
/// Scoop searches its cloned manifests; this tool has no clone, so the names come from the
/// GitHub tree of each bucket (one request per bucket) and are cached on the stick. That is
/// enough to find a package by name; a one-line description is fetched only for the results
/// that are actually shown.
/// </summary>
public sealed class ScoopIndex
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxStale = TimeSpan.FromDays(30);

    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public sealed record Entry(string Bucket, string App);

    public ScoopIndex(HttpClient http, string stickRoot)
    {
        _http = http;
        _cacheDir = Path.Combine(stickRoot, "cache", "scoop");
    }

    /// <summary>Every app name in every supported bucket, plus a warning when something failed.</summary>
    public async Task<(List<Entry> Entries, string? Error)> LoadAsync(bool refresh, CancellationToken ct = default)
    {
        var entries = new List<Entry>();
        var errors = new List<string>();

        foreach (var bucket in ScoopSource.Buckets)
        {
            var (apps, error) = await LoadBucketAsync(bucket, refresh, ct);
            if (apps is null)
            {
                errors.Add(bucket + ": " + error);
                continue;
            }

            entries.AddRange(apps.Select(app => new Entry(bucket, app)));
        }

        return (entries, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private async Task<(List<string>? Apps, string? Error)> LoadBucketAsync(
        string bucket, bool refresh, CancellationToken ct)
    {
        var cacheFile = Path.Combine(_cacheDir, "index-" + bucket + ".json");

        if (!refresh)
        {
            var cached = await ReadCacheAsync(cacheFile, ct);
            if (cached is not null && DateTimeOffset.UtcNow - cached.Fetched < MaxAge)
                return (cached.Apps, null);
        }

        var apps = await FetchTreeAsync(bucket, ct);

        if (apps is null)
        {
            // Network down: a stale list still lets the user search and pick something.
            var stale = await ReadCacheAsync(cacheFile, ct);
            if (stale is not null && DateTimeOffset.UtcNow - stale.Fetched < MaxStale)
                return (stale.Apps, null);

            return (null, "取不到 bucket 列表（网络不通？）");
        }

        await WriteCacheAsync(cacheFile, apps, ct);
        return (apps, null);
    }

    private async Task<List<string>?> FetchTreeAsync(string bucket, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/ScoopInstaller/{bucket}/git/trees/master?recursive=1";
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
                return null;

            var apps = new List<string>();
            foreach (var node in tree.EnumerateArray())
            {
                if (!node.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
                var p = path.GetString() ?? string.Empty;
                if (!p.StartsWith("bucket/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                apps.Add(Path.GetFileNameWithoutExtension(p));
            }

            return apps
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Name matches: prefix first, then shorter names, then alphabetical.</summary>
    public static List<Entry> Search(IEnumerable<Entry> entries, string term, int limit)
    {
        var t = (term ?? string.Empty).Trim();
        if (t.Length == 0) return new List<Entry>();

        return entries
            .Where(e => e.App.Contains(t, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.App.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.App.Length)
            .ThenBy(e => e.App, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    private sealed class Cached
    {
        public DateTimeOffset Fetched { get; init; }
        public List<string> Apps { get; init; } = new();
    }

    private static async Task<Cached?> ReadCacheAsync(string file, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(file)) return null;

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<string>();
            foreach (var a in apps.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } s)
                    list.Add(s);

            DateTimeOffset.TryParse(root.TryGetProperty("fetched", out var f) ? f.GetString() : null,
                out var fetched);
            return list.Count == 0 ? null : new Cached { Fetched = fetched, Apps = list };
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(string file, List<string> apps, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = JsonSerializer.Serialize(new
            {
                fetched = DateTimeOffset.UtcNow.ToString("O"),
                apps,
            });
            await File.WriteAllTextAsync(file, json, new UTF8Encoding(false), ct);
        }
        catch
        {
            // A read-only cache only costs a network round-trip next time.
        }
    }
}
