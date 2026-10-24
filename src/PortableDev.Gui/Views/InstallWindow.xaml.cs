using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using PortableDev.Core;
using PortableDev.Gui.Engine;

namespace PortableDev.Gui.Views;

/// <summary>Unpack one archive into tools\&lt;id&gt;\&lt;version&gt;.</summary>
public partial class InstallWindow : Window
{
    private readonly string _toolId;
    private readonly string _stickRoot;

    public InstallWindow(string toolId, string manifestVersion)
    {
        InitializeComponent();
        _toolId = toolId;

        var stick = PortableEngine.Locate();
        _stickRoot = stick.Root ?? string.Empty;

        TitleText.Text = "安装 / 更新 " + toolId;
        TxtVersion.Text = manifestVersion;

        if (_stickRoot.Length > 0)
        {
            TxtArchive.Text = GuessArchive(_stickRoot, toolId) ?? string.Empty;
        }

        if (_stickRoot.Length == 0)
            Append("没有找到 U 盘。");
        else
            Append("目标目录：" + Path.Combine(_stickRoot, "tools", toolId, manifestVersion));
    }

    /// <summary>Pre-fill the newest archive found under downloads\&lt;id&gt;\ if any.</summary>
    private static string? GuessArchive(string stickRoot, string toolId)
    {
        var dir = Path.Combine(stickRoot, "downloads", toolId);
        if (!Directory.Exists(dir)) return null;

        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 " + _toolId + " 的压缩包",
            Filter = "压缩包|*.zip;*.7z;*.tar.gz;*.tgz|所有文件|*.*",
            CheckFileExists = true,
        };

        var downloads = Path.Combine(_stickRoot, "downloads");
        if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;

        if (dlg.ShowDialog(this) == true) TxtArchive.Text = dlg.FileName;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_stickRoot.Length == 0) { Append("没有找到 U 盘。"); return; }

        var version = TxtVersion.Text.Trim();
        if (version.Length == 0) { Append("版本不能为空。"); return; }

        var archive = TxtArchive.Text.Trim();
        if (archive.Length == 0) { Append("请先选择压缩包。"); return; }

        BtnInstall.IsEnabled = false;
        try
        {
            Append("");
            Append("---- 开始安装 " + _toolId + " " + version + " ----");

            Append("正在解压…（大包需要几十秒）");

            // Off the UI thread: extracting tens of thousands of files on exFAT
            // would otherwise freeze the window into "not responding".
            var strip = ChkStrip.IsChecked == true;
            var result = await System.Threading.Tasks.Task.Run(
                () => ToolInstaller.Install(_stickRoot, _toolId, version, archive, strip));

            foreach (var line in result.Log) Append(line);

            if (!result.Ok) { Append("未完成。"); return; }

            Append("文件数：" + result.FileCount);

            if (ChkUpdateVersion.IsChecked == true)
            {
                var text = ManifestEditor.Read(_stickRoot, _toolId);
                var updated = ManifestEditor.SetVersion(text, version);
                if (updated is null)
                {
                    Append("清单里没找到 version 行，请手动修改。");
                }
                else
                {
                    var write = ManifestEditor.TryWrite(_stickRoot, _toolId, updated);
                    Append(write.Ok
                        ? "清单版本已改为 " + version + "。"
                        : "写清单失败：" + string.Join("; ", write.Errors));
                }
            }

            Append("");
            Append("完成。回到工具页按“刷新”，再按“应用”把它加进 PATH。");
        }
        catch (Exception ex)
        {
            Append("出错：" + ex.Message);
        }
        finally
        {
            BtnInstall.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Append(string line)
    {
        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
    }
}
