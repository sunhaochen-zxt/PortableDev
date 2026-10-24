using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PortableDev.Core.Model;
using PortableDev.Core.Update;
using PortableDev.Gui.Engine;

namespace PortableDev.Gui.Views;

/// <summary>Downloads and installs one update, streaming progress into the log.</summary>
public partial class UpgradeWindow : Window
{
    private readonly string _stickRoot;
    private readonly ToolManifest _tool;
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _cts = new();
    private bool _running;

    public UpgradeWindow(string stickRoot, ToolManifest tool, UpdateInfo info)
    {
        InitializeComponent();
        _stickRoot = stickRoot;
        _tool = tool;
        _info = info;
        var toolId = tool.Tool.Id;

        TitleText.Text = "升级 " + toolId;
        SubText.Text = info.Summary + "    来源：" + ShortUrl(info.DownloadUrl);
        StatusText.Text = "尚未开始";
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } };
    }

    private static string ShortUrl(string url) =>
        url.Length <= 70 ? url : url[..67] + "…";

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        BtnStart.IsEnabled = false;
        BtnClose.IsEnabled = false;
        Bar.Value = 0;

        var sw = Stopwatch.StartNew();
        long lastBytes = 0;
        var lastAt = TimeSpan.Zero;

        var progress = new Progress<DownloadProgress>(p =>
        {
            Bar.Value = p.Total > 0 ? p.Fraction * 100 : 0;
            StatusText.Text = p.Total > 0
                ? (p.Received / 1024.0 / 1024).ToString("N1") + " / " + (p.Total / 1024.0 / 1024).ToString("N1") + " MB"
                : (p.Received / 1024.0 / 1024).ToString("N1") + " MB";

            // Speed is sampled, not per-buffer: a number that jumps every 100 ms
            // tells the user nothing.
            var now = sw.Elapsed;
            if ((now - lastAt).TotalMilliseconds >= 500)
            {
                var delta = p.Received - lastBytes;
                var secs = (now - lastAt).TotalSeconds;
                if (secs > 0) SpeedText.Text = (delta / 1024.0 / secs).ToString("N0") + " KB/s";
                lastBytes = p.Received;
                lastAt = now;
            }
        });

        try
        {
            var result = await Upgrader.RunAsync(
                _stickRoot, _tool, _info,
                line => Append(line),
                progress,
                _cts.Token);

            Bar.Value = result.Ok ? 100 : Bar.Value;
            StatusText.Text = result.Ok ? "完成" : "未完成";
            SpeedText.Text = string.Empty;
            Append("");
            Append(result.Message);
        }
        catch (Exception ex)
        {
            Append("出错：" + ex.Message);
            StatusText.Text = "失败";
        }
        finally
        {
            _running = false;
            BtnClose.IsEnabled = true;
            BtnStart.IsEnabled = true;
            BtnStart.Content = "再试一次";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Append(string line)
    {
        // Log lines can arrive from the worker thread doing the extraction.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Append(line)));
            return;
        }

        // Extraction has no byte-level progress, so switch the bar to an
        // indeterminate "something is happening" state instead of leaving it at 0%.
        if (line.Contains("正在解压", StringComparison.Ordinal))
        {
            Bar.IsIndeterminate = true;
            StatusText.Text = "正在解压…";
        }
        else if (line.Contains("更新清单版本号", StringComparison.Ordinal))
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 100;
            StatusText.Text = "正在更新清单…";
        }

        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
    }
}
