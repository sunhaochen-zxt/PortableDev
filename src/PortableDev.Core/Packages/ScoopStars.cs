using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortableDev.Core.Packages;

/// <summary>
/// GitHub stars, used as the popularity ranking for the packages home page.
///
/// A result is cached on the stick for a day: the unauthenticated API allows 60 requests
/// per hour, and a cold start already asks for every recommended project once. A rate
/// limit (403) simply stops the run — the caller sorts by whatever is known and keeps the
/// curated order for the rest.
/// </summary>
public static class ScoopStars
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Load star counts for the given repos ("owner/name" or a GitHub url).</summary>
    public static async Task<Dictionary<string, int>> LoadAsync(
        HttpClient http, string stickRoot, IEnumerable<string> repos, CancellationToken ct = default)
    {
        var wanted = repos
            .Select(RepoOf)
            .Where(r => r is not null)
            .Select(r => r!.ToLowerInvariant())
            .Distinct()
            .ToList();

        var stars = ReadCache(stickRoot);
        if (stars is null || DateTimeOffset.UtcNow - stars.Fetched > MaxAge)
            stars = new Cache();

        var missing = wanted.Where(r => !stars.Stars.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            var stopped = false;

            await Task.WhenAll(missing.Select(async repo =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (stopped) return;

                    using var res = await http.GetAsync(
                        "https://api.github.com/repos/" + repo,
                        HttpCompletionOption.ResponseHeadersRead, ct);

                    if (res.StatusCode == HttpStatusCode.Forbidden || res.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        stopped = true;
                        return;
                    }

                    if (!res.IsSuccessStatusCode) return;

                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    if (!doc.RootElement.TryGetProperty("stargazers_count", out var value)) return;
                    if (!value.TryGetInt32(out var count)) return;

                    lock (stars.Stars) stars.Stars[repo] = count;
                }
                catch
                {
                    // A missing star count only costs the ranking, never the install.
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        stars.Fetched = DateTimeOffset.UtcNow;
        WriteCache(stickRoot, stars);
        return new Dictionary<string, int>(stars.Stars, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Pull "owner/name" out of a github.com url; null for other home pages.</summary>
    public static string? RepoOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var text = url.Trim();
        var index = text.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var rest = text[(index + "github.com/".Length)..].Trim('/');
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        var owner = parts[0];
        var name = parts[1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Length == 0 || owner.Length == 0) return null;

        return owner + "/" + name;
    }

    private sealed class Cache
    {
        public DateTimeOffset Fetched { get; set; }
        public Dictionary<string, int> Stars { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string CacheFile(string stickRoot) =>
        Path.Combine(stickRoot, "cache", "scoop", "stars.json");

    private static Cache? ReadCache(string stickRoot)
    {
        try
        {
            var file = CacheFile(stickRoot);
            if (!File.Exists(file)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var cache = new Cache();

            if (root.TryGetProperty("fetched", out var fetched)
                && DateTimeOffset.TryParse(fetched.GetString(), out var when))
                cache.Fetched = when;

            if (root.TryGetProperty("stars", out var stars) && stars.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in stars.EnumerateObject())
                    if (item.Value.TryGetInt32(out var count))
                        cache.Stars[item.Name] = count;
            }

            return cache;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string stickRoot, Cache cache)
    {
        try
        {
            var file = CacheFile(stickRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var json = JsonSerializer.Serialize(new
            {
                fetched = cache.Fetched.ToString("O"),
                stars = cache.Stars,
            });

            File.WriteAllText(file, json);
        }
        catch
        {
            // cache is an optimisation
        }
    }
}
