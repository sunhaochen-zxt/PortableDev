using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;

namespace PortableDev.Gui.ViewModels;

/// <summary>One line in the "machine state" card.</summary>
public sealed class StatusRow : ObservableObject
{
    private bool _ok;
    private string _detail = string.Empty;

    public StatusRow(string name) => Name = name;

    public string Name { get; }

    public bool Ok { get => _ok; set => Set(ref _ok, value); }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>"OK" / "!" marker for the row.</summary>
    public string Mark => Ok ? "OK" : "!";
}

public sealed class DashboardViewModel : ObservableObject
{
    private readonly AppViewModel _app;

    private string _stickRoot = "—";
    private string _stickMeta = string.Empty;
    private string _stableRoot = "—";
    private string _junction = "—";
    private bool _junctionOk;
    private string _pathCount = "—";
    private string _envCount = "—";
    private string _pending = "—";
    private bool _pendingChanges;
    private bool _hasStick;
    private bool _busy;
    private string _headline = "正在检测…";
    private string _subline = string.Empty;
    private string _toolSummary = string.Empty;

    public DashboardViewModel(AppViewModel app)
    {
        _app = app;
        RefreshCommand = new RelayCommand(() => app.RefreshAllAsync());
        ApplyCommand = new RelayCommand(ApplyAsync, () => CanApply);
        VerifyCommand = new RelayCommand(() => app.GoVerifyAsync());
    }

    public ObservableCollection<StatusRow> Rows { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand VerifyCommand { get; }

    public string StickRoot { get => _stickRoot; private set => Set(ref _stickRoot, value); }
    public string StickMeta { get => _stickMeta; private set => Set(ref _stickMeta, value); }
    public string StableRoot { get => _stableRoot; private set => Set(ref _stableRoot, value); }
    public string Junction { get => _junction; private set => Set(ref _junction, value); }
    public bool JunctionOk { get => _junctionOk; private set { if (Set(ref _junctionOk, value)) OnPropertyChanged(nameof(JunctionGlyph)); } }
    public string JunctionGlyph => JunctionOk ? "✓" : "⚠";
    public string PathCount { get => _pathCount; private set => Set(ref _pathCount, value); }
    public string EnvCount { get => _envCount; private set => Set(ref _envCount, value); }
    public string Pending { get => _pending; private set => Set(ref _pending, value); }
    public string ToolSummary { get => _toolSummary; private set => Set(ref _toolSummary, value); }
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Subline { get => _subline; private set => Set(ref _subline, value); }
    public bool HasStick { get => _hasStick; private set { if (Set(ref _hasStick, value)) OnPropertyChanged(nameof(CanApply)); } }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    /// <summary>True when the computed plan still has work to do on this machine.</summary>
    public bool PendingChanges
    {
        get => _pendingChanges;
        private set
        {
            if (Set(ref _pendingChanges, value)) OnPropertyChanged(nameof(CanApply));
        }
    }

    public bool CanApply => HasStick && (PendingChanges || !JunctionOk) && !Busy;

    /// <summary>Recompute everything from the engine. Read-only; safe to call often.</summary>
    public void Refresh()
    {
        Busy = true;
        try
        {
            var stick = PortableEngine.Locate();
            HasStick = stick.Found;

            if (!stick.Found)
            {
                Headline = stick.CandidateCount == 0 ? "没有检测到 U 盘" : "检测到多个候选 U 盘";

                if (stick.CandidateCount == 0)
                {
                    Subline = "插好 U 盘后点「刷新」。程序按 .portabledev-root 标记文件认盘，不按盘符。";
                }
                else
                {
                    // "multiple sticks found" on its own is not actionable: name them.
                    var list = stick.Candidates.Count > 0
                        ? " 候选：" + string.Join("、", stick.Candidates)
                        : string.Empty;
                    Subline = (stick.Error ?? "无法确定用哪一个。") + list +
                              " 只插一个 U 盘，或用 PortableDev.exe --root <路径> 指定。";
                }
                StickRoot = "—";
                StickMeta = string.Empty;
                StableRoot = "—";
                Junction = "—";
                JunctionOk = false;
                PathCount = "—";
                EnvCount = "—";
                Pending = "—";
                ToolSummary = string.Empty;
                Rows.Clear();
                return;
            }

            StickRoot = stick.Root!;
            StickMeta = string.Format("{0} · {1} · 剩余 {2} GB / {3} GB",
                stick.Label ?? "(无卷标)",
                stick.FileSystem ?? "?",
                (stick.FreeBytes / 1024.0 / 1024 / 1024).ToString("N1"),
                (stick.TotalBytes / 1024.0 / 1024 / 1024).ToString("N1"));

            if (stick.Manifest is null)
            {
                Headline = "清单读取失败";
                Subline = stick.ManifestError ?? "manifest 目录缺失或格式错误";
                Rows.Clear();
                return;
            }

            var manifest = stick.Manifest;
            StableRoot = manifest.Root.Stable;

            var (jOk, jDetail) = PortableEngine.JunctionStatus(manifest.Root.Stable, stick.Root!);
            JunctionOk = jOk;
            Junction = jDetail;

            var plan = PortableEngine.BuildPlan(stick);
            var state = PortableEngine.ReadState(stick.Root!, PortableEngine.Normalize(manifest.Root.Stable));

            var managed = PathEditor.ManagedEntries(state.UserPathRaw, manifest.Root.Stable);
            var foreign = PathEditor.ForeignEntries(state.UserPathRaw, manifest.Root.Stable);
            PathCount = managed.Count + " 条受管 / " + foreign.Count + " 条保留";

            var desiredEnv = plan.EnvToSet.Count;
            var pendingEnv = plan.EnvToSet.Count(kv =>
                !state.CurrentEnv.TryGetValue(kv.Key, out var cur) ||
                !string.Equals(cur, kv.Value, StringComparison.OrdinalIgnoreCase));
            EnvCount = plan.EnvToSet.Count == 0
                ? "无"
                : (desiredEnv - pendingEnv) + " / " + desiredEnv + " 已就位";

            var changes = plan.Steps.Count(s => s.Change != ChangeKind.None);
            PendingChanges = changes > 0;
            Pending = changes == 0 ? "无（已同步）" : changes + " 处待应用";

            var enabled = manifest.Tools.Values.Count(t => t.Tool.Enabled);
            var missing = manifest.Tools.Values
                .Where(t => t.Tool.Enabled)
                .Where(t => !Directory.Exists(Path.Combine(stick.Root!,
                    PathResolver.ToolRelativeDir(t).Replace('/', '\\'))))
                .Select(t => t.Tool.Id)
                .ToList();
            ToolSummary = missing.Count == 0
                ? enabled + " 个工具全部就位"
                : enabled + " 个工具，缺失：" + string.Join("、", missing);

            Headline = changes == 0 ? "环境已就绪" : "环境尚未同步";
            Subline = changes == 0
                ? "本机与该 U 盘清单完全一致，可以直接开始工作。"
                : "有 " + changes + " 处需要写入本机，点「立即应用」完成。";

            BuildRows(manifest, state, plan, jOk, jDetail);
            ApplyCommand.Raise();
        }
        finally
        {
            Busy = false;
        }
    }

    private void BuildRows(RootManifest manifest, MachineState state, Plan plan, bool jOk, string jDetail)
    {
        Rows.Clear();

        var stick = PortableEngine.Locate();
        Rows.Add(new StatusRow("U 盘") { Ok = true, Detail = stick.Root ?? "—" });

        Rows.Add(new StatusRow("联接 " + manifest.Root.Stable) { Ok = jOk, Detail = jDetail });

        var kindOk = state.UserPathKind is "ExpandString" or "String";
        Rows.Add(new StatusRow("PATH 类型") { Ok = kindOk, Detail = state.UserPathKind + (kindOk ? "（%VAR% 不会被写死）" : "（已降级，需修复）") });

        var managed = PathEditor.ManagedEntries(state.UserPathRaw, manifest.Root.Stable);
        var desired = new Planner(manifest, new PathResolver(manifest.Root.Stable, state.StickRoot))
            .Build(state, new PlannerOptions { DirectoryExists = Directory.Exists })
            .Steps.Count(s => s.Kind == "path" && s.Change != ChangeKind.None);
        Rows.Add(new StatusRow("PATH 条目") { Ok = managed.Count > 0 && desired == 0, Detail = managed.Count + " 条受管" + (desired > 0 ? "，" + desired + " 条待更新" : "") });

        var dup = PathEditor.Split(state.UserPathRaw)
            .GroupBy(e => e.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Rows.Add(new StatusRow("PATH 重复") { Ok = dup.Count == 0, Detail = dup.Count == 0 ? "无" : string.Join(" | ", dup) });

        var envPending = plan.EnvToSet.Count(kv =>
            !state.CurrentEnv.TryGetValue(kv.Key, out var cur) ||
            !string.Equals(cur, kv.Value, StringComparison.OrdinalIgnoreCase));
        Rows.Add(new StatusRow("环境变量") { Ok = envPending == 0, Detail = plan.EnvToSet.Count - envPending + " / " + plan.EnvToSet.Count + " 已就位" });

        var blockers = plan.Warnings.Where(w => w.Blocker).ToList();
        Rows.Add(new StatusRow("阻塞项") { Ok = blockers.Count == 0, Detail = blockers.Count == 0 ? "无" : string.Join("；", blockers.Select(b => b.Message)) });

        Rows.Add(new StatusRow("管理员权限") { Ok = true, Detail = state.IsElevated ? "当前已提权" : "未提权（建联接时会弹一次 UAC）" });
    }

    private async Task ApplyAsync()
    {
        await _app.ApplyWithProgressAsync();
    }
}
