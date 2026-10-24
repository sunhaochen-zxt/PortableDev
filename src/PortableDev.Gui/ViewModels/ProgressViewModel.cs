using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;

namespace PortableDev.Gui.ViewModels;

/// <summary>
/// State of the apply/clean operation. Apply always goes through the CLI so the
/// elevation broker stays the single write path; this VM only renders what it says.
/// </summary>
public sealed class ProgressViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private string _title = "无进行中的操作";
    private string _subtitle = string.Empty;
    private bool _running;
    private string _result = string.Empty;
    private bool _ok = true;
    private int _step;
    private int _stepTotal;

    public ProgressViewModel(AppViewModel app)
    {
        _app = app;
        CopyCommand = new RelayCommand(CopyTranscript, () => Lines.Count > 0);
        ClearCommand = new RelayCommand(() =>
        {
            Lines.Clear();
            Title = "无进行中的操作";
            Subtitle = string.Empty;
            Result = string.Empty;
            Step = 0;
            StepTotal = 0;
        }, () => Lines.Count > 0);
    }

    public RelayCommand CopyCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>
    /// Put the transcript on the clipboard. Failures are swallowed on purpose:
    /// the clipboard is shared and can be locked by another process, and that must
    /// never take down the window.
    /// </summary>
    private void CopyTranscript()
    {
        try
        {
            var text = Transcript;
            if (text.Length > 0) System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // another process holds the clipboard; nothing useful to do
        }
    }

    public ObservableCollection<string> Lines { get; } = new();

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public bool Running { get => _running; private set { if (Set(ref _running, value)) OnPropertyChanged(nameof(Idle)); } }
    public bool Idle => !Running;
    public string Result
    {
        get => _result;
        private set
        {
            if (Set(ref _result, value)) OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>Drives the verdict banner; binding a string to a visibility converter
    /// would always be false, so the view binds this bool instead.</summary>
    public bool HasResult => !string.IsNullOrEmpty(_result);
    public bool Ok { get => _ok; private set => Set(ref _ok, value); }
    public int Step { get => _step; private set { if (Set(ref _step, value)) OnPropertyChanged(nameof(StepText)); } }
    public int StepTotal { get => _stepTotal; private set { if (Set(ref _stepTotal, value)) OnPropertyChanged(nameof(StepText)); } }
    public string StepText => StepTotal > 0 ? "步骤 " + Step + " / " + StepTotal : string.Empty;

    public void Reset(string title, int stepTotal) =>
        UiThread.Post(() =>
        {
            Lines.Clear();
            Title = title;
            Subtitle = string.Empty;
            Result = string.Empty;
            Ok = true;
            Step = 0;
            StepTotal = stepTotal;
            Running = true;
        });

    /// <summary>Full transcript, for the copy button.</summary>
    public string Transcript => string.Join(Environment.NewLine, Lines);

    /// <summary>
    /// Add one output line. Streamed from a process reader, so it may arrive on any
    /// thread: the collection is thread-affine and every mutation is marshalled.
    /// </summary>
    public void Append(string line) => UiThread.Post(() => AppendCore(line));

    private void AppendCore(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Lines.Add(line);
        CopyCommand.Raise();
        ClearCommand.Raise();

        // The CLI narrates its own steps; mirror them so the bar moves with reality
        // instead of on a timer.
        var head = line.Split(' ')[0].TrimEnd('-', '>', ' ');
        switch (head)
        {
            case "backup": Step = Math.Max(Step, 1); Subtitle = "已备份原 PATH"; break;
            case "elevating": Subtitle = "等待 UAC 授权（只为创建联接）…"; break;
            case "junction": Step = Math.Max(Step, 2); Subtitle = "稳定根联接就绪"; break;
            case "env": Step = Math.Max(Step, 3); Subtitle = "环境变量已写入"; break;
            case "path": Step = Math.Max(Step, 4); Subtitle = "PATH 已更新"; break;
            case "applied.": Step = StepTotal; Subtitle = "完成"; break;
            case "nothing": Step = StepTotal; Subtitle = "无需改动"; break;
            case "cleaned.": Step = StepTotal; Subtitle = "已卸载"; break;
        }
    }

    public void Finish(CliResult r) => UiThread.Post(() => FinishCore(r));

    private void FinishCore(CliResult r)
    {
        Running = false;
        Ok = r.Ok;
        Subtitle = r.Ok ? "已完成" : "失败";
        Result = r.Ok
            ? (Lines.Any(l => l.StartsWith("nothing", StringComparison.Ordinal))
                ? "机器已与清单一致，没有写入任何东西。"
                : "操作完成。已打开的终端需要重新打开才能看到新的 PATH。")
            : "失败：" + r.Describe() + (string.IsNullOrWhiteSpace(r.Error) ? string.Empty : " — " + r.Error.Trim());
    }
}
