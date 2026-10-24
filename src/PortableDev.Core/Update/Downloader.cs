using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PortableDev.Core.Update;

public sealed record DownloadProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? (double)Received / Total : 0;
}

/// <summary>
/// Downloads a file, splitting it across several connections.
///
/// This is not premature optimisation: the GitHub release CDN throttles *per
/// connection* (measured here at ~43 KB/s single, ~350 KB/s at 8, near-linear up
/// to 24), so a single-stream download of llvm-mingw takes about an hour while a
/// parallel one takes a few minutes. Parts are kept on disk, so an interrupted
/// download resumes instead of starting over.
/// </summary>
public static class Downloader
{
    private const long MinChunkedSize = 2 * 1024 * 1024;
    private const long MinChunkSize = 512 * 1024;

    public static async Task<(bool Ok, string Error)> DownloadAsync(
        HttpClient http,
        string url,
        string destPath,
        int connections,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(destPath)!;
            Directory.CreateDirectory(dir);

            var (total, acceptsRanges) = await ProbeAsync(http, url, ct);

            if (total < MinChunkedSize || !acceptsRanges)
                return await SingleStreamAsync(http, url, destPath, total, progress, ct);

            return await ChunkedAsync(http, url, destPath, total, connections, progress, ct);
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(long Total, bool Ranges)> ProbeAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode) return (0, false);

        var len = res.Content.Headers.ContentLength ?? 0;
        var ranges = res.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
        return (len, ranges);
    }

    private static async Task<(bool, string)> SingleStreamAsync(
        HttpClient http, string url, string destPath, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        if (total <= 0) total = res.Content.Headers.ContentLength ?? 0;

        // Write next to the destination and rename when complete: a dropped connection must
        // not leave a truncated file that the "already downloaded" check then accepts.
        var assembling = destPath + ".assembling";
        var completed = false;
        try
        {
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            long received = 0;

            await using (var dst = File.Create(assembling))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            if (total > 0 && received != total)
                return (false, $"下载不完整：{received} / {total} 字节");

            File.Move(assembling, destPath, overwrite: true);
            completed = true;
            return (true, string.Empty);
        }
        finally
        {
            if (!completed) TryDelete(assembling);
        }
    }

    private static async Task<(bool, string)> ChunkedAsync(
        HttpClient http, string url, string destPath, long total, int connections,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var partsDir = destPath + ".parts";
        Directory.CreateDirectory(partsDir);

        var wanted = Math.Clamp(connections, 1, 32);
        var bySize = (int)Math.Max(1, total / MinChunkSize);
        var count = Math.Max(1, Math.Min(wanted, bySize));

        var chunk = (total + count - 1) / count;
        var ranges = Enumerable.Range(0, count)
            .Select(i =>
            {
                var start = (long)i * chunk;
                var end = Math.Min(total - 1, start + chunk - 1);
                return (Index: i, Start: start, End: end, Length: end - start + 1);
            })
            .Where(r => r.Length > 0)
            .ToList();

        // Per-chunk byte counters. Reporting "my chunk's progress" would make the
        // number jump backwards as chunks overtake each other, so every update
        // reports the sum of all chunks instead.
        var written = new long[ranges.Count];
        var gate = new object();

        // Count what an earlier run already finished, so resume is visible.
        foreach (var r in ranges)
        {
            var p = PartPath(partsDir, r.Index);
            if (File.Exists(p) && new FileInfo(p).Length == r.Length) written[r.Index] = r.Length;
        }

        var tasks = ranges.Select(async r =>
        {
            var partPath = PartPath(partsDir, r.Index);
            if (written[r.Index] == r.Length)
                return;

            await FetchRangeAsync(http, url, r.Start, r.End, partPath, ct,
                bytes =>
                {
                    long sum;
                    lock (gate)
                    {
                        written[r.Index] = bytes;
                        sum = written.Sum();
                    }
                    progress?.Report(new DownloadProgress(Math.Min(total, sum), total));
                });
        });

        await Task.WhenAll(tasks);

        // Every part must be exactly the range it claims before anything is joined. A
        // short part used to be concatenated into a truncated file, whose completeness was
        // then never questioned: the "exists and is not empty" branch skipped it forever.
        foreach (var r in ranges)
        {
            var partPath = PartPath(partsDir, r.Index);
            if (!File.Exists(partPath))
                return (false, $"分块 {r.Index} 缺失（预期 {r.Length} 字节）；已保留 .parts 供续传");
            if (new FileInfo(partPath).Length != r.Length)
                return (false,
                    $"分块 {r.Index} 长度 {new FileInfo(partPath).Length}，预期 {r.Length}；已保留 .parts 供续传");
        }

        // Concatenate in order into a sibling name, then rename: destPath is either absent
        // or a complete package, never a half-written one.
        var assembling = destPath + ".assembling";
        var assembled = false;
        try
        {
            await using (var outStream = File.Create(assembling))
            {
                foreach (var r in ranges)
                {
                    var partPath = PartPath(partsDir, r.Index);
                    await using var part = File.OpenRead(partPath);
                    await part.CopyToAsync(outStream, ct);
                }
            }

            File.Move(assembling, destPath, overwrite: true);
            assembled = true;
        }
        finally
        {
            if (!assembled) TryDelete(assembling);
        }

        try { Directory.Delete(partsDir, true); } catch { }
        progress?.Report(new DownloadProgress(total, total));
        return (true, string.Empty);
    }

    private static string PartPath(string dir, int index) => Path.Combine(dir, "part" + index.ToString("D3"));

    private static async Task FetchRangeAsync(
        HttpClient http, string url, long start, long end, string partPath,
        CancellationToken ct, Action<long> onWritten)
    {
        const int attempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(start, end);

                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();

                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(partPath);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    onWritten(written);
                }
                return;
            }
            catch (Exception) when (attempt < attempts)
            {
                // A dropped connection mid-chunk is normal on flaky links; retry the
                // same range instead of throwing away the whole download.
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Check a downloaded file against an expected SHA256.
    ///
    /// <paramref name="expected"/> null means the source published no checksum: Ok is true
    /// but the actual hash is still returned so the caller can record it. This is one
    /// implementation shared by the CLI (allow or not) and the GUI (which writes the hash
    /// into the manifest), so the two can never disagree about what "verified" means.
    /// </summary>
    public static async Task<(bool Ok, string? Actual, string? Error)> VerifyAsync(
        string path, string? expected, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return (false, null, "文件不存在：" + path);

        var actual = await Sha256Async(path, ct);
        if (actual is null) return (false, null, "无法计算 SHA256：" + path);

        if (string.IsNullOrWhiteSpace(expected)) return (true, actual, null);

        var want = expected.Trim();
        return string.Equals(actual, want, StringComparison.OrdinalIgnoreCase)
            ? (true, actual, null)
            : (false, actual, "SHA256 不匹配：期望 " + want.ToLowerInvariant() + "，实际 " + actual);
    }

    public static async Task<string?> Sha256Async(string path, CancellationToken ct = default)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hash = await SHA256.Create().ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
