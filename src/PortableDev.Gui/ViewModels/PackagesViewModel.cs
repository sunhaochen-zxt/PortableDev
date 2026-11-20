using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Core.Packages;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;

namespace PortableDev.Gui.ViewModels;

/// <summary>One row of a bucket search.</summary>
public sealed class PackageRow
{
    public string App { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Resource => Bucket + "/" + App;
}

/// <summary>One already-installed bucket package.</summary>
public sealed class InstalledPackageRow
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
}

/// <summary>
/// "软件包" page: search a Scoop bucket and install into the managed tool tree.
///
/// Reads come from Core directly (the same parser the CLI uses); writes are delegated to
/// PortableDev.exe, so there is still exactly one write path. Installed packages are just
/// generated manifests, so this page shows what `pkg list` shows.
/// </summary>
public sealed class PackagesViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private string _query = string.Empty;
    private string _status = "输入关键词搜索 bucket，例如 fd、ripgrep、notepad。安装后会自动 apply。";
    private bool _busy;

    public PackagesViewModel(AppViewModel app)
    {
        _app = app;

        SearchCommand = new RelayCommand(SearchAsync, () => !Busy);
        InstallCommand = new RelayCommand<PackageRow>(InstallAsync, _ => !Busy);
        RemoveCommand = new RelayCommand<InstalledPackageRow>(RemoveAsync, _ => !Busy);
        RefreshCommand = new RelayCommand(Refresh);
        ClearCommand = new RelayCommand(() =>
        {
            Query = string.Empty;
            Results.Clear();
        });
    }

    public ObservableCollection<PackageRow> Results { get; } = new();
    public ObservableCollection<InstalledPackageRow> Installed { get; } = new();

    public RelayCommand SearchCommand { get; }
    public RelayCommand<PackageRow> InstallCommand { get; }
    public RelayCommand<InstalledPackageRow> RemoveCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ClearCommand { get; }

    public string Query
    {
        get => _query;
        set => Set(ref _query, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            SearchCommand.Raise();
            InstallCommand.Raise();
            RemoveCommand.Raise();
        }
    }

    /// <summary>Called when the page becomes visible.</summary>
    public void Refresh() => LoadInstalled();

    private void LoadInstalled()
    {
        Installed.Clear();

        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Manifest is null || stick.Root is null)
        {
            Status = "没有检测到 U 盘（或清单读不出来）。";
            return;
        }

        foreach (var tool in stick.Manifest.Tools.Values
                     .Where(t => t.Tool.Source?.StartsWith("scoop:", StringComparison.OrdinalIgnoreCase) == true)
                     .OrderBy(t => t.Tool.Id, StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(stick.Root, PathResolver.ToolRelativeDir(tool).Replace('/', '\\'));
            Installed.Add(new InstalledPackageRow
            {
                Id = tool.Tool.Id,
                Version = tool.Tool.Version,
                Source = tool.Tool.Source ?? string.Empty,
                State = Directory.Exists(dir) ? "已安装" : "缺文件",
            });
        }

        if (Installed.Count == 0)
            Status = "还没有用 pkg 装过包。搜索一个名字试试；装完的包会出现在下面。";
        else
            Status = $"已安装 {Installed.Count} 个 bucket 包。";
    }

    private async Task SearchAsync()
    {
        var term = (Query ?? string.Empty).Trim();
        if (term.Length == 0)
        {
            Status = "先输入关键词，例如 fd、ripgrep、notepad。";
            return;
        }

        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Root is null)
        {
            Status = "没有检测到 U 盘。";
            return;
        }

        Busy = true;
        try
        {
            Status = "正在读取 bucket 索引…（首次会下载一次，之后走缓存）";
            var index = new ScoopIndex(_http, stick.Root);
            var (entries, error) = await index.LoadAsync(false);
            if (entries.Count == 0)
            {
                Status = "取不到 bucket 索引：" + (error ?? "未知错误");
                return;
            }

            var hits = ScoopIndex.Search(entries, term, 24);
            if (hits.Count == 0)
            {
                Results.Clear();
                Status = "没有名字匹配“" + term + "”的包。";
                return;
            }

            Status = $"找到 {hits.Count} 个，正在读取版本和简介…";
            var source = new ScoopSource(_http, stick.Root);
            var gate = new SemaphoreSlim(4);
            var rows = new PackageRow?[hits.Count];

            await Task.WhenAll(hits.Select(async (hit, i) =>
            {
                await gate.WaitAsync();
                try
                {
                    var (package, _) = await source.GetAsync(hit.Bucket + "/" + hit.App, false, CancellationToken.None);
                    rows[i] = new PackageRow
                    {
                        App = hit.App,
                        Bucket = hit.Bucket,
                        Version = package?.Version ?? "?",
                        Description = package?.Description ?? string.Empty,
                    };
                }
                finally
                {
                    gate.Release();
                }
            }));

            Results.Clear();
            foreach (var row in rows) if (row is not null) Results.Add(row);

            Status = $"“{term}” 有 {hits.Count} 个结果。点“安装”会下载、校验、解包并自动 apply。";
        }
        catch (Exception ex)
        {
            Status = "搜索失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallAsync(PackageRow? row)
    {
        if (row is null) return;

        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Root is null)
        {
            Status = "没有检测到 U 盘。";
            return;
        }

        Busy = true;
        try
        {
            Status = "正在安装 " + row.Resource + "…";
            var result = await CliBridge.RunAsync(stick.Root,
                new[] { "pkg", "add", row.Resource, "--yes" },
                line => UiThread.Post(() => Status = line));

            if (result.Ok)
            {
                Status = row.Resource + " 安装完成。";
                await _app.RefreshAllAsync();
            }
            else
            {
                Status = "安装失败：" + (string.IsNullOrWhiteSpace(result.Error) ? result.Describe() : result.Error.Trim());
            }

            LoadInstalled();
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RemoveAsync(InstalledPackageRow? row)
    {
        if (row is null) return;

        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Root is null)
        {
            Status = "没有检测到 U 盘。";
            return;
        }

        Busy = true;
        try
        {
            Status = "正在卸载 " + row.Id + "…";
            var result = await CliBridge.RunAsync(stick.Root,
                new[] { "pkg", "remove", row.Id, "--yes" },
                line => UiThread.Post(() => Status = line));

            Status = result.Ok ? row.Id + " 已卸载（persist 数据保留）。" : "卸载失败：" + result.Describe();
            await _app.RefreshAllAsync();
            LoadInstalled();
        }
        finally
        {
            Busy = false;
        }
    }
}
