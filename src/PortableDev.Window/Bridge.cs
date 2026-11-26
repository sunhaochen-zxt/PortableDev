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
    private readonly ConcurrentDictionary<string, bool> _ownTasks = new(StringComparer.Ordinal);
    private bool _extSubscribed;
    private readonly Dictionary<string, ExtPolicy> _policies;
    private string? _pageExtension;

    /// <summary>Global read-only commands any page may call (spec 3.2).</summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "list", "ls", "info", "search", "outdated", "doctor", "help", "env", "version", "log", "ui",
    };

    /// <summary>Flags that make an otherwise read-only command destructive; pages never get these.</summary>
    private static readonly Dictionary<string, string[]> DeniedFlags = new(StringComparer.Ordinal)
    {
        ["log"] = ["--vacuum"],
        ["doctor"] = ["--fix"],
        ["gc"] = ["--blobs", "--keep"],
    };

    public Bridge(WebView2 webView, KernelLink link, Func<string, Task> navigate, Dictionary<string, ExtPolicy> policies)
    {
        _webView = webView;
        _link = link;
        _navigate = navigate;
        _policies = policies;
    }

    /// <summary>Called by the shell on every navigation so commands are attributed to the right extension.</summary>
    public void SetPageContext(string extension) => _pageExtension = extension;

    /// <summary>Loads per-extension command policies from the installed ext.json files.</summary>
    public static Dictionary<string, ExtPolicy> LoadPolicies(string extensionsDir)
    {
        var policies = new Dictionary<string, ExtPolicy>(StringComparer.Ordinal);
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(extensionsDir);
        }
        catch (Exception)
        {
            return policies;
        }

        foreach (var directory in directories)
        {
            try
            {
                var manifestPath = Path.Combine(directory, "ext.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest)
                {
                    continue;
                }

                var name = JsonUtil.String(manifest, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var own = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in JsonUtil.Array(manifest, "commands") ?? new JsonArray())
                {
                    if (node is not JsonObject command)
                    {
                        continue;
                    }

                    var commandName = JsonUtil.String(command, "name");
                    if (!string.IsNullOrWhiteSpace(commandName))
                    {
                        own.Add(commandName);
                    }

                    foreach (var alias in JsonUtil.Strings(JsonUtil.Array(command, "aliases")))
                    {
                        own.Add(alias);
                    }
                }

                HashSet<string>? allowed = null;
                var uiAllowed = false;
                foreach (var node in JsonUtil.Array(manifest, "permissions") ?? new JsonArray())
                {
                    if (node is not JsonObject permission)
                    {
                        continue;
                    }

                    var kind = JsonUtil.String(permission, "kind");
                    if (string.Equals(kind, "ui", StringComparison.Ordinal))
                    {
                        // spec 4.4: only an extension that declared the ui permission may
                        // drive the kernel from its pages.
                        uiAllowed = JsonUtil.Bool(permission, "allow") == true;
                        continue;
                    }

                    if (!string.Equals(kind, "commands", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    allowed ??= new HashSet<string>(StringComparer.Ordinal);
                    foreach (var allowedCommand in JsonUtil.Strings(JsonUtil.Array(permission, "allow")))
                    {
                        allowed.Add(allowedCommand);
                    }
                }

                policies[name] = new ExtPolicy(own, allowed, uiAllowed);
            }
            catch (Exception)
            {
                // A broken manifest must not break the shell; the kernel reports it separately.
            }
        }

        return policies;
    }

    public async Task AttachAsync(string theme, string locale)
    {
        var core = _webView.CoreWebView2;
        core.WebMessageReceived += OnWebMessage;
        _link.EventReceived += OnKernelEvent;
        // The placeholders sit inside quoted JS string literals, so the replacement values
        // must already be JSON-encoded (the caller serializes them) including their quotes.
        var script = LoadScript()
            .Replace("\"__PD_THEME__\"", theme, StringComparison.Ordinal)
            .Replace("\"__PD_LOCALE__\"", locale, StringComparison.Ordinal);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(script).ConfigureAwait(true);
    }

    private void OnKernelEvent(string taskId, JsonObject data)
    {
        if (string.Equals(JsonUtil.String(data, "type"), EventTypes.Done, StringComparison.Ordinal))
        {
            _ownTasks.TryRemove(taskId, out _);
            foreach (var (promptId, owner) in _prompts)
            {
                if (string.Equals(owner, taskId, StringComparison.Ordinal))
                {
                    _prompts.TryRemove(promptId, out _);
                }
            }
        }

        if (_prompts.Count > 512)
        {
            _prompts.Clear();
        }

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
        // Only pages served from the shell's own virtual host may talk to the bridge.
        if (args.Source is null || !args.Source.StartsWith("https://pd.local/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

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
                {
                    var argv = Strings(parameters);
                    DenyUnlessAllowed(argv);
                    var taskId = await _link.ExecAsync(argv).ConfigureAwait(false);
                    _ownTasks[taskId] = true;
                    Reply(id, new JsonObject { ["taskId"] = taskId });
                    return;
                }
                case "run":
                {
                    var argv = Strings(parameters);
                    DenyUnlessAllowed(argv);
                    var events = new JsonArray();
                    var timeout = JsonUtil.Int(parameters, "timeoutMs") ?? 180000;
                    // Register the task so prompt replies from this run are accepted (exec did
                    // this already; run used to leave its task unknown).
                    var done = await _link.RunWithEventsAsync(argv, events, timeout, MaxEvents,
                        taskId => _ownTasks[taskId] = true).ConfigureAwait(false);
                    Reply(id, new JsonObject { ["done"] = done.DeepClone(), ["events"] = events });
                    return;
                }
                case "cancel":
                {
                    var taskId = JsonUtil.String(parameters, "taskId") ?? string.Empty;
                    DenyUnlessOwned(taskId);
                    await _link.CancelAsync(taskId).ConfigureAwait(false);
                    Reply(id, new JsonObject());
                    return;
                }
                case "reply":
                {
                    var promptId = JsonUtil.String(parameters, "promptId") ?? string.Empty;
                    var taskId = _prompts.TryGetValue(promptId, out var known)
                        ? known
                        : JsonUtil.String(parameters, "taskId") ?? string.Empty;
                    DenyUnlessOwned(taskId);
                    await _link.ReplyAsync(taskId, promptId, parameters["value"]?.DeepClone()).ConfigureAwait(false);
                    _prompts.TryRemove(promptId, out _);
                    Reply(id, new JsonObject());
                    return;
                }
                case "on":
                {
                    // Subscriptions are page-local in the JS bridge; the host only needs to act
                    // for "ext", which requires a kernel-side subscription. Anything already
                    // subscribed stays subscribed (unsubscribe is page-local too).
                    var name = JsonUtil.String(parameters, "name");
                    if (string.Equals(name, "ext", StringComparison.Ordinal) && !_extSubscribed)
                    {
                        await _link.SubscribeExtAsync().ConfigureAwait(false);
                        _extSubscribed = true;
                    }

                    Reply(id, new JsonObject());
                    return;
                }
                case "off":
                    Reply(id, new JsonObject());
                    return;
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
            var code = ex is BridgeDeniedException denied ? denied.Code : "ERR_IO";
            Post(new JsonObject
            {
                ["type"] = "reply",
                ["id"] = id,
                ["ok"] = false,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message },
            });
        }
    }

    /// <summary>Bounds one pd.run call so a chatty task cannot grow the shell without limit.</summary>
    private const int MaxEvents = 5000;

    /// <summary>Pages may only run their own commands, granted commands, or global read-only ones.</summary>
    private void DenyUnlessAllowed(string[] argv)
    {
        if (IsAllowed(argv))
        {
            return;
        }

        throw new BridgeDeniedException("ERR_UNSUPPORTED",
            "the current page is not allowed to run '" + string.Join(' ', argv.Take(2)) + "'");
    }

    private bool IsAllowed(string[] argv)
    {
        var command = argv.Length > 0 ? argv[0] : string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        ExtPolicy? policy = null;
        if (_pageExtension is not null && _policies.TryGetValue(_pageExtension, out var found))
        {
            policy = found;
            if (!policy.UiAllowed)
            {
                return false;
            }
        }

        if (string.Equals(command, "config", StringComparison.Ordinal))
        {
            // window.md: read-only config forms are always exposed to UI-permitted pages;
            // mutating config subcommands require explicit commands permission.
            if (argv.Length > 1
                && (string.Equals(argv[1], "get", StringComparison.Ordinal) || string.Equals(argv[1], "list", StringComparison.Ordinal)))
            {
                return true;
            }

            return policy is not null
                && policy.AllowedCommands is not null
                && policy.AllowedCommands.Contains(command);
        }

        if (ReadOnlyCommands.Contains(command))
        {
            if (DeniedFlags.TryGetValue(command, out var denied))
            {
                foreach (var argument in argv.Skip(1))
                {
                    if (denied.Any(flag => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase)
                        || argument.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        if (policy is null)
        {
            return false;
        }

        return policy.OwnCommands.Contains(command)
            || (policy.AllowedCommands is not null && policy.AllowedCommands.Contains(command));
    }

    /// <summary>A page may only cancel or answer prompts for tasks it started itself.</summary>
    private void DenyUnlessOwned(string taskId)
    {
        if (!string.IsNullOrEmpty(taskId) && _ownTasks.ContainsKey(taskId))
        {
            return;
        }

        throw new BridgeDeniedException("ERR_BAD_ARGS", "unknown or foreign task");
    }

    private static string[] Strings(JsonObject parameters)
        => JsonUtil.Strings(JsonUtil.Array(parameters, "argv")).ToArray();

    private void Reply(string id, JsonObject result)
        => Post(new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = true, ["result"] = result });

    private void Post(JsonObject message)
    {
        // CoreWebView2 must be touched on the UI thread, but events and replies arrive on
        // pool threads. Posting directly either throws (swallowed below, the page hangs with
        // no error) or kills the pump when an event forward throws; marshal everything.
        var text = message.ToJsonString();
        try
        {
            _webView.BeginInvoke(new Action(() =>
            {
                try
                {
                    _webView.CoreWebView2?.PostWebMessageAsJson(text);
                }
                catch (Exception)
                {
                    // The page is gone or the view is closing.
                }
            }));
        }
        catch (Exception)
        {
            // The view is already disposed.
        }
    }

    /// <summary>Declared commands plus extra commands granted through permissions.</summary>
    public sealed record ExtPolicy(HashSet<string> OwnCommands, HashSet<string>? AllowedCommands, bool UiAllowed);

    /// <summary>A policy denial carries its own error code instead of surfacing as a generic IO failure.</summary>
    private sealed class BridgeDeniedException : Exception
    {
        public BridgeDeniedException(string code, string message) : base(message) => Code = code;

        public string Code { get; }
    }

    private static string LoadScript()
    {
        using var stream = typeof(Bridge).Assembly.GetManifestResourceStream("PortableDev.Window.pd-bridge.js")
            ?? throw new InvalidOperationException("bridge script is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
