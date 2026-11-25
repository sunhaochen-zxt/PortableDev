using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;

namespace PortableDev.Kernel.Transport;

/// <summary>Tracks connections, in-flight tasks and subscriptions (spec 4.7).</summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, IMessageSink> _sinks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IMessageSink> _taskOwners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IMessageSink>> _subscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dispatch.TaskContext> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dispatch.TaskContext> _extensionTasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _extSubscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _extensions = new(StringComparer.Ordinal);

    public int ConnectionCount => _sinks.Count;

    /// <summary>Connections that are real clients; extension pipes must not keep the kernel alive.</summary>
    public int ClientConnectionCount => _sinks.Count - _extensions.Count;

    public void MarkExtension(string sessionId) => _extensions[sessionId] = true;

    public void Register(string sessionId, IMessageSink sink) => _sinks[sessionId] = sink;

    public void Unregister(string sessionId)
    {
        _sinks.TryRemove(sessionId, out _);
        _extensions.TryRemove(sessionId, out _);
        _extSubscribers.TryRemove(sessionId, out _);
        foreach (var (taskId, owners) in _subscribers)
        {
            owners.TryRemove(sessionId, out _);
            if (owners.IsEmpty)
            {
                _subscribers.TryRemove(taskId, out _);
            }
        }
    }

    public void RegisterTask(Dispatch.TaskContext task)
    {
        _tasks[task.Key] = task;
        _extensionTasks[task.ExtensionId] = task;
        _taskOwners[task.Key] = task.Sink;
    }

    public Dispatch.TaskContext? FindTask(string key) => _tasks.GetValueOrDefault(key);

    /// <summary>Looks a task up by the id the extension echoes back (session-scoped, path-safe).</summary>
    public Dispatch.TaskContext? FindTaskByExtensionId(string extensionId) => _extensionTasks.GetValueOrDefault(extensionId);

    /// <summary>Removes the task only if it is still the registered instance (ids may be reused).</summary>
    public void CompleteTask(Dispatch.TaskContext task)
    {
        if (_tasks.TryGetValue(task.Key, out var current) && ReferenceEquals(current, task))
        {
            _tasks.TryRemove(task.Key, out _);
            _extensionTasks.TryRemove(task.ExtensionId, out _);
            _taskOwners.TryRemove(task.Key, out _);
            if (_subscribers.TryRemove(task.Key, out var owners))
            {
                owners.Clear();
            }
        }
    }

    /// <summary>Finds the registry key for a wire task id, optionally constrained to one session.</summary>
    public string? ResolveKey(string taskId, string? sessionId = null)
    {
        foreach (var (key, task) in _tasks)
        {
            if (string.Equals(task.Id, taskId, StringComparison.Ordinal) &&
                (sessionId is null || string.Equals(task.SessionId, sessionId, StringComparison.Ordinal)))
            {
                return key;
            }
        }

        return null;
    }

    public void SubscribeTask(string taskId, string sessionId, IMessageSink sink)
    {
        var owners = _subscribers.GetOrAdd(taskId, _ => new ConcurrentDictionary<string, IMessageSink>(StringComparer.Ordinal));
        owners[sessionId] = sink;
    }

    public void UnsubscribeTask(string taskId, string sessionId)
    {
        if (_subscribers.TryGetValue(taskId, out var owners))
        {
            owners.TryRemove(sessionId, out _);
        }
    }

    public void SubscribeExtensions(string sessionId) => _extSubscribers[sessionId] = true;

    public void UnsubscribeExtensions(string sessionId) => _extSubscribers.TryRemove(sessionId, out _);

    /// <summary>Sends one task event to the owner and every subscriber; a `done` event settles the task.</summary>
    public async Task PublishAsync(string taskId, Envelope envelope)
    {
        if (_taskOwners.TryGetValue(taskId, out var owner))
        {
            await SafeSendAsync(owner, envelope).ConfigureAwait(false);
        }

        if (_subscribers.TryGetValue(taskId, out var subscribers))
        {
            foreach (var (_, sink) in subscribers)
            {
                await SafeSendAsync(sink, envelope).ConfigureAwait(false);
            }
        }

        if (IsDone(envelope) && _tasks.TryGetValue(taskId, out var task))
        {
            task.MarkDoneFromExtension();
            var exitCode = ReadExitCode(envelope.Data, out var ok);
            task.Complete(ok ? exitCode : (exitCode == 0 ? ExitCodes.Internal : exitCode), envelope.Data["error"] as JsonObject, null);
        }
    }

    /// <summary>Sends an extension-status event to every session that subscribed with `scope: ext`.</summary>
    public async Task BroadcastExtensionEventAsync(Envelope envelope)
    {
        foreach (var (sessionId, _) in _extSubscribers)
        {
            if (_sinks.TryGetValue(sessionId, out var sink))
            {
                await SafeSendAsync(sink, envelope).ConfigureAwait(false);
            }
        }
    }

    private static bool IsDone(Envelope envelope)
        => envelope.Data["type"] is JsonValue value && value.TryGetValue<string>(out var type) && type == EventTypes.Done;

    private static int ReadExitCode(JsonObject data, out bool ok)
    {
        ok = data["ok"] is JsonValue okNode && okNode.TryGetValue<bool>(out var flag) && flag;
        return data["exitCode"] is JsonValue codeNode && codeNode.TryGetValue<int>(out var code) ? code : ExitCodes.Internal;
    }

    private static async Task SafeSendAsync(IMessageSink sink, Envelope envelope)
    {
        try
        {
            await sink.SendAsync(envelope).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Peer gone or shutting down; the connection loop performs the cleanup.
        }
    }
}
