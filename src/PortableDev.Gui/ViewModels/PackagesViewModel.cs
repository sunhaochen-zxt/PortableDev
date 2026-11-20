using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Core;
using PortableDev.Core.Packages;
using PortableDev.Gui.Engine;
using PortableDev.Gui.Infrastructure;

namespace PortableDev.Gui.ViewModels;

/// <summary>One package row, on the recommended shelf, the browser or a search result.</summary>
public sealed class PackageRow
{
    public string App { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int Stars { get; init; }
    public bool IsInstalled { get; init; }

    /// <summary>Curated position, used to keep unknown-star rows in a sane order.</summary>
    public int Rank { get; init; }

    public string Resource => Bucket + "/" + App;

    public string StarsText => Stars <= 0
        ? string.Empty
        : Stars >= 1000 ? (Stars / 1000.0).ToString("0.0") + "k ★" : Stars + " ★";

    public string InstalledText => IsInstalled ? "已安装" : string.Empty;
}

/// <summary>A category chip for the browse view.</summary>
public sealed class CategoryChip
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
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
/// "软件包" page, AppStore style.
///
/// Two ways in: 推荐 is a hand-curated shelf ranked by GitHub stars, 全部软件 pages through
/// every app in Main and Extras (names come from the cached bucket index, so browsing works
/// offline; versions are fetched for the visible page only). Search covers everything.
///
/// Reads come from Core (the same parser the CLI uses); writes are delegated to
/// PortableDev.exe, so there is still exactly one write path.
/// </summary>
public sealed class PackagesViewModel : ObservableObject
{
    private const int PageSize = 40;

    private readonly AppViewModel _app;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };

    private string _query = string.Empty;
    private string _status = "正在加载推荐…";
    private bool _busy;
    private bool _searching;
    private bool _featuredLoaded;
    private bool _loadingFeatured;
    private bool _loadingBrowse;

    // 0 = recommended shelf, 1 = browse every app
    private int _mode;

    private List<ScoopIndex.Entry>? _allEntries;
    private List<ScoopIndex.Entry> _filtered = new();
    private string _category = ScoopCategories.All;
    private int _browsePage = 1;
    private int _browseTotalPages;

    public PackagesViewModel(AppViewModel app)
    {
        _app = app;

        // No CanExecute predicates: a disabled button that cannot say why is worse than a
        // command that returns early with a message in the status line.
        SearchCommand = new RelayCommand(SearchAsync);
        InstallCommand = new RelayCommand<PackageRow>(InstallAsync);
        RemoveCommand = new RelayCommand<InstalledPackageRow>(RemoveAsync);
        RefreshCommand = new RelayCommand(() =>
        {
            if (_mode == 0) _ = LoadFeaturedAsync(force: true);
            else _ = LoadBrowseAsync(force: true);
        });
        ClearCommand = new RelayCommand(() =>
        {
            Query = string.Empty;
            ReplaceResults(Array.Empty<PackageRow>());
            Status = _mode == 0 ? "推荐按 GitHub star 排序；切到「全部软件」可以翻页浏览。" : "正在显示全部软件。";
        });
        SetModeCommand = new RelayCommand<string>(mode =>
        {
            _mode = mode == "all" ? 1 : 0;
            ReplaceResults(Array.Empty<PackageRow>());
            RaiseMode();
            if (_mode == 0) _ = LoadFeaturedAsync();
            else _ = LoadBrowseAsync();
        });
        GoPageCommand = new RelayCommand<string>(step =>
        {
            var target = step == "next" ? _browsePage + 1 : _browsePage - 1;
            _ = ShowPageAsync(target);
        });
        SelectCategoryCommand = new RelayCommand<string>(category =>
        {
            _category = string.IsNullOrWhiteSpace(category) ? ScoopCategories.All : category;
            ApplyCategory();
            _ = ShowPageAsync(1);
        });
    }

    public ObservableCollection<PackageRow> Featured { get; } = new();
    public ObservableCollection<PackageRow> Browse { get; } = new();
    public ObservableCollection<PackageRow> Results { get; } = new();
    public ObservableCollection<InstalledPackageRow> Installed { get; } = new();

    public RelayCommand SearchCommand { get; }
    public RelayCommand<PackageRow> InstallCommand { get; }
    public RelayCommand<InstalledPackageRow> RemoveCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand<string> SetModeCommand { get; }
    public RelayCommand<string> GoPageCommand { get; }
    public RelayCommand<string> SelectCategoryCommand { get; }

    /// <summary>Category chips for the browse view, with counts.</summary>
    public ObservableCollection<CategoryChip> Categories { get; } = new();

    public string Query { get => _query; set => Set(ref _query, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    // ------------------------------------------------------------------ mode ----
    public bool ShowFeatured => _mode == 0 && Results.Count == 0;
    public bool ShowBrowse => _mode == 1 && Results.Count == 0;
    public bool ShowResults => Results.Count > 0;

    public string FeaturedTab => _mode == 0 ? "● 推荐" : "推荐";
    public string AllTab => _mode == 1 ? "● 全部软件" : "全部软件";

    public string PageInfo => _browseTotalPages == 0
        ? string.Empty
        : $"第 {_browsePage} / {_browseTotalPages} 页 · 共 {_filtered.Count} 个";

    public bool CanGoPrev => _browsePage > 1;
    public bool CanGoNext => _browsePage < _browseTotalPages;

    /// <summary>Called when the page becomes visible.</summary>
    public void Refresh()
    {
        LoadInstalled();
        if (_mode == 0) _ = LoadFeaturedAsync();
        else _ = LoadBrowseAsync();
    }

    private void RaiseMode()
    {
        OnPropertyChanged(nameof(ShowFeatured));
        OnPropertyChanged(nameof(ShowBrowse));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(FeaturedTab));
        OnPropertyChanged(nameof(AllTab));
    }

    // ------------------------------------------------------------ browse all ----

    private async Task LoadBrowseAsync(bool force = false)
    {
        if (_loadingBrowse) return;
        if (_allEntries is not null && !force)
        {
            await ShowPageAsync(_browsePage);
            return;
        }

        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Root is null)
        {
            Status = "没有检测到 U 盘（或清单读不出来）。";
            return;
        }

        _loadingBrowse = true;
        try
        {
            Status = "正在读取 bucket 列表…（首次会下载一次，之后走盘上的缓存）";
            var index = new ScoopIndex(_http, stick.Root);
            var (entries, error) = await index.LoadAsync(force);

            if (entries.Count == 0)
            {
                Status = "取不到 bucket 列表：" + (error ?? "未知错误");
                return;
            }

            _allEntries = entries.OrderBy(e => e.App, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyCategory();
            await ShowPageAsync(1);
        }
        catch (Exception ex)
        {
            Status = "读取 bucket 列表失败：" + ex.Message;
        }
        finally
        {
            _loadingBrowse = false;
        }
    }

    private async Task ShowPageAsync(int page)
    {
        if (_allEntries is null || _filtered.Count == 0) return;

        _browsePage = Math.Clamp(page, 1, _browseTotalPages);
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));

        var slice = _filtered
            .Skip((_browsePage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        var installed = Installed.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Browse.Clear();
        foreach (var entry in slice)
            Browse.Add(new PackageRow
            {
                App = entry.App,
                Bucket = entry.Bucket,
                Version = "?",
                Category = ScoopCategories.Classify(entry.App),
                IsInstalled = installed.Contains(entry.App),
            });

        Status = $"{_category}：共 {_filtered.Count} 个，第 {_browsePage} / {_browseTotalPages} 页。"
            + "版本信息按当前页读取。";

        // Versions are only fetched for the forty rows on screen; a short budget keeps a
        // dead network from freezing the page in "loading".
        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Root is null) return;

        var source = new ScoopSource(_http, stick.Root);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var gate = new SemaphoreSlim(5);
        var currentPage = _browsePage;

        var tasks = slice.Select(async (entry, index) =>
        {
            try { await gate.WaitAsync(budget.Token); }
            catch (OperationCanceledException) { return; }

            try
            {
                var (package, _) = await source.GetAsync(entry.Bucket + "/" + entry.App, false, budget.Token);
                if (package is null || currentPage != _browsePage) return;

                var replacement = new PackageRow
                {
                    App = entry.App,
                    Bucket = entry.Bucket,
                    Version = package.Version,
                    Description = package.Description ?? string.Empty,
                    Category = ScoopCategories.Classify(entry.App),
                    IsInstalled = installed.Contains(entry.App),
                };

                if (index < Browse.Count) Browse[index] = replacement;
            }
            catch (OperationCanceledException)
            {
                // budget exhausted; the row keeps its name
            }
            finally
            {
                gate.Release();
            }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    private void ApplyCategory()
    {
        if (_allEntries is null)
        {
            _filtered = new List<ScoopIndex.Entry>();
        }
        else if (_category == ScoopCategories.All)
        {
            _filtered = _allEntries;
        }
        else
        {
            _filtered = _allEntries.Where(e => ScoopCategories.Classify(e.App) == _category).ToList();
        }

        _browseTotalPages = Math.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        BuildChips();
    }

    private void BuildChips()
    {
        Categories.Clear();
        foreach (var category in ScoopCategories.Order)
        {
            var count = _allEntries is null
                ? 0
                : category == ScoopCategories.All
                    ? _allEntries.Count
                    : _allEntries.Count(e => ScoopCategories.Classify(e.App) == category);

            Categories.Add(new CategoryChip
            {
                Name = category,
                Label = count > 0 ? $"{category} {count}" : category,
                IsSelected = string.Equals(category, _category, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    // ------------------------------------------------------------- home shelf ----

    private async Task LoadFeaturedAsync(bool force = false)
    {
        if (_loadingFeatured) return;
        if (_featuredLoaded && !force) return;

        var stick = PortableEngine.Locate();
        if (!stick.Found || stick.Root is null)
        {
            Status = "没有检测到 U 盘（或清单读不出来）。";
            return;
        }

        _loadingFeatured = true;
        try
        {
            Status = "正在准备推荐列表…（首次会查一次版本和 GitHub star，之后走缓存）";

            var catalog = ScoopCatalog.Featured;
            var source = new ScoopSource(_http, stick.Root);
            var packages = new ScoopPackage?[catalog.Count];

            // A cold network must not leave the page "loading" forever: the shelf gets a
            // time budget, keeps whatever arrived, and only the missing rows are skipped.
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            using (var gate = new SemaphoreSlim(4))
            {
                var lookups = catalog.Select(async (entry, index) =>
                {
                    try { await gate.WaitAsync(budget.Token); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        var (package, _) = await source.GetAsync(entry.Bucket + "/" + entry.App, false, budget.Token);
                        packages[index] = package;
                    }
                    catch (OperationCanceledException)
                    {
                        // budget exhausted: skip the rest
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                try { await Task.WhenAll(lookups); }
                catch (OperationCanceledException) { }
            }

            // Star counts are fetched once per day; a rate limit only means the ranking
            // keeps the curated order for the entries without a known count.
            Dictionary<string, int> stars;
            using (var starBudget = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                try
                {
                    stars = await ScoopStars.LoadAsync(_http, stick.Root,
                        packages.Where(p => p is not null).Select(p => p!.Homepage), starBudget.Token);
                }
                catch
                {
                    stars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
            }

            var installed = Installed.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = new List<PackageRow>();
            var missing = 0;

            for (var i = 0; i < catalog.Count; i++)
            {
                var entry = catalog[i];
                var package = packages[i];
                if (package is null) missing++;

                var repo = ScoopStars.RepoOf(package?.Homepage);
                var count = repo is not null && stars.TryGetValue(repo, out var c) ? c : 0;

                // A row without a manifest still goes on the shelf: the offline page is
                // more useful listing names than showing an empty shell. Installing it
                // needs the network anyway, and the CLI says so.
                rows.Add(new PackageRow
                {
                    App = entry.App,
                    Bucket = entry.Bucket,
                    Version = package?.Version ?? "?",
                    Description = package?.Description ?? string.Empty,
                    Category = entry.Category,
                    Stars = count,
                    IsInstalled = installed.Contains(entry.App),
                    Rank = i,
                });
            }

            var ordered = rows
                .OrderByDescending(r => r.Stars)
                .ThenBy(r => r.Rank)
                .ToList();

            Featured.Clear();
            foreach (var row in ordered) Featured.Add(row);

            _featuredLoaded = missing == 0;
            Status = missing == 0
                ? $"推荐 {ordered.Count} 个，按 GitHub star 排序；切到「全部软件」可以翻页浏览全部。"
                : $"推荐 {ordered.Count} 个，其中 {missing} 个暂时取不到版本（离线或网络受限）；已缓存的信息照常显示，稍后点「刷新」重试。";
        }
        catch (Exception ex)
        {
            Status = "加载推荐失败：" + ex.Message;
        }
        finally
        {
            _loadingFeatured = false;
        }
    }

    // ---------------------------------------------------------------- search ----

    private async Task SearchAsync()
    {
        if (_searching) return;

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

        _searching = true;
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

            _allEntries = entries.OrderBy(e => e.App, StringComparer.OrdinalIgnoreCase).ToList();

            var hits = ScoopIndex.Search(entries, term, 48);
            if (hits.Count == 0)
            {
                ReplaceResults(Array.Empty<PackageRow>());
                Status = "没有名字匹配“" + term + "”的包。";
                return;
            }

            Status = $"找到 {hits.Count} 个，正在读取版本和简介…";
            var source = new ScoopSource(_http, stick.Root);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var gate = new SemaphoreSlim(4);
            var rows = new PackageRow?[hits.Count];

            await Task.WhenAll(hits.Select(async (hit, i) =>
            {
                try { await gate.WaitAsync(budget.Token); }
                catch (OperationCanceledException) { return; }

                try
                {
                    var (package, _) = await source.GetAsync(hit.Bucket + "/" + hit.App, false, budget.Token);
                    rows[i] = new PackageRow
                    {
                        App = hit.App,
                        Bucket = hit.Bucket,
                        Version = package?.Version ?? "?",
                        Description = package?.Description ?? string.Empty,
                        Category = ScoopCategories.Classify(hit.App),
                    };
                }
                catch (OperationCanceledException)
                {
                    // row keeps its name
                }
                finally
                {
                    gate.Release();
                }
            }));

            var installed = Installed.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var visible = rows.Where(r => r is not null).Select(r => r!).ToList();
            visible = visible.Select(r => new PackageRow
            {
                App = r.App,
                Bucket = r.Bucket,
                Version = r.Version,
                Description = r.Description,
                Category = r.Category,
                Stars = r.Stars,
                IsInstalled = installed.Contains(r.App),
            }).ToList();

            ReplaceResults(visible);
            Status = $"“{term}” 有 {visible.Count} 个结果。点“安装”会下载、校验、解包并自动 apply。";
        }
        catch (Exception ex)
        {
            Status = "搜索失败：" + ex.Message;
        }
        finally
        {
            _searching = false;
        }
    }

    private void ReplaceResults(IEnumerable<PackageRow> rows)
    {
        Results.Clear();
        foreach (var row in rows) Results.Add(row);
        OnPropertyChanged(nameof(ShowFeatured));
        OnPropertyChanged(nameof(ShowBrowse));
        OnPropertyChanged(nameof(ShowResults));
    }

    // --------------------------------------------------------------- install ----

    private async Task InstallAsync(PackageRow? row)
    {
        if (row is null || Busy) return;

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
        if (row is null || Busy) return;

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
    }
}
