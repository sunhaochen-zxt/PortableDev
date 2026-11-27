using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PortableDev.Core.Util;

namespace PortableDev.Window;

/// <summary>The window shell: a thin container that loads pages provided by extensions.</summary>
public sealed class MainForm : Form
{
    private readonly WindowOptions _options;
    private readonly SmoothPanel _sidebar = new();
    private readonly SmoothPanel _brandHeader = new();
    private readonly Panel _sidebarDivider = new();
    private readonly SmoothPanel _footerPanel = new();
    private readonly Panel _footerDivider = new();
    private readonly SmoothNavListBox _navigation = new();
    private readonly Label _status = new();
    private readonly List<JsonObject> _pages = new();
    private readonly Font _brandTitleFont = new("Segoe UI", 11.5f, FontStyle.Bold);
    private readonly Font _brandSubFont = new("Segoe UI", 8f, FontStyle.Regular);
    private readonly Font _brandBadgeFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    private readonly Font _navFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    private readonly Font _navActiveFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    private readonly Font _statusFont = new("Segoe UI", 8.25f, FontStyle.Regular);
    private WebView2 _webView = new();
    private KernelLink? _link;
    private Bridge? _bridge;
    private bool _initialized;
    private bool _darkTheme;
    private bool _suppressNavChange;
    private int _hoverIndex = -1;
    private long _navStart;
    private string _baseStatus = string.Empty;
    private bool _smokeWritten;
    private System.Windows.Forms.Timer? _smokeTimer;

    public MainForm(WindowOptions options)
    {
        _options = options;
        Text = "PortableDev";
        Width = 1260;
        Height = 800;
        MinimumSize = new Size(920, 600);
        DoubleBuffered = true;

        var initialLook = ReadLook();
        _darkTheme = string.Equals(initialLook.Theme, "dark", StringComparison.OrdinalIgnoreCase);

        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = 224;
        _sidebar.Padding = new Padding(0);

        _brandHeader.Dock = DockStyle.Top;
        _brandHeader.Height = 60;
        _brandHeader.Paint += PaintBrandHeader;

        _footerPanel.Dock = DockStyle.Bottom;
        _footerPanel.Height = 40;
        _footerPanel.Padding = new Padding(14, 0, 12, 0);

        _footerDivider.Dock = DockStyle.Top;
        _footerDivider.Height = 1;

        _status.Dock = DockStyle.Fill;
        _status.Font = _statusFont;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        _status.Text = "正在连接内核…";
        _footerPanel.Controls.Add(_status);
        _footerPanel.Controls.Add(_footerDivider);

        _navigation.Dock = DockStyle.Fill;
        _navigation.BorderStyle = BorderStyle.None;
        _navigation.DrawMode = DrawMode.OwnerDrawFixed;
        _navigation.ItemHeight = 38;
        _navigation.IntegralHeight = false;
        _navigation.Cursor = Cursors.Hand;
        _navigation.DrawItem += DrawNavigationItem;
        _navigation.MouseMove += OnNavigationMouseMove;
        _navigation.MouseLeave += OnNavigationMouseLeave;
        _navigation.SelectedIndexChanged += async (_, _) =>
        {
            if (!_suppressNavChange)
            {
                await ShowSelectedPageAsync().ConfigureAwait(true);
            }
        };

        _sidebar.Controls.Add(_navigation);
        _sidebar.Controls.Add(_footerPanel);
        _sidebar.Controls.Add(_brandHeader);

        _sidebarDivider.Dock = DockStyle.Left;
        _sidebarDivider.Width = 1;

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);
        Controls.Add(_sidebarDivider);
        Controls.Add(_sidebar);

        ApplyShellTheme(_darkTheme);
        HandleCreated += (_, _) => ApplyWindowTitleBarTheme(_darkTheme);
        Shown += async (_, _) => await InitializeAsync().ConfigureAwait(true);
        FormClosed += (_, _) =>
        {
            _smokeTimer?.Stop();
            _smokeTimer?.Dispose();
            _smokeTimer = null;
            _brandTitleFont.Dispose();
            _brandSubFont.Dispose();
            _brandBadgeFont.Dispose();
            _navFont.Dispose();
            _navActiveFont.Dispose();
            _statusFont.Dispose();
            DisposeLink();
        };
    }

    private async Task LoadPagesAsync()
    {
        var pages = await _link!.RequestPagesAsync().ConfigureAwait(true);
        _pages.Clear();
        _navigation.Items.Clear();
        foreach (var node in pages)
        {
            if (node is not JsonObject page)
            {
                continue;
            }

            _pages.Add(page);
            _navigation.Items.Add(new PageItem(
                JsonUtil.String(page, "title") ?? "页面",
                JsonUtil.String(page, "icon") ?? "box",
                page));
        }
    }

    /// <summary>Copies missing/version-mismatched built-in extensions from app\baseline\extensions.</summary>
    private void EnsureBuiltInExtensions()
    {
        var baseline = Path.Combine(_options.Root, "app", "baseline", "extensions");
        var installed = Path.Combine(_options.Root, "app", "extensions");
        if (!Directory.Exists(baseline))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(baseline))
        {
            try
            {
                var name = Path.GetFileName(directory);
                var sourceManifest = Path.Combine(directory, "ext.json");
                var targetManifest = Path.Combine(installed, name, "ext.json");
                var stale = !File.Exists(targetManifest);
                if (!stale)
                {
                    var want = JsonNode.Parse(File.ReadAllText(sourceManifest)) as JsonObject;
                    var got = JsonNode.Parse(File.ReadAllText(targetManifest)) as JsonObject;
                    stale = want is not null && got is not null
                        && !string.Equals(JsonUtil.String(want, "version"), JsonUtil.String(got, "version"), StringComparison.Ordinal);
                }

                if (stale)
                {
                    CopyDirectory(directory, Path.Combine(installed, name));
                }
            }
            catch (Exception)
            {
                // Best effort; a still-broken extension is reported by the kernel and pd doctor.
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var child in Directory.GetDirectories(source))
        {
            CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
        }
    }

    private async Task<bool> TryRepairExtensionsAsync()
    {
        var baseline = Path.Combine(_options.Root, "app", "baseline", "extensions");
        if (!Directory.Exists(baseline) || _link is null)
        {
            return false;
        }

        _status.Text = "界面扩展缺失，正在从 baseline 恢复…";
        try
        {
            var done = await _link.RunAsync(["doctor", "--fix"], 180000).ConfigureAwait(true);
            var exitCode = JsonUtil.Int(done, "exitCode") ?? 0;
            var ok = exitCode == 0 && (JsonUtil.Bool(done, "ok") ?? true);
            _status.Text = ok ? "已从 baseline 恢复界面扩展" : "恢复界面扩展失败（详见 pd doctor）";
            return ok;
        }
        catch (Exception ex)
        {
            _status.Text = "恢复界面扩展失败: " + ex.Message;
            return false;
        }
    }

    private void DisposeLink()
    {
        var link = _link;
        _link = null;
        if (link is not null)
        {
            _ = link.DisposeAsync().AsTask();
        }
    }

    private async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            // Repair built-in extension files *before* the kernel scans its registry: restoring
            // after boot would need a kernel restart (the registry is scanned once).
            EnsureBuiltInExtensions();
            _link = await KernelLink.ConnectAsync(_options.Root, _options.KernelDll).ConfigureAwait(true);
            await LoadPagesAsync().ConfigureAwait(true);

            StartSmokeWatchdog();
            if (_pages.Count == 0)
            {
                _status.Text = "没有安装界面扩展（缺少 ui 扩展）";
                if (_options.Smoke is not null)
                {
                    WriteSmoke(new JsonObject { ["ok"] = false, ["status"] = _status.Text, ["error"] = "no pages" });
                    BeginInvoke(Close);
                }

                return;
            }

            if (!await EnsureWebViewAsync().ConfigureAwait(true))
            {
                _status.Text = "WebView2 初始化失败（已重试 3 次），详见 state/logs/window-error.log";
                if (_options.Smoke is not null)
                {
                    WriteSmoke(new JsonObject { ["ok"] = false, ["status"] = _status.Text, ["error"] = "webview failed" });
                    BeginInvoke(Close);
                }

                return;
            }

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // The shell only loads local extension pages: skip the per-navigation
            // reputation (SmartScreen) check, which stalls every navigation on a fake host.
            core.Settings.IsReputationCheckingRequired = false;
            core.SetVirtualHostNameToFolderMapping("pd.local", _options.Extensions, CoreWebView2HostResourceAccessKind.Allow);

            var look = ReadLook();
            ApplyShellTheme(string.Equals(look.Theme, "dark", StringComparison.OrdinalIgnoreCase));
            _bridge = new Bridge(
                _webView,
                _link,
                NavigateToAsync,
                Bridge.LoadPolicies(_options.Extensions),
                theme =>
                {
                    if (IsDisposed || !IsHandleCreated)
                    {
                        return;
                    }

                    BeginInvoke(() => ApplyShellTheme(string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)));
                });
            await _bridge.AttachAsync(System.Text.Json.JsonSerializer.Serialize(look.Theme), System.Text.Json.JsonSerializer.Serialize(look.Locale)).ConfigureAwait(true);

            // The shell only ever shows local extension pages: block any other navigation and
            // all popup windows, so a package page cannot turn the shell into a browser.
            core.NavigationStarting += (_, navigation) =>
            {
                if (!IsLocalPageUrl(navigation.Uri))
                {
                    navigation.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, window) => window.Handled = true;
            core.NavigationCompleted += OnNavigationCompleted;
            if (_options.Smoke is not null || _options.SmokeCheck is not null)
            {
                var checkFile = _options.SmokeCheck ?? _options.Smoke;
                core.NavigationCompleted += async (_, eventArgs) => await FinishSmokeAsync(eventArgs, checkFile).ConfigureAwait(true);
            }

            // Navigate explicitly: relying on SelectedIndexChanged is fragile (adding items
            // can already leave the selection at 0, in which case no event fires and the view
            // stays blank until the user's first click).
            // The headless smoke-check scrapes the overview page (env/version blocks); a UI
            // extension may reorder its pages, so pick it by id instead of assuming index 0.
            var firstIndex = 0;
            if (_options.SmokeCheck is not null)
            {
                var overview = _pages.FindIndex(page =>
                    string.Equals(JsonUtil.String(page, "id"), "overview", StringComparison.Ordinal));
                firstIndex = overview >= 0 ? overview : 0;
            }

            SelectSidebarIndex(firstIndex);
            _baseStatus = "内核 " + _link.KernelVersion + " · " + Path.GetFileName(_options.Root);
            _status.Text = _baseStatus;
            await NavigateToPageAsync(_pages[firstIndex]).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
    }

    /// <summary>True only for the virtual host the shell itself maps; everything else is foreign.</summary>
    private static bool IsLocalPageUrl(string? uri)
        => !string.IsNullOrEmpty(uri)
            && uri.StartsWith("https://pd.local/", StringComparison.OrdinalIgnoreCase)
            && !uri.Contains("..", StringComparison.Ordinal);

    private async Task<bool> EnsureWebViewAsync()
    {
        CleanupStaleWebViewData();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var environment = await CreateWebViewEnvironmentAsync(attempt).ConfigureAwait(true);
                await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnostic(ex, attempt);
                RecreateWebView();
            }
        }

        return false;
    }

    /// <summary>
    /// Every attempt gets a fresh user-data folder: WebView2 refuses a folder another process still
    /// holds (ERROR_BUSY) and reports E_UNEXPECTED when a half-initialized profile is reused.
    /// </summary>
    private static async Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync(int attempt)
    {
        // Pages live on a fake host (https://pd.local) that only exists inside this WebView.
        // A system proxy would stall every navigation on it, so bypass the proxy entirely.
        var browserOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--proxy-server=direct://",
        };
        var local = Path.Combine(Path.GetTempPath(), "pd-window");
        var name = attempt == 0
            ? Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            : Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var folder = Path.Combine(local, name);
        return await CoreWebView2Environment.CreateAsync(null, folder, browserOptions).ConfigureAwait(true);
    }

    private static void CleanupStaleWebViewData()
    {
        try
        {
            var local = Path.Combine(Path.GetTempPath(), "pd-window");
            if (!Directory.Exists(local))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(local))
            {
                var name = Path.GetFileName(dir);
                var dash = name.IndexOf('-');
                var pidPart = dash < 0 ? name : name.Substring(0, dash);
                if (!int.TryParse(pidPart, CultureInfo.InvariantCulture, out var pid))
                {
                    continue;
                }

                try
                {
                    System.Diagnostics.Process.GetProcessById(pid).Dispose();
                }
                catch (ArgumentException)
                {
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                }
                catch (Exception)
                {
                    // Unknown state: leave it alone.
                }
            }
        }
        catch (Exception)
        {
            // Cleanup must never block startup.
        }
    }

    private void RecreateWebView()
    {
        try
        {
            Controls.Remove(_webView);
            _webView.Dispose();
        }
        catch (Exception)
        {
            // The control may already be disposed.
        }

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = _darkTheme ? Color.FromArgb(39, 39, 39) : Color.FromArgb(249, 249, 249),
        };
        Controls.Add(_webView);
        _webView.BringToFront();
    }

    private void HandleFailure(Exception ex)
    {
        WriteDiagnostic(ex, -1);
        _status.Text = "窗口壳启动失败: " + ex.Message + "（详见 state/logs/window-error.log）";
        if (_options.Smoke is not null)
        {
            WriteSmoke(new JsonObject { ["ok"] = false, ["status"] = _status.Text, ["error"] = ex.Message });
            BeginInvoke(Close);
        }
    }

    private void WriteDiagnostic(Exception ex, int attempt)
    {
        try
        {
            var directory = Path.Combine(_options.Root, "state", "logs");
            Directory.CreateDirectory(directory);
            var builder = new StringBuilder();
            builder.AppendLine(DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + " attempt=" + attempt
                + " hresult=0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ex.GetType().Name + ": " + ex.Message);
            builder.AppendLine(ex.ToString());
            File.AppendAllText(Path.Combine(directory, "window-error.log"), builder.ToString(), Encoding.UTF8);
        }
        catch (Exception)
        {
            // Diagnostics must never crash the shell.
        }
    }

    private void StartSmokeWatchdog()
    {
        if (_options.Smoke is null)
        {
            return;
        }

        _smokeTimer = new System.Windows.Forms.Timer { Interval = 25000 };
        _smokeTimer.Tick += (_, _) =>
        {
            _smokeTimer!.Stop();
            WriteSmoke(new JsonObject { ["ok"] = false, ["status"] = _status.Text, ["error"] = "window shell did not finish" });
            BeginInvoke(Close);
        };
        _smokeTimer.Start();
    }

    private int _navRetries;
    private int _navGeneration;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess && _navRetries < 1 && _webView.CoreWebView2 is not null)
        {
            // First navigation right after WebView2 init aborts from time to time; one retry is enough.
            _navRetries++;
            _navStart = Environment.TickCount64;
            try { _webView.CoreWebView2.Reload(); } catch (Exception) { }
            return;
        }

        var elapsed = Environment.TickCount64 - _navStart;
        var extra = args.IsSuccess ? " · 导航" + elapsed + "ms" : " · 导航失败";
        try
        {
            BeginInvoke(() =>
            {
                if (!string.IsNullOrEmpty(_baseStatus))
                {
                    _status.Text = _baseStatus + extra;
                }
            });
        }
        catch (Exception)
        {
            // Closing.
        }
    }

    private async Task FinishSmokeAsync(CoreWebView2NavigationCompletedEventArgs args, string? file = null)
    {
        file ??= _options.Smoke;
        var generation = _navGeneration;
        var report = new JsonObject { ["ok"] = false, ["status"] = _status.Text };
        if (!args.IsSuccess)
        {
            report["error"] = "navigation failed: " + args.WebErrorStatus;
            WriteSmoke(report, file);
            BeginInvoke(Close);
            return;
        }

        if (_options.SmokeCheck is not null)
        {
            // Give the page time to run its own pd.* calls, then scrape what actually rendered.
            // This is the only headless proof that the full page->host->kernel->host->page loop works.
            await Task.Delay(8000).ConfigureAwait(true);
            try
            {
                var envText = await _webView.CoreWebView2
                    .ExecuteScriptAsync("document.getElementById('env') ? document.getElementById('env').innerText.slice(0, 120) : '<no #env>'")
                    .ConfigureAwait(true);
                var versionText = await _webView.CoreWebView2
                    .ExecuteScriptAsync("document.getElementById('version') ? document.getElementById('version').innerText.slice(0, 120) : '<no #version>'")
                    .ConfigureAwait(true);
                report["envRendered"] = envText;
                report["versionRendered"] = versionText;
            }
            catch (Exception ex)
            {
                report["scrapeError"] = ex.Message;
            }
        }
        try
        {
            if (!args.IsSuccess)
            {
                report["error"] = "navigation failed: " + args.WebErrorStatus;
            }
            else
            {
                var value = await _webView.CoreWebView2
                    .ExecuteScriptAsync("typeof window.pd + ':' + (window.pd ? window.pd.theme : '')")
                    .ConfigureAwait(true);
                report["bridge"] = value;
                var url = await _webView.CoreWebView2
                    .ExecuteScriptAsync("location.href")
                    .ConfigureAwait(true);
                report["url"] = url;
                report["navMs"] = Environment.TickCount64 - _navStart;
                report["ok"] = value.Contains("object", StringComparison.OrdinalIgnoreCase)
                    && url.Contains("pd.local", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            report["error"] = ex.Message;
        }

        if (generation != _navGeneration)
        {
            // Superseded by a newer navigation; do not write a stale report.
            return;
        }

        WriteSmoke(report);
        var holdText = Environment.GetEnvironmentVariable("PD_WINDOW_SMOKE_HOLD_MS");
        if (int.TryParse(holdText, out var holdMs) && holdMs > 0)
        {
            await Task.Delay(holdMs).ConfigureAwait(true);
        }

        BeginInvoke(Close);
    }

    private void WriteSmoke(JsonObject report, string? file = null)
    {
        file ??= _options.SmokeCheck ?? _options.Smoke;
        if (_smokeWritten || file is null)
        {
            return;
        }

        _smokeWritten = true;
        _smokeTimer?.Stop();
        File.WriteAllText(file, report.ToJsonString());
    }

    private async Task ShowSelectedPageAsync()
    {
        if (_navigation.SelectedItem is not PageItem item || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await NavigateToPageAsync(item.Page).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Disposed during close (async void event path); never crash the process.
        }
    }

    private async Task NavigateToPageAsync(JsonObject page)
    {
        _navGeneration++;
        _navRetries = 0;
        var extension = JsonUtil.String(page, "extension") ?? string.Empty;
        var entry = JsonUtil.String(page, "entry") ?? string.Empty;
        if (entry.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || entry.Contains('\\')
            || entry.Contains("..", StringComparison.Ordinal)
            || entry.Contains(':')
            || entry.Contains('?')
            || entry.Contains('#')
            || entry.Contains('\0'))
        {
            throw new InvalidOperationException("unsafe page entry: " + entry);
        }

        while (entry.StartsWith("/", StringComparison.Ordinal))
        {
            entry = entry[1..];
        }

        var safePath = string.Join("/", entry.Split('/').Select(Uri.EscapeDataString));
        _bridge?.SetPageContext(extension);
        _navStart = Environment.TickCount64;
        _webView.CoreWebView2.Navigate("https://pd.local/" + Uri.EscapeDataString(extension) + "/" + safePath);
        _status.Text = (JsonUtil.String(page, "title") ?? extension) + " · " + extension;
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task NavigateToAsync(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return;
        }

        for (var index = 0; index < _pages.Count; index++)
        {
            var page = _pages[index];
            var extension = JsonUtil.String(page, "extension") ?? string.Empty;
            var id = JsonUtil.String(page, "id") ?? string.Empty;
            if (string.Equals(pageId, extension + ":" + id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pageId, id, StringComparison.OrdinalIgnoreCase))
            {
                SelectSidebarIndex(index);
                await NavigateToPageAsync(page).ConfigureAwait(true);
                return;
            }
        }

        throw new InvalidOperationException("unknown page: " + pageId);
    }

    private (string Theme, string Locale) ReadLook()
    {
        try
        {
            var path = Path.Combine(_options.Root, "config", "settings.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject settings)
            {
                // settings.json is nested ({"ui": {...}}); read the section first.
                var look = JsonUtil.Object(settings, "ui");
                var theme = JsonUtil.String(look, "theme") ?? "light";
                var locale = JsonUtil.String(look, "language") ?? "zh-CN";
                return (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light", locale);
            }
        }
        catch (Exception)
        {
            // Defaults below.
        }

        return ("dark", "zh-CN");
    }

    private void SelectSidebarIndex(int index)
    {
        if (index < 0 || index >= _navigation.Items.Count)
        {
            return;
        }

        _suppressNavChange = true;
        try
        {
            _navigation.SelectedIndex = index;
            _navigation.Invalidate();
        }
        finally
        {
            _suppressNavChange = false;
        }
    }

    private void ApplyShellTheme(bool isDark)
    {
        _darkTheme = isDark;
        // WinUI 3 native palette: Mica shell surface (#F3F3F3 / #202020) + content layer (#F9F9F9 / #272727).
        var contentBg = isDark ? Color.FromArgb(39, 39, 39) : Color.FromArgb(249, 249, 249);
        var sidebarBg = isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        var divider = isDark ? Color.FromArgb(49, 49, 49) : Color.FromArgb(229, 229, 229);
        var muted = isDark ? Color.FromArgb(158, 158, 158) : Color.FromArgb(118, 118, 118);

        SuspendLayout();
        try
        {
            BackColor = sidebarBg;
            _sidebar.BackColor = sidebarBg;
            _brandHeader.BackColor = sidebarBg;
            _navigation.BackColor = sidebarBg;
            _footerPanel.BackColor = sidebarBg;
            _footerDivider.BackColor = divider;
            _sidebarDivider.BackColor = divider;
            _status.BackColor = sidebarBg;
            _status.ForeColor = muted;
            try
            {
                _webView.DefaultBackgroundColor = contentBg;
            }
            catch (Exception)
            {
                // Ignore before handle initialization.
            }

            ApplyWindowTitleBarTheme(isDark);
        }
        finally
        {
            ResumeLayout(false);
        }

        _brandHeader.Invalidate();
        _navigation.Invalidate();
        _footerPanel.Invalidate();
    }

    private void ApplyWindowTitleBarTheme(bool isDark)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            var value = isDark ? 1 : 0;
            const int DwmwaUseImmersiveDarkMode = 20;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));

            // Request Windows 11 native Mica backdrop where supported (22H2+); harmlessly ignored on older builds.
            var backdrop = 2; // DWMSBT_MAINWINDOW
            const int DwmwaSystemBackdropType = 38;
            _ = DwmSetWindowAttribute(Handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows builds ignore these attributes safely.
        }
    }

    private void PaintBrandHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_brandHeader.BackColor);

        // WinUI 3 compact app emblem: 30x30 with 6px radius in Windows 11 Accent Blue.
        var badgeRect = new Rectangle(14, 15, 30, 30);
        var accent = _darkTheme ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);
        var badgeText = _darkTheme ? Color.FromArgb(16, 16, 16) : Color.White;
        using (var path = CreateRoundedRect(badgeRect, 6))
        using (var brush = new SolidBrush(accent))
        {
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, "PD", _brandBadgeFont, badgeRect, badgeText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var titleColor = _darkTheme ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);
        var subColor = _darkTheme ? Color.FromArgb(158, 158, 158) : Color.FromArgb(97, 97, 97);
        TextRenderer.DrawText(g, "PortableDev", _brandTitleFont, new Point(52, 13), titleColor, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Windows 便携开发环境", _brandSubFont, new Point(53, 33), subColor, TextFormatFlags.NoPadding);
    }

    private void OnNavigationMouseMove(object? sender, MouseEventArgs e)
    {
        var index = _navigation.IndexFromPoint(e.Location);
        if (index == _hoverIndex)
        {
            return;
        }

        var previous = _hoverIndex;
        _hoverIndex = index;
        if (previous >= 0 && previous < _navigation.Items.Count)
        {
            _navigation.Invalidate(_navigation.GetItemRectangle(previous));
        }

        if (_hoverIndex >= 0 && _hoverIndex < _navigation.Items.Count)
        {
            _navigation.Invalidate(_navigation.GetItemRectangle(_hoverIndex));
        }
    }

    private void OnNavigationMouseLeave(object? sender, EventArgs e)
    {
        if (_hoverIndex < 0)
        {
            return;
        }

        var previous = _hoverIndex;
        _hoverIndex = -1;
        if (previous < _navigation.Items.Count)
        {
            _navigation.Invalidate(_navigation.GetItemRectangle(previous));
        }
    }

    private void DrawNavigationItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _navigation.Items.Count)
        {
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var sidebarBg = _navigation.BackColor;
        using (var bgBrush = new SolidBrush(sidebarBg))
        {
            g.FillRectangle(bgBrush, e.Bounds);
        }

        // WinUI 3 NavigationView item: 5px rounded subtle fill + 3x16 centered accent pill on left.
        var selected = (e.State & DrawItemState.Selected) != 0;
        var hovered = e.Index == _hoverIndex && !selected;
        var pillRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 2, e.Bounds.Width - 16, e.Bounds.Height - 4);

        if (selected)
        {
            var pillColor = _darkTheme ? Color.FromArgb(45, 45, 45) : Color.FromArgb(233, 233, 233);
            var accentColor = _darkTheme ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);
            using (var pillPath = CreateRoundedRect(pillRect, 5))
            using (var pillBrush = new SolidBrush(pillColor))
            {
                g.FillPath(pillBrush, pillPath);
            }

            var barRect = new Rectangle(pillRect.X + 1, pillRect.Y + (pillRect.Height - 16) / 2, 3, 16);
            using (var barPath = CreateRoundedRect(barRect, 2))
            using (var barBrush = new SolidBrush(accentColor))
            {
                g.FillPath(barBrush, barPath);
            }
        }
        else if (hovered)
        {
            var hoverColor = _darkTheme ? Color.FromArgb(41, 41, 41) : Color.FromArgb(236, 236, 236);
            using var hoverPath = CreateRoundedRect(pillRect, 5);
            using var hoverBrush = new SolidBrush(hoverColor);
            g.FillPath(hoverBrush, hoverPath);
        }

        var item = _navigation.Items[e.Index] as PageItem;
        var title = item?.Title ?? _navigation.Items[e.Index]?.ToString() ?? string.Empty;
        var iconName = item?.Icon ?? "box";

        var fgColor = selected
            ? (_darkTheme ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26))
            : (_darkTheme ? Color.FromArgb(214, 214, 214) : Color.FromArgb(60, 60, 60));

        var iconRect = new Rectangle(pillRect.X + 13, pillRect.Y + (pillRect.Height - 16) / 2, 16, 16);
        DrawNavIcon(g, iconName, iconRect, fgColor);

        var textRect = new Rectangle(iconRect.Right + 11, pillRect.Y, pillRect.Width - 42, pillRect.Height);
        TextRenderer.DrawText(g, title, selected ? _navActiveFont : _navFont, textRect, fgColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static void DrawNavIcon(Graphics g, string icon, Rectangle r, Color color)
    {
        using var pen = new Pen(color, 1.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (icon)
        {
            case "home":
                g.DrawLines(pen, [
                    new PointF(r.Left + 2, r.Top + 8),
                    new PointF(r.Left + 8, r.Top + 2.5f),
                    new PointF(r.Right - 2, r.Top + 8),
                ]);
                g.DrawLines(pen, [
                    new PointF(r.Left + 4, r.Top + 7.5f),
                    new PointF(r.Left + 4, r.Bottom - 2.5f),
                    new PointF(r.Right - 4, r.Bottom - 2.5f),
                    new PointF(r.Right - 4, r.Top + 7.5f),
                ]);
                break;
            case "compass":
                g.DrawEllipse(pen, r.Left + 1.5f, r.Top + 1.5f, 13, 13);
                g.DrawPolygon(pen, [
                    new PointF(r.Left + 10.5f, r.Top + 5.5f),
                    new PointF(r.Left + 8.8f, r.Top + 9.2f),
                    new PointF(r.Left + 5.5f, r.Top + 10.5f),
                    new PointF(r.Left + 7.2f, r.Top + 6.8f),
                ]);
                break;
            case "globe":
                g.DrawEllipse(pen, r.Left + 1.5f, r.Top + 1.5f, 13, 13);
                g.DrawEllipse(pen, r.Left + 5f, r.Top + 1.5f, 6, 13);
                g.DrawLine(pen, r.Left + 1.5f, r.Top + 8f, r.Right - 1.5f, r.Top + 8f);
                break;
            case "cpu":
                g.DrawRectangle(pen, r.Left + 4, r.Top + 4, 8, 8);
                g.DrawLine(pen, r.Left + 6, r.Top + 1.5f, r.Left + 6, r.Top + 4);
                g.DrawLine(pen, r.Left + 10, r.Top + 1.5f, r.Left + 10, r.Top + 4);
                g.DrawLine(pen, r.Left + 6, r.Bottom - 4, r.Left + 6, r.Bottom - 1.5f);
                g.DrawLine(pen, r.Left + 10, r.Bottom - 4, r.Left + 10, r.Bottom - 1.5f);
                g.DrawLine(pen, r.Left + 1.5f, r.Top + 6, r.Left + 4, r.Top + 6);
                g.DrawLine(pen, r.Left + 1.5f, r.Top + 10, r.Left + 4, r.Top + 10);
                g.DrawLine(pen, r.Right - 4, r.Top + 6, r.Right - 1.5f, r.Top + 6);
                g.DrawLine(pen, r.Right - 4, r.Top + 10, r.Right - 1.5f, r.Top + 10);
                break;
            case "list":
                g.DrawLine(pen, r.Left + 5.5f, r.Top + 4f, r.Right - 2, r.Top + 4f);
                g.DrawLine(pen, r.Left + 5.5f, r.Top + 8f, r.Right - 2, r.Top + 8f);
                g.DrawLine(pen, r.Left + 5.5f, r.Top + 12f, r.Right - 2, r.Top + 12f);
                g.DrawLine(pen, r.Left + 2f, r.Top + 4f, r.Left + 3f, r.Top + 4f);
                g.DrawLine(pen, r.Left + 2f, r.Top + 8f, r.Left + 3f, r.Top + 8f);
                g.DrawLine(pen, r.Left + 2f, r.Top + 12f, r.Left + 3f, r.Top + 12f);
                break;
            case "shield":
                g.DrawPolygon(pen, [
                    new PointF(r.Left + 8, r.Top + 2),
                    new PointF(r.Right - 2.5f, r.Top + 4.5f),
                    new PointF(r.Right - 3f, r.Top + 9.5f),
                    new PointF(r.Left + 8, r.Bottom - 2),
                    new PointF(r.Left + 3f, r.Top + 9.5f),
                    new PointF(r.Left + 2.5f, r.Top + 4.5f),
                ]);
                break;
            case "sliders":
                g.DrawLine(pen, r.Left + 2, r.Top + 4.5f, r.Right - 2, r.Top + 4.5f);
                g.DrawLine(pen, r.Left + 2, r.Top + 11.5f, r.Right - 2, r.Top + 11.5f);
                g.DrawEllipse(pen, r.Left + 4.5f, r.Top + 2.5f, 4, 4);
                g.DrawEllipse(pen, r.Left + 8.5f, r.Top + 9.5f, 4, 4);
                break;
            default: // box
                g.DrawPolygon(pen, [
                    new PointF(r.Left + 8, r.Top + 2),
                    new PointF(r.Right - 2, r.Top + 5.2f),
                    new PointF(r.Right - 2, r.Bottom - 5.2f),
                    new PointF(r.Left + 8, r.Bottom - 2),
                    new PointF(r.Left + 2, r.Bottom - 5.2f),
                    new PointF(r.Left + 2, r.Top + 5.2f),
                ]);
                g.DrawLine(pen, r.Left + 2, r.Top + 5.2f, r.Left + 8, r.Top + 8.2f);
                g.DrawLine(pen, r.Right - 2, r.Top + 5.2f, r.Left + 8, r.Top + 8.2f);
                g.DrawLine(pen, r.Left + 8, r.Top + 8.2f, r.Left + 8, r.Bottom - 2);
                break;
        }
    }

    private static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private sealed class SmoothPanel : Panel
    {
        public SmoothPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
        }
    }

    /// <summary>Flicker-free owner-drawn ListBox: paints empty area cleanly without WM_ERASEBKGND flashes.</summary>
    private sealed class SmoothNavListBox : ListBox
    {
        private const int WmEraseBkgnd = 0x0014;

        public SmoothNavListBox()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            UpdateStyles();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmEraseBkgnd)
            {
                m.Result = (IntPtr)1;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(BackColor))
            {
                e.Graphics.FillRectangle(bg, ClientRectangle);
            }

            for (var index = 0; index < Items.Count; index++)
            {
                var rect = GetItemRectangle(index);
                if (!e.ClipRectangle.IntersectsWith(rect))
                {
                    continue;
                }

                var state = SelectedIndex == index ? DrawItemState.Selected : DrawItemState.Default;
                OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, rect, index, state, ForeColor, BackColor));
            }
        }
    }

    private sealed record PageItem(string Title, string Icon, JsonObject Page)
    {
        public override string ToString() => Title;
    }
}