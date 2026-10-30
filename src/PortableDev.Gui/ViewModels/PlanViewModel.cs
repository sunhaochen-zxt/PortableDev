using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;

namespace PortableDev.Gui.ViewModels;

/// <summary>One row in the plan diff.</summary>
public sealed class PlanRow : ObservableObject
{
    private bool _selected = true;

    public required string Kind { get; init; }        // junction | path | env | tool
    public required ChangeKind Change { get; init; }
    public required string Target { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }

    /// <summary>Only Add/Update rows can be deselected; removals are always shown.</summary>
    public bool Selectable => Change is ChangeKind.Add or ChangeKind.Update;

    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public bool IsChange => Change != ChangeKind.None;

    public string Glyph => Change switch
    {
        ChangeKind.Add => "+",
        ChangeKind.Update => "~",
        ChangeKind.Remove => "−",
        _ => " ",
    };

    public string ChangeText => Change switch
    {
        ChangeKind.Add => "新增",
        ChangeKind.Update => "更新",
        ChangeKind.Remove => "移除",
        _ => "不变",
    };

    public string Detail => Change switch
    {
        ChangeKind.Update when Before is not null && After is not null => Before + "  →  " + After,
        ChangeKind.Add when After is not null => After,
        ChangeKind.Remove when Before is not null => Before,
        _ => After ?? Before ?? string.Empty,
    };
}

/// <summary>A titled group of plan rows.</summary>
public sealed class PlanGroup : ObservableObject
{
    private string _summary = string.Empty;
    public required string Title { get; init; }
    public ObservableCollection<PlanRow> Rows { get; } = new();
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool HasChanges => Rows.Any(r => r.IsChange);
}

public sealed class PlanViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private string _headline = "正在计算…";
    private string _sub = string.Empty;
    private bool _needsElevation;
    private bool _degraded;
    private bool _hasChanges;
    private Plan? _plan;
    private string _warnings = string.Empty;
    private bool _hasWarnings;

    public PlanViewModel(AppViewModel app)
    {
        _app = app;
        RefreshCommand = new RelayCommand(() => app.RefreshAllAsync());
        ApplyCommand = new RelayCommand(() => app.ApplyWithProgressAsync(), () => HasChanges);
        DegradedCommand = new RelayCommand(() =>
        {
            Degraded = !Degraded;
            _ = app.RefreshAllAsync();
        });
    }

    public ObservableCollection<PlanGroup> Groups { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DegradedCommand { get; }

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Sub { get => _sub; private set => Set(ref _sub, value); }
    public bool HasChanges { get => _hasChanges; private set { if (Set(ref _hasChanges, value)) ApplyCommand.Raise(); } }
    public bool NeedsElevation { get => _needsElevation; private set { if (Set(ref _needsElevation, value)) OnPropertyChanged(nameof(ElevationText)); } }
    public string ElevationText => NeedsElevation ? "会弹 1 次 UAC" : "无需提权";
    public string Warnings { get => _warnings; private set => Set(ref _warnings, value); }
    public bool HasWarnings { get => _hasWarnings; private set => Set(ref _hasWarnings, value); }

    /// <summary>Degraded mode: PATH points at the real stick path instead of C:\Dev.</summary>
    public bool Degraded
    {
        get => _degraded;
        set
        {
            if (Set(ref _degraded, value)) OnPropertyChanged(nameof(DegradedHint));
        }
    }

    public string DegradedHint => Degraded
        ? "降级模式：不建 C:\\Dev，PATH 直接指向 U 盘真实路径。盘符一变就失效。"
        : "标准模式：PATH 写 C:\\Dev，U 盘盘符变化不影响。";

    public void Refresh()
    {
        Groups.Clear();
        _plan = null;

        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Manifest is null)
        {
            Headline = "没有可用的 U 盘";
            Sub = "插好 U 盘后刷新。";
            HasChanges = false;
            HasWarnings = false;
            Warnings = string.Empty;
            return;
        }

        Plan plan;
        try
        {
            plan = PortableEngine.BuildPlan(stick, null, Degraded);
        }
        catch (EngineException ex)
        {
            Headline = "无法生成计划";
            Sub = ex.Message;
            HasChanges = false;
            return;
        }

        _plan = plan;
        var changes = plan.Steps.Count(s => s.Change != ChangeKind.None);
        HasChanges = changes > 0;
        NeedsElevation = plan.NeedsElevation;

        Headline = changes == 0 ? "没有需要改动的地方" : "将执行 " + changes + " 处改动";
        Sub = changes == 0
            ? "本机已经与清单一致，再次应用不会有任何写入。"
            : "以下是逐条差异，勾选的项目才会写入。";

        var junction = new PlanGroup { Title = "稳定根联接" };
        var home = new PlanGroup { Title = "设置随盘走（家目录联接）" };
        var path = new PlanGroup { Title = "PATH" };
        var env = new PlanGroup { Title = "环境变量" };
        var launcher = new PlanGroup { Title = "启动器（按清单生成的 .cmd）" };
        var tools = new PlanGroup { Title = "工具" };

        foreach (var s in plan.Steps)
        {
            var row = new PlanRow
            {
                Kind = s.Kind,
                Change = s.Change,
                Target = s.Target,
                Before = s.Before,
                After = s.After,
            };

            var group = s.Kind switch
            {
                "junction" => junction,
                "home" => home,
                "path" => path,
                "env" => env,
                "launcher" => launcher,
                "tool" => tools,
                // Never fall through to a visible group: an unknown kind used to be counted
                // in the headline yet displayed nowhere, so the window promised changes that
                // did not exist on screen.
                _ => tools,
            };
            group.Rows.Add(row);
        }

        foreach (var g in new[] { junction, home, path, env, launcher, tools })
        {
            var changed = g.Rows.Count(r => r.IsChange);
            g.Summary = changed == 0 ? "无改动" : changed + " 处";
            if (g.Rows.Count > 0) Groups.Add(g);
        }

        var warn = plan.Warnings.ToList();
        HasWarnings = warn.Count > 0;
        Warnings = string.Join(Environment.NewLine, warn.Select(w =>
            (w.Blocker ? "[阻塞] " : "[提示] ") + w.Code + "：" + w.Message));
    }
}
