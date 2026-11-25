using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PortableDev.Core.Util;

namespace PortableDev.Window;

/// <summary>The window shell: a thin container that loads pages provided by extensions.</summary>
public sealed class MainForm : Form
{
    private readonly WindowOptions _options;
    private readonly WebView2 _webView = new();
    private readonly ListBox _navigation = new();
    private readonly Label _status = new();
    private readonly List<JsonObject> _pages = new();
    private KernelLink? _link;
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
        Load += async (_, _) => await InitializeAsync().ConfigureAwait(true);
    }

    private async Task InitializeAsync()
    {
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

            if (_options.Smoke is not null)
            {
                _smokeTimer = new System.Windows.Forms.Timer { Interval = 25000 };
                _smokeTimer.Tick += (_, _) =>
                {
                    _smokeTimer!.Stop();
                    WriteSmoke(new JsonObject { ["ok"] = false, ["status"] = _status.Text, ["error"] = "window shell did not finish" });
                    BeginInvoke(Close);
                };
                _smokeTimer.Start();
            }

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

            var dataFolder = Path.Combine(Path.GetTempPath(), "pd-window-data");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("pd.local", _options.Extensions, CoreWebView2HostResourceAccessKind.Allow);

            var look = ReadLook();
            var bridge = new Bridge(_webView, _link, NavigateToAsync);
            await bridge.AttachAsync(look.Theme, look.Locale).ConfigureAwait(true);

            if (_options.Smoke is not null)
            {
                core.NavigationCompleted += async (_, eventArgs) => await FinishSmokeAsync(eventArgs).ConfigureAwait(true);
            }

            _navigation.SelectedIndex = 0;
            _status.Text = "内核 " + _link.KernelVersion + " · " + Path.GetFileName(_options.Root.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception ex)
        {
            _status.Text = "窗口壳启动失败: " + ex.Message;
            if (_options.Smoke is not null)
            {
                WriteSmoke(new JsonObject { ["ok"] = false, ["status"] = _status.Text, ["error"] = ex.Message });
                BeginInvoke(Close);
            }
        }
    }

    private async Task FinishSmokeAsync(CoreWebView2NavigationCompletedEventArgs args)
    {
        var report = new JsonObject { ["ok"] = false, ["status"] = _status.Text };
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
                report["ok"] = value.Contains("object", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            report["error"] = ex.Message;
        }

        WriteSmoke(report);
        BeginInvoke(Close);
    }

    private void WriteSmoke(JsonObject report)
    {
        if (_smokeWritten || _options.Smoke is null)
        {
            return;
        }

        _smokeWritten = true;
        _smokeTimer?.Stop();
        File.WriteAllText(_options.Smoke, report.ToJsonString());
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
        if (entry.StartsWith("/", StringComparison.Ordinal))
        {
            entry = entry[1..];
        }

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
                var theme = JsonUtil.String(settings, "ui.theme") ?? "dark";
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
