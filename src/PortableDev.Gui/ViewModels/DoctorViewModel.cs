using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;
using PortableDev.Win;

namespace PortableDev.Gui.ViewModels;

public sealed class DoctorRow : ObservableObject
{
    private bool _ok;
    private string _detail = string.Empty;

    public required string Name { get; init; }
    public required string Title { get; init; }
    public bool Ok { get => _ok; set { if (Set(ref _ok, value)) OnPropertyChanged(nameof(Glyph)); } }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Glyph => Ok ? "✓" : "!";
}

public sealed class DoctorViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private string _summary = string.Empty;
    private bool _allOk;

    public DoctorViewModel(AppViewModel app)
    {
        _app = app;
        RunCommand = new RelayCommand(() => app.RefreshAllAsync());
        CleanCommand = new RelayCommand(() => app.CleanAsync());
    }

    public ObservableCollection<DoctorRow> Rows { get; } = new();
    public RelayCommand RunCommand { get; }
    public RelayCommand CleanCommand { get; }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool AllOk { get => _allOk; private set => Set(ref _allOk, value); }

    /// <summary>Mirrors Doctor.Run() so the GUI and 'PortableDev doctor' agree.</summary>
    public void Refresh()
    {
        Rows.Clear();
        var stick = PortableEngine.Locate();

        Add("stick", "U 盘", stick.Found,
            stick.Root ?? "未找到（" + stick.CandidateCount + " 个候选）");

        if (!stick.Found)
        {
            Finish();
            return;
        }

        Add("volume-label", "卷标", true, stick.Label ?? "(无)");

        var freeGb = stick.FreeBytes / 1024.0 / 1024 / 1024;
        var totalGb = stick.TotalBytes / 1024.0 / 1024 / 1024;
        Add("free-space", "剩余空间", stick.FreeBytes > 2L * 1024 * 1024 * 1024,
            freeGb.ToString("N1") + " GB / " + totalGb.ToString("N1") + " GB");

        if (stick.Manifest is null)
        {
            Add("manifest", "清单", false, stick.ManifestError ?? "不可用");
            Finish();
            return;
        }

        var manifest = stick.Manifest;
        Add("manifest", "清单", true, manifest.Tools.Count + " 个工具，" + manifest.Env.Count + " 个环境变量");

        var stable = manifest.Root.Stable;
        var (jOk, jDetail) = PortableEngine.JunctionStatus(stable, stick.Root!);
        Add("junction", "联接 " + stable, jOk, jDetail);

        var state = PortableEngine.ReadState(stick.Root!, PortableEngine.Normalize(stable));
        Add("path-kind", "PATH 类型", state.UserPathKind is "ExpandString" or "String",
            state.UserPathKind + (state.UserPathKind == "ExpandString" ? "（安全）" : "（已降级）"));

        var managed = PathEditor.ManagedEntries(state.UserPathRaw, stable);
        Add("path-managed", "PATH 受管条目", managed.Count > 0, managed.Count + " 条在 " + stable + " 下");

        var dup = PathEditor.Split(state.UserPathRaw)
            .GroupBy(e => e.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Add("path-duplicates", "PATH 重复项", dup.Count == 0, dup.Count == 0 ? "无" : string.Join(" | ", dup));

        Add("filesystem-ntfs", "文件系统", string.Equals(stick.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase),
            (stick.FileSystem ?? "?") + (string.Equals(stick.FileSystem, "exFAT", StringComparison.OrdinalIgnoreCase)
                ? "（不支持符号链接；PATH 每次会话重建，属预期）" : string.Empty));

        var missing = manifest.Tools.Values
            .Where(t => t.Tool.Enabled)
            .Where(t => !Directory.Exists(Path.Combine(stick.Root!, PathResolver.ToolRelativeDir(t).Replace('/', '\\'))))
            .Select(t => t.Tool.Id).ToList();
        Add("tools", "工具目录", missing.Count == 0, missing.Count == 0 ? "全部存在" : "缺失：" + string.Join("、", missing));

        Add("elevated", "管理员权限", true,
            state.IsElevated ? "当前已提权" : "未提权（建联接时会弹一次 UAC）");

        Add("cli", "命令行本体", CliBridge.Available(stick.Root),
            CliBridge.Available(stick.Root) ? CliBridge.Locate(stick.Root) : "找不到 PortableDev.exe，apply/clean 不可用");

        Finish();
    }

    private void Add(string name, string title, bool ok, string detail) =>
        Rows.Add(new DoctorRow { Name = name, Title = title, Ok = ok, Detail = detail ?? string.Empty });

    private void Finish()
    {
        var bad = Rows.Count(r => !r.Ok);
        AllOk = bad == 0;
        Summary = bad == 0 ? "全部检查通过" : bad + " 项需要处理";
    }
}
