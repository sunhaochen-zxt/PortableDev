using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Core;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;

namespace PortableDev.Gui.ViewModels;

/// <summary>
/// 系统状态 page: what fastfetch reports about this machine.
///
/// The report is deliberately NOT parsed. The page shows fastfetch's own output verbatim,
/// so changing the preset, the structure or the tool version is a manifest/directory change
/// and never a GUI change, and the GUI cannot drift from what `fastfetch` prints in a shell.
///
/// The executable is resolved from the manifest first, so the page reports the tool that is
/// actually on the stick, not whatever a PATH lookup happens to find. The stick path comes
/// first, the stable root second (degraded mode / not yet applied), PATH last.
/// </summary>
public sealed class SystemViewModel : ObservableObject
{
    private const string ToolId = "fastfetch";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// fastfetch 2.70 still writes ANSI colour escapes even with <c>--pipe</c>: its
    /// "Colors" module prints raw escape sequences. A TextBox renders them as visible
    /// "[40m [41m ..." junk, so strip CSI and OSC sequences here rather than in the
    /// manifest — a different fastfetch version then cannot reintroduce the noise.
    /// </summary>
    private static readonly Regex AnsiEscape = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)",
        RegexOptions.Compiled);

    private string _report = string.Empty;
    private string _status = "尚未读取";
    private string _source = string.Empty;
    private bool _running;

    public SystemViewModel()
    {
        RefreshCommand = new RelayCommand(RefreshAsync);
        CopyCommand = new RelayCommand(Copy);
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyCommand { get; }

    /// <summary>fastfetch's own output, exactly as printed.</summary>
    public string Report { get => _report; private set { if (Set(ref _report, value)) OnPropertyChanged(nameof(HasReport)); } }

    /// <summary>One line: reading, read at HH:mm:ss, or why it failed.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Which fastfetch.exe the report came from.</summary>
    public string Source { get => _source; private set => Set(ref _source, value); }

    public bool HasReport => Report.Length > 0;

    /// <summary>Started by the shell whenever this page becomes visible.</summary>
    public void Refresh() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (_running) return;
        _running = true;
        Status = "正在读取系统状态…";

        try
        {
            var exe = ResolveTool();
            if (exe is null)
            {
                ApplyText(string.Empty, "找不到 fastfetch：清单里没有 " + ToolId + "，或者盘上的工具目录缺失。");
                return;
            }

            Source = exe;

            var (exit, text, error) = await RunAsync(exe).ConfigureAwait(true);
            if (error is not null)
            {
                ApplyText(string.Empty, error);
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ApplyText(string.Empty, "fastfetch 没有输出（退出码 " + exit + "）。");
                return;
            }

            ApplyText(text, exit == 0
                ? "读取于 " + DateTime.Now.ToString("HH:mm:ss") + " · " + exe
                : "fastfetch 退出码 " + exit + " · " + exe);
        }
        catch (Exception ex)
        {
            ApplyText(string.Empty, "读取失败：" + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Never touches a bound property off the UI thread.</summary>
    private void ApplyText(string report, string status) => UiThread.Post(() =>
    {
        Report = report;
        Status = status;
    });

    private void Copy()
    {
        if (Report.Length == 0) return;

        try
        {
            System.Windows.Clipboard.SetText(Report);
            Status = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            // Clipboard.SetText throws when another process holds the clipboard.
            Status = "复制失败：" + ex.Message;
        }
    }

    private static string? ResolveTool()
    {
        var stick = PortableEngine.Locate();
        if (stick.Root is not null && stick.Manifest is not null &&
            stick.Manifest.Tools.TryGetValue(ToolId, out var tool))
        {
            var rel = PathResolver.ToolRelativeDir(tool).Replace('/', '\\');
            foreach (var root in new[] { stick.Root, stick.Manifest.Root.Stable })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                var candidate = Path.Combine(root, rel, "fastfetch.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "fastfetch.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // A malformed PATH entry must not stop the search.
            }
        }

        return null;
    }

    private static async Task<(int Exit, string Text, string? Error)> RunAsync(string exe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            // --pipe: no ANSI escapes or cursor tricks, the output is bound to a TextBox.
            // --logo none: the window already has a header, and the logo is ASCII art.
            Arguments = "--pipe --logo none",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // fastfetch writes UTF-8. Without this the runtime decodes it with the OEM
            // code page, and every Chinese name in the report becomes mojibake.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (0, string.Empty, "无法启动 fastfetch：" + ex.Message);
        }

        // Drain both pipes concurrently: a sequential ReadToEnd deadlocks as soon as the
        // tool fills the pipe that is not being read yet.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(RunTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (0, string.Empty,
                "fastfetch 超过 " + (int)RunTimeout.TotalSeconds + " 秒没有结束，已终止。");
        }

        var text = ((await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false))).Trim();

        // Colours off: the report is displayed, not printed to a terminal.
        text = AnsiEscape.Replace(text, string.Empty);

        // Trailing spaces per line are left behind by stripped palette sequences, and the
        // "Colors" module leaves empty lines at the end; neither is worth showing.
        text = string.Join("\n", text.Split('\n').Select(line => line.TrimEnd())).TrimEnd();

        return (process.ExitCode, text, null);
    }
}
