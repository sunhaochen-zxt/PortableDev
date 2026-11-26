using System.Globalization;
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
    private readonly ListBox _navigation = new();
    private readonly Label _status = new();
    private readonly List<JsonObject> _pages = new();
    private WebView2 _webView = new();
    private KernelLink? _link;
    private bool _initialized;
    private long _navStart;
    private string _baseStatus = string.Empty;
    private bool _smokeWritten;
    private System.Windows.Forms.Timer? _smokeTimer;

    public MainForm(WindowOptions options)
    {
        _options = options;
        Text = "PortableDev";
        Width = 1200;
        Height = 760;

        _navigation.Dock = DockStyle.Left;
        _navigation.Width = 190;
        _navigation.IntegralHeight = false;
        _navigation.SelectedIndexChanged += async (_, _) => await ShowSelectedPageAsync().ConfigureAwait(true);

        _status.Dock = DockStyle.Top;
        _status.Height = 26;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(8, 0, 0, 0);
        _status.Text = "正在连接内核…";

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);
        Controls.Add(_navigation);
        Controls.Add(_status);
        Shown += async (_, _) => await InitializeAsync().ConfigureAwait(true);
        FormClosed += (_, _) => DisposeLink();
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
            _link = await KernelLink.ConnectAsync(_options.Root, _options.KernelDll).ConfigureAwait(true);
            var pages = await _link.RequestPagesAsync().ConfigureAwait(true);
            foreach (var node in pages)
            {
                if (node is not JsonObject page)
                {
                    continue;
                }

                _pages.Add(page);
                _navigation.Items.Add(new PageItem(JsonUtil.String(page, "title") ?? "页面", page));
            }

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
            var bridge = new Bridge(_webView, _link, NavigateToAsync);
            await bridge.AttachAsync(look.Theme, look.Locale).ConfigureAwait(true);

            core.NavigationCompleted += OnNavigationCompleted;
            if (_options.Smoke is not null || _options.SmokeCheck is not null)
            {
                var checkFile = _options.SmokeCheck ?? _options.Smoke;
                core.NavigationCompleted += async (_, eventArgs) => await FinishSmokeAsync(eventArgs, checkFile).ConfigureAwait(true);
            }

            // Navigate explicitly: relying on SelectedIndexChanged is fragile (adding items
            // can already leave the selection at 0, in which case no event fires and the view
            // stays blank until the user's first click).
            _navigation.SelectedIndex = 0;
            _baseStatus = "内核 " + _link.KernelVersion + " · " + Path.GetFileName(_options.Root);
            _status.Text = _baseStatus;
            await NavigateToPageAsync(_pages[0]).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HandleFailure(ex);
        }
    }

    private async Task<bool> EnsureWebViewAsync()
    {
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

        _webView = new WebView2 { Dock = DockStyle.Fill };
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

        await NavigateToPageAsync(item.Page).ConfigureAwait(true);
    }

    private async Task NavigateToPageAsync(JsonObject page)
    {
        var extension = JsonUtil.String(page, "extension") ?? string.Empty;
        var entry = JsonUtil.String(page, "entry") ?? string.Empty;
        while (entry.StartsWith("/", StringComparison.Ordinal))
        {
            entry = entry[1..];
        }

        _navStart = Environment.TickCount64;
        _webView.CoreWebView2.Navigate("https://pd.local/" + Uri.EscapeDataString(extension) + "/" + entry);
        _status.Text = (JsonUtil.String(page, "title") ?? extension) + " · " + extension;
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task NavigateToAsync(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return;
        }

        foreach (var page in _pages)
        {
            var extension = JsonUtil.String(page, "extension") ?? string.Empty;
            var id = JsonUtil.String(page, "id") ?? string.Empty;
            if (string.Equals(pageId, extension + ":" + id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pageId, id, StringComparison.OrdinalIgnoreCase))
            {
                await NavigateToPageAsync(page).ConfigureAwait(true);
                return;
            }
        }
    }

    private (string Theme, string Locale) ReadLook()
    {
        try
        {
            var path = Path.Combine(_options.Root, "config", "settings.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject settings)
            {
                var theme = JsonUtil.String(settings, "ui.theme") ?? "light";
                var locale = JsonUtil.String(settings, "ui.language") ?? "zh-CN";
                return (theme == "light" ? "light" : "dark", locale);
            }
        }
        catch (Exception)
        {
            // Defaults below.
        }

        return ("dark", "zh-CN");
    }

    private sealed record PageItem(string Title, JsonObject Page)
    {
        public override string ToString() => Title;
    }
}
