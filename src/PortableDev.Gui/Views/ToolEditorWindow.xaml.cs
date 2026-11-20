using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using PortableDev.Gui.Engine;

namespace PortableDev.Gui.Views;

/// <summary>
/// Two modes in one window:
///   Add  — a short form that renders a new manifest.d\&lt;id&gt;.toml;
///   Edit — the raw TOML, so comments and keys the form does not know about
///          survive untouched.
/// Saving always reloads the whole manifest and rolls back on failure.
/// </summary>
public partial class ToolEditorWindow : Window
{
    private readonly string? _editId;
    private string _stickRoot = string.Empty;

    public ToolEditorWindow(string? editId)
    {
        InitializeComponent();
        _editId = editId;

        CmbKind.ItemsSource = new[] { "prebuilt", "zip", "tar.gz", "7z-sfx", "raw" };
        CmbKind.SelectedIndex = 0;

        var stick = PortableEngine.Locate();
        _stickRoot = stick.Root ?? string.Empty;

        if (editId is null)
        {
            TitleText.Text = "新增工具";
            SubtitleText.Text = "生成 manifest.d\\<id>.toml。保存后回到工具页按“应用”才会写进 PATH。";
            TxtId.TextChanged += (_, _) => AutoDir();
            TxtVersion.TextChanged += (_, _) => AutoDir();
        }
        else
        {
            TitleText.Text = "编辑工具";
            SubtitleText.Text = "直接改 TOML 原文。保存时会整体重新解析，出错自动回滚。";
            FormPanel.Visibility = Visibility.Collapsed;
            RawPanel.Visibility = Visibility.Visible;
            BtnDelete.Visibility = Visibility.Visible;
            Title = "编辑 " + editId;

            RawPanel.Text = _stickRoot.Length == 0
                ? string.Empty
                : ManifestEditor.Read(_stickRoot, editId);
        }
    }

    private void AutoDir()
    {
        var id = TxtId.Text.Trim();
        var ver = TxtVersion.Text.Trim();
        if (id.Length == 0) return;
        TxtDir.Text = ver.Length == 0
            ? "tools/" + id + "/<版本>"
            : "tools/" + id + "/" + ver;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_stickRoot.Length == 0) { ShowError("没有找到 U 盘。"); return; }

        if (_editId is not null)
        {
            var ok = ManifestEditor.TryWrite(_stickRoot, _editId, RawPanel.Text);
            if (!ok.Ok) { ShowError(string.Join(Environment.NewLine, ok.Errors)); return; }
            DialogResult = true;
            return;
        }

        // ---- add mode ----
        var id = TxtId.Text.Trim();
        var version = TxtVersion.Text.Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]*$"))
        {
            ShowError("工具 id 只能用小写字母、数字、点、下划线、连字符，且以字母或数字开头。");
            return;
        }
        if (version.Length == 0) { ShowError("版本不能为空。"); return; }

        if (string.IsNullOrWhiteSpace(TxtDir.Text))
            TxtDir.Text = "tools/" + id + "/" + version;

        if (ManifestEditor.Exists(_stickRoot, id)) { ShowError("已经存在同名工具：" + id + "。请换个 id，或去“编辑”里改。"); return; }

        var spec = new NewToolSpec
        {
            Id = id,
            Version = version,
            Title = TxtTitle.Text.Trim(),
            Kind = (CmbKind.SelectedItem as string) ?? "prebuilt",
            Dir = TxtDir.Text.Trim(),
            Notes = TxtNotes.Text.Trim(),
            Enabled = ChkEnabled.IsChecked == true,
        };

        foreach (var line in TxtPathDirs.Text.Replace("\r\n", "\n").Split('\n'))
        {
            var d = line.Trim();
            if (d.Length > 0) spec.PathDirs.Add(d);
        }
        if (spec.PathDirs.Count == 0) spec.PathDirs.Add(".");

        spec.Verifies.Add(new VerifyRow { Run = TxtVerifyRun.Text, Expect = TxtVerifyExpect.Text });

        var text = ManifestEditor.BuildToolToml(spec);
        var result = ManifestEditor.TryWrite(_stickRoot, id, text);
        if (!result.Ok) { ShowError(string.Join(Environment.NewLine, result.Errors)); return; }

        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_editId is null || _stickRoot.Length == 0) return;

        var answer = System.Windows.MessageBox.Show(
            "从清单里删除 “" + _editId + "” 吗？\n\n只删 manifest.d\\ 下的定义，工具目录本身不动。",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            ManifestEditor.Delete(_stickRoot, _editId);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorBox.Visibility = Visibility.Visible;
    }
}
