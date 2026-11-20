using System;
using System.ComponentModel;
using System.Windows;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;
using PortableDev.Gui.ViewModels;

namespace PortableDev.Gui;

public partial class MainWindow : Window
{
    private readonly AppViewModel _vm;
    private TrayHost? _tray;
    private BackgroundUpdates? _updates;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new AppViewModel();
        DataContext = _vm;

        // A launcher that starts us from a console-less parent can hand over a hidden
        // STARTUPINFO, leaving the window minimised off-screen. Force a normal state
        // on first load; this is cheap and harmless when we were shown normally.
        Loaded += async (_, _) =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            await _vm.RefreshAllAsync();
            StartTray();
        };

        Closing += OnClosing;
    }

    /// <summary>
    /// Tray + background update checks. Failure here must never prevent the window from
    /// working: the app without a tray is still the app.
    /// </summary>
    private void StartTray()
    {
        if (_tray is not null) return;

        try
        {
            _tray = new TrayHost();
            _tray.ShowRequested += ShowFromTray;
            _tray.ExitRequested += ExitFromTray;
            _tray.CheckUpdatesRequested += CheckUpdatesFromTray;
            _tray.DownloadUpdatesRequested += DownloadUpdatesFromTray;
            _tray.SetTooltip("PortableDev · 后台运行中");

            _updates = new BackgroundUpdates(TimeSpan.FromHours(4));
            _updates.Checked += summary => UiThread.Post(() =>
            {
                _tray?.SetTooltip("PortableDev · " + summary.Message);
                if (summary.Count > 0) _tray?.Notify("有工具可更新", summary.Message);
            });
        }
        catch
        {
            _tray = null;
            _updates = null;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // The first close hides the window; quitting is an explicit tray-menu action, so
        // an accidental Alt+F4 does not kill a running download.
        if (_exiting || _tray is null) return;

        e.Cancel = true;
        Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.Notify("PortableDev 仍在运行",
                "关掉窗口只是收进托盘；后台每 4 小时检查一次更新。托盘右键可以退出。");
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _vm.RefreshInBackground();
    }

    private async void CheckUpdatesFromTray()
    {
        if (_updates is null) return;

        _tray?.Notify("正在检查更新", "查询每个工具的上游版本…");
        var summary = await _updates.CheckAsync();
        _tray?.Notify("检查更新", summary.Message,
            summary.Failed ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
    }

    private async void DownloadUpdatesFromTray()
    {
        if (_updates is null) return;

        _tray?.Notify("开始下载", "在后台下载可用更新；完成后会提示，安装仍需在工具页确认。");
        try
        {
            var result = await _updates.DownloadAsync(line => _tray?.SetTooltip("PortableDev · " + line));
            _tray?.Notify("下载结束",
                result.Ok ? "更新包已下载。回到「工具」页应用升级即可。" : "下载失败：" + result.Describe(),
                result.Ok ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            _tray?.Notify("下载出错", ex.Message, System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private void ExitFromTray()
    {
        _exiting = true;
        try { _updates?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        System.Windows.Application.Current?.Shutdown();
    }
}
