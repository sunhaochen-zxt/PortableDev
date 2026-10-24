using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Core.Update;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>
/// Asks upstream for the newest version of every tool that declares an [update]
/// source. Read-only: it never downloads or writes anything.
/// </summary>
public static class UpdateCommand
{
    public static int Run(CommandOptions opts)
    {
        var (root, count) = VolumeLocator.Resolve(opts.Root);
        if (root is null)
        {
            Console.Error.WriteLine(count == 0 ? "stick not found" : "multiple sticks found; use --root");
            return 4;
        }

        var manifest = ManifestLoader.Load(root);
        var targets = manifest.Tools
            .Where(t => t.Value.Tool.Enabled && t.Value.Update is not null)
            .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            Console.WriteLine("no tool declares an [update] source");
            return 0;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.Add("User-Agent", "PortableDev/1.0");
        var checker = new UpdateChecker(http);

        var only = opts.Positional.FirstOrDefault();
        if (only is not null)
            targets = targets.Where(t => t.Key.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();

        var rows = new List<(string Id, string Current, string? Latest, string Url, string Status)>();
        var infos = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, tool) in targets)
        {
            var current = tool.Tool.Version;
            var info = checker.CheckAsync(tool, current, CancellationToken.None)
                              .GetAwaiter().GetResult();

            var status = info is null ? "unknown"
                       : info.IsNewer ? "update"
                       : "current";

            if (info is not null) infos[id] = info;
            rows.Add((id, current, info?.AvailableVersion, info?.DownloadUrl ?? string.Empty, status));
        }

        if (opts.Fetch)
            return Fetch(rows, infos, manifest, root);

        if (opts.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                rows.Select(r => new { tool = r.Id, current = r.Current, latest = r.Latest, status = r.Status, url = r.Url }),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"{"tool",-14}{"installed",-14}{"latest",-14}status");
        foreach (var r in rows)
            Console.WriteLine($"{r.Id,-14}{r.Current,-14}{r.Latest ?? "?",-14}{r.Status}");

        var updates = rows.Count(r => r.Status == "update");
        Console.WriteLine();
        Console.WriteLine(updates == 0 ? "all tools are up to date" : updates + " update(s) available");
        return 0;
    }

    /// <summary>
    /// Download only, into downloads&lt;id&gt;&lt;version&gt;. Useful for a slow link:
    /// fetch overnight, install from the GUI later without touching the network.
    ///
    /// Every file is verified before it counts as downloaded. A checksum is fetched from
    /// the source's sidecar when it publishes one; without one, the actual hash is
    /// recorded and the file is accepted as "unverified", never as "good".
    /// </summary>
    private static int Fetch(
        List<(string Id, string Current, string? Latest, string Url, string Status)> rows,
        Dictionary<string, UpdateInfo> infos,
        RootManifest manifest,
        string stickRoot)
    {
        var todo = rows.Where(r => r.Status == "update").ToList();
        if (todo.Count == 0) { Console.WriteLine("nothing to download"); return 0; }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.Add("User-Agent", "PortableDev/1.0");
        var checker = new UpdateChecker(http);

        var failures = 0;
        foreach (var row in todo)
        {
            var info = infos[row.Id];
            manifest.Tools.TryGetValue(row.Id, out var tool);

            var name = NameOf(info.DownloadUrl);
            var dir = Path.Combine(stickRoot, "downloads", row.Id, info.AvailableVersion);
            var dest = Path.Combine(dir, name);

            Console.WriteLine();
            Console.WriteLine($"{row.Id}: {info.Summary}");
            Console.WriteLine($"  -> {dest}");

            // Resolved only here: the read-only listing never pays for a checksum request.
            var expected = tool is null
                ? null
                : checker.ResolveSha256Async(tool, info, CancellationToken.None).GetAwaiter().GetResult();

            Console.WriteLine(expected is null
                ? "  该源没有提供校验值；下载后记录实际 SHA256"
                : "  期望 SHA256：" + expected);

            // An existing file is only skipped when it can be proven intact. The old
            // "exists and is not empty" test accepted a half package permanently.
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                var existing = Downloader.VerifyAsync(dest, expected).GetAwaiter().GetResult();
                var sizeOk = info.SizeBytes is not > 0 || new FileInfo(dest).Length == info.SizeBytes.Value;
                if (existing.Ok && sizeOk)
                {
                    Console.WriteLine("  已存在且校验通过，跳过");
                    continue;
                }

                Console.WriteLine("  已存在的文件不可信（" + (existing.Error ?? "大小不符") + "），重新下载");
                try { File.Delete(dest); } catch { }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            var progress = new Progress<DownloadProgress>(p =>
            {
                if ((sw.Elapsed - lastReport).TotalSeconds < 1) return;
                lastReport = sw.Elapsed;
                var mb = p.Received / 1024.0 / 1024;
                var suffix = p.Total > 0
                    ? " / " + (p.Total / 1024.0 / 1024).ToString("N1") + " MB  " + (p.Fraction * 100).ToString("N1") + "%"
                    : string.Empty;
                Console.WriteLine("  " + mb.ToString("N1") + " MB" + suffix);
            });

            var dl = Downloader.DownloadAsync(http, info.DownloadUrl, dest, 16, progress)
                               .GetAwaiter().GetResult();
            sw.Stop();

            Console.WriteLine();
            if (!dl.Ok) { Console.WriteLine("  下载失败：" + dl.Error); failures++; continue; }

            var verify = Downloader.VerifyAsync(dest, expected).GetAwaiter().GetResult();
            if (!verify.Ok)
            {
                Console.WriteLine("  校验失败，已删除文件：" + verify.Error);
                try { File.Delete(dest); } catch { }
                failures++;
                continue;
            }

            var bytes = new FileInfo(dest).Length;
            var rate = bytes / 1024.0 / Math.Max(1, sw.Elapsed.TotalSeconds);
            Console.WriteLine($"  完成 {bytes / 1024.0 / 1024:N1} MB，{rate:N0} KB/s，用时 {sw.Elapsed.TotalSeconds:N0}s");
            Console.WriteLine(expected is null
                ? "  未提供校验值；记录 SHA256 " + verify.Actual
                : "  校验通过（SHA256 " + verify.Actual + "）");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "all downloads finished" : failures + " download(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static string NameOf(string url)
    {
        try { return Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath)); }
        catch { var i = url.LastIndexOf('/'); return i < 0 ? "download.bin" : url[(i + 1)..]; }
    }
}
