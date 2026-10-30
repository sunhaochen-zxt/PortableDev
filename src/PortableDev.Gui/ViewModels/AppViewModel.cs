using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;

namespace PortableDev.Gui.ViewModels;

/// <summary>Root view model. Owns the pages and serialises every page refresh.</summary>
public sealed class AppViewModel : ObservableObject
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _page;
    private string _footer = "就绪";
    private string _stickLabel = string.Empty;
    private bool _hasStick;

    public AppViewModel()
    {
        Dashboard = new DashboardViewModel(this);
        Plan = new PlanViewModel(this);
        Tools = new ToolsViewModel(this);
        Doctor = new DoctorViewModel(this);
        Progress = new ProgressViewModel(this);
        System = new SystemViewModel();

        NavCommand = new RelayCommand<string>(p =>
        {
            if (int.TryParse(p, out var i)) Page = i;
        });

        // A command that throws used to disappear into "async void" and leave the window
        // silent. The footer is the one place always visible, so errors land there.
        RelayCommand.UnhandledError += ex => UiThread.Post(() => Footer = "出错：" + ex.Message);
    }

    public DashboardViewModel Dashboard { get; }
    public PlanViewModel Plan { get; }
    public ToolsViewModel Tools { get; }
    public DoctorViewModel Doctor { get; }
    public ProgressViewModel Progress { get; }
    public SystemViewModel System { get; }

    public RelayCommand<string> NavCommand { get; }

    /// <summary>0 dashboard · 1 tools · 2 plan · 3 doctor · 4 progress · 5 system</summary>
    public int Page
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                OnPropertyChanged(nameof(IsDashboard));
                OnPropertyChanged(nameof(IsTools));
                OnPropertyChanged(nameof(IsPlan));
                OnPropertyChanged(nameof(IsDoctor));
                OnPropertyChanged(nameof(IsProgress));
                OnPropertyChanged(nameof(IsSystem));
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(PageHint));
                _ = RefreshPageAsync();
            }
        }
    }

    public bool IsDashboard => Page == 0;
    public bool IsTools => Page == 1;
    public bool IsPlan => Page == 2;
    public bool IsDoctor => Page == 3;
    public bool IsProgress => Page == 4;
    public bool IsSystem => Page == 5;

    public string PageTitle => Page switch
    {
        0 => "概览",
        1 => "工具",
        2 => "变更预览",
        3 => "体检",
        4 => "进度与日志",
        5 => "系统状态",
        _ => string.Empty,
    };

    public string PageHint => Page switch
    {
        0 => "U 盘与本机环境是否一致，一眼看完。",
        1 => "清单里声明的每个工具、版本与可用性。",
        2 => "写盘之前先看清每一处差异。plan 本身零副作用。",
        3 => "按 CLI doctor 的同一套规则逐项检查。",
        4 => "apply / clean 的实时输出。",
        5 => "本机硬件与系统信息，由盘上的 fastfetch 实时读取。",
        _ => string.Empty,
    };

    public string Footer { get => _footer; private set => Set(ref _footer, value); }
    public string StickLabel { get => _stickLabel; private set => Set(ref _stickLabel, value); }
    public bool HasStick { get => _hasStick; private set => Set(ref _hasStick, value); }

    /// <summary>
    /// Refresh whichever page is visible.
    ///
    /// Every page model owns ObservableCollections, and WPF collection views are
    /// thread-affine: mutating them off the dispatcher thread throws
    /// NotSupportedException. The reads themselves are cheap (a registry query and
    /// a handful of Directory.Exists calls), so they run on the UI thread rather
    /// than being split into a snapshot/apply pair for no measurable gain.
    /// </summary>
    public Task RefreshPageAsync()
    {
        if (!_gate.Wait(0)) return Task.CompletedTask;
        try
        {
            return OnUi(() =>
            {
                try
                {
                    Footer = "正在读取…";
                    switch (Page)
                    {
                        case 0: Dashboard.Refresh(); break;
                        case 1: Tools.Refresh(); break;
                        case 2: Plan.Refresh(); break;
                        case 3: Doctor.Refresh(); break;
                        case 5: System.Refresh(); break;
                    }

                    SyncStickLabel();
                    Footer = "就绪 · " + DateTime.Now.ToString("HH:mm:ss");
                }
                catch (Exception ex)
                {
                    Footer = "出错：" + ex.Message;
                }
                finally
                {
                    _gate.Release();
                }
            });
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>Run an action on the UI thread, whether or not we are already on it.</summary>
    private static Task OnUi(Action action) => UiThread.InvokeAsync(action);

    private void SyncStickLabel()
    {
        var stick = PortableEngine.Locate();
        HasStick = stick.Found;
        StickLabel = stick.Found
            ? (stick.Root ?? string.Empty) +
              (string.IsNullOrEmpty(stick.Label) ? string.Empty : "  (" + stick.Label + ")")
            : "未检测到 U 盘";
    }

    /// <summary>Background refresh: never blocks the UI thread on exFAT directory scans.</summary>
    public void RefreshInBackground() => _ = RefreshPageAsync();

    /// <summary>Refresh every page model. Always marshalled onto the UI thread.</summary>
    public Task RefreshAllAsync() => OnUi(() =>
    {
        try
        {
            Dashboard.Refresh();
            Plan.Refresh();
            Tools.Refresh();
            Doctor.Refresh();
            SyncStickLabel();
            Footer = "就绪 · " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            Footer = "出错：" + ex.Message;
        }
    });

    public Task GoVerifyAsync()
    {
        Page = 1;
        Tools.VerifyAllCommand.Execute(null);
        return Task.CompletedTask;
    }

    /// <summary>Run apply through the CLI and stream its output into the progress page.</summary>
    public async Task ApplyWithProgressAsync()
    {
        var stick = PortableEngine.Locate();
        if (!stick.Found)
        {
            Progress.Reset("无法应用", 0);
            Progress.Append("没有找到 U 盘。");
            Progress.Finish(new CliResult { ExitCode = 4 });
            Page = 4;
            return;
        }

        var args = new List<string> { "apply", "--yes" };
        if (Plan.Degraded) args.Add("--no-junction");
        await RunCliAsync("正在应用变更", 4, args, stick.Root);
    }

    public async Task CleanAsync()
    {
        var stick = PortableEngine.Locate();
        if (!stick.Found) return;
        await RunCliAsync("正在还原本机环境", 3, new[] { "clean", "--yes" }, stick.Root);
    }

    private async Task RunCliAsync(string title, int steps, IEnumerable<string> args, string? stickRoot)
    {
        Page = 4;
        Progress.Reset(title, steps);
        Footer = "运行中…";

        var result = await CliBridge.RunAsync(stickRoot, args, line =>
        {
            Progress.Append(line);
        });

        Progress.Finish(result);

        if (string.IsNullOrWhiteSpace(string.Join("", Progress.Lines)) && !string.IsNullOrWhiteSpace(result.Error))
            Progress.Append(result.Error);

        await RefreshAllAsync();
    }
}
