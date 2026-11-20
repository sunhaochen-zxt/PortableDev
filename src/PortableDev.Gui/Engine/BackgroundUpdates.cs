using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortableDev.Gui.Engine;

public sealed record UpdateSummary(int Count, string Message, bool Failed);

/// <summary>
/// Periodic update check that runs while the window is in the tray.
///
/// It only ever calls the read-only `update` command (or `update --fetch`, which downloads
/// but installs nothing): upgrading tools changes the manifest and PATH, and that stays a
/// deliberate action on the Tools page.
/// </summary>
public sealed class BackgroundUpdates : IDisposable
{
    private readonly Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackgroundUpdates(TimeSpan interval)
    {
        // First pass after the window has settled, then at the requested interval.
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(20), interval);
    }

    public event Action<UpdateSummary>? Checked;

    public async Task<UpdateSummary> CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return new UpdateSummary(0, "上一次检查还没结束", true);

        UpdateSummary summary;
        try
        {
            var stick = PortableEngine.Locate();
            if (!stick.Found || stick.Root is null)
            {
                summary = new UpdateSummary(0, "没有检测到 U 盘", true);
            }
            else
            {
                var result = await CliBridge.RunAsync(stick.Root, new[] { "update", "--json" });
                if (!result.Ok)
                {
                    summary = new UpdateSummary(0, "检查更新失败：" + FirstLine(result.Error), true);
                }
                else
                {
                    summary = Parse(result.Output);
                }
            }
        }
        catch (Exception ex)
        {
            summary = new UpdateSummary(0, "检查更新出错：" + ex.Message, true);
        }
        finally
        {
            _gate.Release();
        }

        Checked?.Invoke(summary);
        return summary;
    }

    /// <summary>Download the pending updates; installing them is left to the Tools page.</summary>
    public async Task<CliResult> DownloadAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Root is null)
            return new CliResult { ExitCode = 4, Error = "没有检测到 U 盘" };

        return await CliBridge.RunAsync(stick.Root, new[] { "update", "--fetch" }, onLine, ct);
    }

    private static UpdateSummary Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new UpdateSummary(0, "更新结果格式无法解析", true);

            var count = 0;
            var names = new List<string>();

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var status = row.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (!string.Equals(status, "update", StringComparison.OrdinalIgnoreCase)) continue;

                count++;
                if (names.Count < 5 && row.TryGetProperty("tool", out var t))
                    names.Add(t.GetString() ?? "?");
            }

            return count == 0
                ? new UpdateSummary(0, "全部工具都是最新版本", false)
                : new UpdateSummary(count, count + " 个工具可更新：" + string.Join("、", names)
                    + (count > names.Count ? "…" : string.Empty), false);
        }
        catch (Exception ex)
        {
            return new UpdateSummary(0, "更新结果格式无法解析：" + ex.Message, true);
        }
    }

    private static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Trim();
        var newline = line.IndexOfAny(new[] { '\r', '\n' });
        return newline < 0 ? line : line[..newline];
    }

    public void Dispose() => _timer.Dispose();
}
