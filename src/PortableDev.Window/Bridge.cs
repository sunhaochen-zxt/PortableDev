using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

namespace PortableDev.Window;

/// <summary>WebView2 to kernel bridge: pages call window.pd, the host relays to the kernel.</summary>
public sealed class Bridge
{
    private readonly WebView2 _webView;
    private readonly KernelLink _link;
    private readonly Func<string, Task> _navigate;
    private readonly ConcurrentDictionary<string, string> _prompts = new(StringComparer.Ordinal);

    public Bridge(WebView2 webView, KernelLink link, Func<string, Task> navigate)
    {
        _webView = webView;
        _link = link;
        _navigate = navigate;
    }

    public async Task AttachAsync(string theme, string locale)
    {
        var core = _webView.CoreWebView2;
        core.WebMessageReceived += OnWebMessage;
        _link.EventReceived += OnKernelEvent;
        var script = LoadScript()
            .Replace("__PD_THEME__", theme, StringComparison.Ordinal)
            .Replace("__PD_LOCALE__", locale, StringComparison.Ordinal);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(script).ConfigureAwait(true);
    }

    private void OnKernelEvent(string taskId, JsonObject data)
    {
        if (JsonUtil.String(data, "type") == EventTypes.Prompt)
        {
            var promptId = JsonUtil.String(data, "promptId");
            if (promptId is not null)
            {
                _prompts[promptId] = taskId;
            }
        }

        Post(new JsonObject
        {
            ["type"] = "event",
            ["taskId"] = taskId,
            ["event"] = data.DeepClone(),
        });
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(args.WebMessageAsJson) as JsonObject;
        }
        catch (Exception)
        {
            return;
        }

        var id = message is null ? null : JsonUtil.String(message, "id");
        if (message is null || id is null)
        {
            return;
        }

        _ = HandleAsync(id, JsonUtil.String(message, "method") ?? string.Empty, JsonUtil.Object(message, "params") ?? new JsonObject());
    }

    private async Task HandleAsync(string id, string method, JsonObject parameters)
    {
        try
        {
            switch (method)
            {
                case "exec":
                    Reply(id, new JsonObject { ["taskId"] = await _link.ExecAsync(Strings(parameters)).ConfigureAwait(false) });
                    return;
                case "run":
                {
                    var events = new JsonArray();
                    var timeout = JsonUtil.Int(parameters, "timeoutMs") ?? 180000;
                    var done = await _link.RunWithEventsAsync(Strings(parameters), events, timeout).ConfigureAwait(false);
                    Reply(id, new JsonObject { ["done"] = done.DeepClone(), ["events"] = events });
                    return;
                }
                case "cancel":
                    await _link.CancelAsync(JsonUtil.String(parameters, "taskId") ?? string.Empty).ConfigureAwait(false);
                    Reply(id, new JsonObject());
                    return;
                case "reply":
                {
                    var promptId = JsonUtil.String(parameters, "promptId") ?? string.Empty;
                    var taskId = _prompts.TryGetValue(promptId, out var known)
                        ? known
                        : JsonUtil.String(parameters, "taskId") ?? string.Empty;
                    await _link.ReplyAsync(taskId, promptId, parameters["value"]?.DeepClone()).ConfigureAwait(false);
                    _prompts.TryRemove(promptId, out _);
                    Reply(id, new JsonObject());
                    return;
                }
                case "env":
                    Reply(id, new JsonObject
                    {
                        ["kernelVersion"] = _link.KernelVersion,
                        ["root"] = _link.Root,
                        ["stickId"] = _link.StickId,
                        ["stableRoot"] = _link.StableRoot,
                    });
                    return;
                case "navigate":
                    await _navigate(JsonUtil.String(parameters, "page") ?? string.Empty).ConfigureAwait(false);
                    Reply(id, new JsonObject());
                    return;
                default:
                    throw new InvalidOperationException("unknown bridge method: " + method);
            }
        }
        catch (Exception ex)
        {
            Post(new JsonObject
            {
                ["type"] = "reply",
                ["id"] = id,
                ["ok"] = false,
                ["error"] = new JsonObject { ["code"] = "ERR_IO", ["message"] = ex.Message },
            });
        }
    }

    private static string[] Strings(JsonObject parameters)
        => JsonUtil.Strings(JsonUtil.Array(parameters, "argv")).ToArray();

    private void Reply(string id, JsonObject result)
        => Post(new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = true, ["result"] = result });

    private void Post(JsonObject message)
    {
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(message.ToJsonString());
        }
        catch (Exception)
        {
            // The page is gone or the view is closing.
        }
    }

    private static string LoadScript()
    {
        using var stream = typeof(Bridge).Assembly.GetManifestResourceStream("PortableDev.Window.pd-bridge.js")
            ?? throw new InvalidOperationException("bridge script is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
