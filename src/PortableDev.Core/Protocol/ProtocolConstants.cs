namespace PortableDev.Core.Protocol;

/// <summary>Wire-protocol constants agreed in docs/spec.md section 3.</summary>
public static class ProtocolConstants
{
    /// <summary>Envelope version (`v`).</summary>
    public const int Version = 1;

    /// <summary>Kernel/extension interface version, compared by `requiresKernel`.</summary>
    public const int KernelAbi = 1;

    /// <summary>Hard frame limit: 16 MiB, per spec 3.2.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public const string DefaultPipePrefix = "portabledev";

    public const string DefaultStableRoot = @"C:\Dev";

    public const string KernelVersion = "2.0.0";

    /// <summary>ISO-8601 with offset, as used by `ts`.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    /// <summary>System task id used by extension state events (spec 4.3).</summary>
    public const string SystemTaskId = "system";
}

/// <summary>Envelope `kind` values.</summary>
public static class Kinds
{
    public const string Hello = "hello";
    public const string HelloOk = "hello.ok";
    public const string HelloErr = "hello.err";
    public const string Req = "req";
    public const string Evt = "evt";
    public const string Ctl = "ctl";
    public const string Ack = "ack";
    public const string ExtInvoke = "ext.invoke";
    public const string ExtReady = "ext.ready";
    public const string ExtResult = "ext.result";
    public const string Call = "call";
    public const string CallOk = "call.ok";
    public const string CallErr = "call.err";
}

/// <summary>Event `type` values carried inside `evt.data`.</summary>
public static class EventTypes
{
    public const string Progress = "progress";
    public const string Log = "log";
    public const string Table = "table";
    public const string Prompt = "prompt";
    public const string Done = "done";
    public const string Step = "step";
    public const string Link = "link";
    public const string Kv = "kv";
    public const string Tree = "tree";
    public const string Notice = "notice";
    public const string Ext = "ext";
}

/// <summary>Client/interop feature flags announced during `hello`.</summary>
public static class Features
{
    public const string Json = "json";
    public const string Ansi = "ansi";
    public const string Prompt = "prompt";
    public const string Progress = "progress";
    public const string Unicode = "unicode";
    public const string Link = "link";
    public const string Cancel = "cancel";
    public const string Subscribe = "subscribe";
    public const string Grants = "grants";
    public const string ExtEvents = "ext-events";
}

/// <summary>`ctl` actions, per spec 4.7 and 5.4.</summary>
public static class CtlActions
{
    public const string Cancel = "cancel";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
    public const string PromptReply = "prompt_reply";
    public const string GrantRequest = "grant.request";
    public const string GrantReply = "grant.reply";
    public const string LogWrite = "log.write";
}
