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

/// <summary>One tool row: identity, location, and (after a verify run) its check result.</summary>
public sealed class ToolRow : ObservableObject
{
    private string _status = "未检查";
    private bool _ok = true;
    private bool _checked;
    private string _output = string.Empty;

    public required string Id { get; init; }
    public required string Version { get; init; }
    public string? Title { get; init; }
    public required string Path { get; init; }
    public required string Notes { get; init; }
    public bool Present { get; init; }

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Ok
    {
        get => _ok;
        set
        {
            if (Set(ref _ok, value)) NotifyDerived();
        }
    }
    public bool Checked
    {
        get => _checked;
        set
        {
            if (Set(ref _checked, value)) NotifyDerived();
        }
    }
    public string Output
    {
        get => _output;
        set
        {
            if (Set(ref _output, value)) NotifyDerived();
        }
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(HasReason));
    }

    public string Glyph => !Checked ? "•" : Ok ? "✓" : "✗";

    /// <summary>
    /// Why the check failed, on one line, shown right under the tool name.
    ///
    /// The CLI already explains the cause (expected version vs. actual output); hiding it
    /// behind a tooltip made every failure look like the same unexplained red "失败".
    /// </summary>
    public string Reason
    {
        get
        {
            if (!Checked || Ok || string.IsNullOrWhiteSpace(_output)) return string.Empty;
            var one = _output.Replace("\r", string.Empty).Replace("\n", "   ").Trim();
            return one.Length > 160 ? one[..160] + "…" : one;
        }
    }

    public bool HasReason => Reason.Length > 0;

    /// <summary>True when the declared version directory is missing on the stick.</summary>
    public bool Missing => !Present;
    public string InstallState => Present ? "已安装" : "未安装";

    // ---- upstream update state ----
    private string? _updateTo;
    private bool _checkedUpstream;
    private bool _hasUpdate;
    private PortableDev.Core.Model.UpdateInfo? _updateInfo;

    /// <summary>Newest version found upstream, when it is newer than the installed one.</summary>
    public string? UpdateTo
    {
        get => _updateTo;
        private set
        {
            if (Set(ref _updateTo, value)) OnPropertyChanged(nameof(UpdateText));
        }
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        private set
        {
            if (Set(ref _hasUpdate, value)) OnPropertyChanged(nameof(UpdateText));
        }
    }

    public bool CheckedUpstream
    {
        get => _checkedUpstream;
        private set
        {
            if (Set(ref _checkedUpstream, value)) OnPropertyChanged(nameof(UpdateText));
        }
    }

    private bool _noSource;

    /// <summary>True when the manifest declares no [update] source for this tool.</summary>
    public bool NoUpdateSource
    {
        get => _noSource;
        private set
        {
            if (Set(ref _noSource, value)) OnPropertyChanged(nameof(UpdateText));
        }
    }

    public string UpdateText => NoUpdateSource
        ? string.Empty
        : !CheckedUpstream
            ? string.Empty
            : HasUpdate ? "可升级 " + UpdateTo : "已是最新";

    /// <summary>Mark a tool that has no upstream to ask.</summary>
    public void MarkNoSource()
    {
        NoUpdateSource = true;
        CheckedUpstream = false;
        HasUpdate = false;
        _updateInfo = null;
    }

    public PortableDev.Core.Model.UpdateInfo? UpdateInfo => _updateInfo;

    public void ApplyUpdateInfo(PortableDev.Core.Model.UpdateInfo? info)
    {
        _updateInfo = info;
        CheckedUpstream = true;
        HasUpdate = info is not null && info.IsNewer;
        UpdateTo = HasUpdate ? info!.AvailableVersion : null;
    }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public string Display => string.IsNullOrWhiteSpace(Title) ? Id : Title + "  (" + Id + ")";
}

public sealed class ToolsViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private string _summary = string.Empty;
    private bool _busy;
    private double _progress;
    private string _current = string.Empty;

    public ToolsViewModel(AppViewModel app)
    {
        _app = app;
        VerifyAllCommand = new RelayCommand(VerifyAllAsync, () => !Busy && Rows.Count > 0);
        RefreshCommand = new RelayCommand(() => app.RefreshAllAsync());

        // No canExecute gate on purpose: the button must never be silently dead.
        // Re-entry is already prevented inside RelayCommand, and the handler
        // no-ops when there is nothing to check.
        CheckUpdatesCommand = new RelayCommand(CheckUpdatesAsync);
        UpgradeCommand = new RelayCommand<ToolRow>(OpenUpgrade);
        AddToolCommand = new RelayCommand(OpenAdd);
        EditToolCommand = new RelayCommand<ToolRow>(OpenEdit);
        InstallToolCommand = new RelayCommand<ToolRow>(OpenInstall);
        OpenFolderCommand = new RelayCommand<ToolRow>(OpenFolder);
    }

    public ObservableCollection<ToolRow> Rows { get; } = new();

    public RelayCommand VerifyAllCommand { get; }
    public RelayCommand RefreshCommand { get; }

    /// <summary>Ask upstream for newer versions of every tool that declares a source.</summary>
    public RelayCommand CheckUpdatesCommand { get; }

    /// <summary>Download and install a discovered update.</summary>
    public RelayCommand<ToolRow> UpgradeCommand { get; }

    private string _updateSummary = string.Empty;
    public string UpdateSummary { get => _updateSummary; private set => Set(ref _updateSummary, value); }

    private bool _checkingUpdates;
    public bool CheckingUpdates
    {
        get => _checkingUpdates;
        private set
        {
            if (Set(ref _checkingUpdates, value))
            {
                OnPropertyChanged(nameof(Idle));
                CheckUpdatesCommand.Raise();
            }
        }
    }

    private async Task CheckUpdatesAsync()
    {
        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Manifest is null) { UpdateSummary = "没有可用的 U 盘"; return; }

        CheckingUpdates = true;
        UpdateSummary = "正在向上游查询版本…";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.Add("User-Agent", "PortableDev/1.0");
            var checker = new PortableDev.Core.Update.UpdateChecker(http);

            var found = 0;
            var checkedCount = 0;

            foreach (var row in Rows)
            {
                if (!stick.Manifest.Tools.TryGetValue(row.Id, out var tool) || tool.Update is null)
                {
                    row.MarkNoSource();
                    continue;
                }

                UpdateSummary = "正在查询 " + row.Id + " …";
                var info = await checker.CheckAsync(tool, row.Version);
                row.ApplyUpdateInfo(info);
                checkedCount++;
                if (row.HasUpdate) found++;
            }

            UpdateSummary = found == 0
                ? "已检查 " + checkedCount + " 个工具，都是最新"
                : "发现 " + found + " 个可升级（共检查 " + checkedCount + " 个）";
        }
        catch (Exception ex)
        {
            UpdateSummary = "检查失败：" + ex.Message;
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    private void OpenUpgrade(ToolRow row)
    {
        var info = row.UpdateInfo;
        var stick = PortableEngine.Locate();
        if (info is null || !stick.Found || stick.Root is null || stick.Manifest is null) return;
        if (!stick.Manifest.Tools.TryGetValue(row.Id, out var tool)) return;

        ShowDialogSafely(() => new PortableDev.Gui.Views.UpgradeWindow(stick.Root!, tool, info));
    }

    /// <summary>Open the "new tool" form; writes manifest.d\&lt;id&gt;.toml.</summary>
    public RelayCommand AddToolCommand { get; }

    /// <summary>Edit an existing manifest as raw TOML (comments survive).</summary>
    public RelayCommand<ToolRow> EditToolCommand { get; }

    /// <summary>Unpack an archive into tools\&lt;id&gt;\&lt;version&gt;.</summary>
    public RelayCommand<ToolRow> InstallToolCommand { get; }

    public RelayCommand<ToolRow> OpenFolderCommand { get; }

    private void OpenAdd() => ShowDialogSafely(
        () => new PortableDev.Gui.Views.ToolEditorWindow(null));

    private void OpenEdit(ToolRow row) => ShowDialogSafely(
        () => new PortableDev.Gui.Views.ToolEditorWindow(row.Id));

    private void OpenInstall(ToolRow row) => ShowDialogSafely(
        () => new PortableDev.Gui.Views.InstallWindow(row.Id, row.Version));

    /// <summary>
    /// Open a modal window and make any failure visible.
    ///
    /// A dialog that silently refuses to appear is the worst possible behaviour:
    /// the user clicks, nothing happens, and there is nothing to report. Every
    /// failure mode here ends in a message box with the real exception text.
    /// </summary>
    private void ShowDialogSafely(Func<System.Windows.Window> factory)
    {
        try
        {
            var win = factory();
            win.Owner = Owner;
            win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "打开这个窗口时出错：" + Environment.NewLine + Environment.NewLine + ex.Message,
                "PortableDev", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            _ = _app.RefreshAllAsync();
        }
    }

    private void OpenFolder(ToolRow row)
    {
        try
        {
            if (!Directory.Exists(row.Path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.Path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // opening a folder is a convenience; never let it break the page
        }
    }

    private static System.Windows.Window? Owner =>
        System.Windows.Application.Current?.MainWindow;

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            OnPropertyChanged(nameof(Idle));
            VerifyAllCommand.Raise();
            CheckUpdatesCommand.Raise();
        }
    }
    public bool Idle => !Busy;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Current { get => _current; private set => Set(ref _current, value); }

    public void Refresh()
    {
        Rows.Clear();
        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Manifest is null)
        {
            Summary = "没有可用的 U 盘";
            VerifyAllCommand.Raise();
            return;
        }

        var manifest = stick.Manifest;
        var total = 0;
        var present = 0;

        foreach (var (id, tool) in manifest.Tools.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!tool.Tool.Enabled) continue;
            var dir = Path.Combine(stick.Root!, PathResolver.ToolRelativeDir(tool).Replace('/', '\\'));
            var exists = Directory.Exists(dir);
            total++;
            if (exists) present++;

            Rows.Add(new ToolRow
            {
                Id = id,
                Title = tool.Tool.Title,
                Version = tool.Tool.Version,
                Path = dir,
                Notes = tool.Tool.Notes ?? string.Empty,
                Present = exists,
                Status = exists ? "未检查" : "目录缺失",
                Ok = exists,
                Checked = !exists,
            });
        }

        Summary = present + " / " + total + " 个工具目录就位";

        // These commands are gated on Rows.Count, which was 0 when WPF first asked.
        // Without an explicit nudge they stay greyed out for the whole session.
        VerifyAllCommand.Raise();
        CheckUpdatesCommand.Raise();
    }

    /// <summary>
    /// Run the same verifications the CLI runs. This delegates to 'PortableDev verify --json'
    /// so the GUI cannot disagree with the command line about what "passing" means.
    /// </summary>
    private async Task VerifyAllAsync()
    {
        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Manifest is null) return;

        Busy = true;
        Progress = 0;
        try
        {
            foreach (var r in Rows) { r.Checked = false; r.Status = "检查中…"; r.Ok = true; }
            Current = "正在运行 verify…";

            var result = await CliBridge.RunAsync(stick.Root, new[] { "verify", "--json" });
            var parsed = VerifyJson.Parse(result.Output);

            var done = 0;
            foreach (var row in Rows)
            {
                var hits = parsed.Where(p => string.Equals(p.Tool, row.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                row.Checked = true;
                if (hits.Count == 0)
                {
                    row.Ok = row.Present;
                    row.Status = row.Present ? "未声明检查" : "目录缺失";
                }
                else
                {
                    var ok = hits.All(h => h.Ok);
                    row.Ok = ok;
                    row.Status = ok ? "通过" : "失败";
                    var bad = hits.FirstOrDefault(h => !h.Ok);
                    row.Output = bad is null
                        ? string.Join("\n", hits.Select(h => h.Command + "  →  " + Truncate(h.Output)))
                        : bad.Command + "  →  " + (bad.Output ?? string.Empty);
                }
                done++;
                Progress = done * 100.0 / Math.Max(1, Rows.Count);
            }

            var failed = Rows.Count(r => r.Checked && !r.Ok);
            Summary = failed == 0
                ? Rows.Count + " 项检查全部通过"
                : (Rows.Count - failed) + " / " + Rows.Count + " 通过，" + failed + " 项失败";
            Current = string.Empty;
        }
        catch (Exception ex)
        {
            Current = "验证失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
            Progress = 0;
        }
    }

    private static string Truncate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(无输出)";
        var one = s.Replace("\r", string.Empty).Replace("\n", " | ").Trim();
        return one.Length > 120 ? one[..120] + "…" : one;
    }
}
