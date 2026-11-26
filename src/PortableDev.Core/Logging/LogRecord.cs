using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortableDev.Core.Util;

namespace PortableDev.Core.Logging;

/// <summary>
/// One journal record. The wire shape is the contract (spec 5.3.1); storage is implementation-private,
/// so the on-disk format of a log provider is deliberately *not* part of the contract.
/// </summary>
public sealed class LogRecord
{
    public const int MaxBytes = 64 * 1024;

    public required string Ts { get; init; }

    public required int Pri { get; init; }

    public required string Level { get; init; }

    public string? Msg { get; init; }

    public string? Evt { get; init; }

    public long Seq { get; set; }

    public required string BootId { get; init; }

    public required string Unit { get; init; }

    public int? Pid { get; init; }

    public string? InvocationId { get; init; }

    public string? StickId { get; init; }

    public string? MachineId { get; init; }

    public string? Transport { get; init; }

    public string? Trace { get; init; }

    public string? Task { get; init; }

    public bool Imported { get; init; }

    public bool Truncated { get; init; }

    /// <summary>Record carries secret-looking values; encryption (log.encryption) will use this later.</summary>
    public bool Sensitive { get; init; }

    public JsonObject? Fields { get; set; }

    public static int Priority(LogLevel level) => level switch
    {
        LogLevel.Error => 3,
        LogLevel.Warn => 4,
        LogLevel.Debug => 7,
        _ => 6,
    };

    public static LogLevel LevelFromPriority(int pri) => pri switch
    {
        <= 3 => LogLevel.Error,
        4 => LogLevel.Warn,
        7 => LogLevel.Debug,
        _ => LogLevel.Info,
    };

    public JsonObject ToJson()
    {
        var record = new JsonObject
        {
            ["ts"] = Ts,
            ["pri"] = Pri,
            ["level"] = Level,
            ["seq"] = Seq,
            ["_bootId"] = BootId,
            ["_unit"] = Unit,
        };

        if (Msg is not null)
        {
            record["msg"] = Msg;
        }

        if (Evt is not null)
        {
            record["evt"] = Evt;
        }

        if (Pid is not null)
        {
            record["_pid"] = Pid.Value;
        }

        if (InvocationId is not null)
        {
            record["_invocationId"] = InvocationId;
        }

        if (StickId is not null)
        {
            record["_stickId"] = StickId;
        }

        if (MachineId is not null)
        {
            record["_machineId"] = MachineId;
        }

        if (Transport is not null)
        {
            record["_transport"] = Transport;
        }

        if (Trace is not null)
        {
            record["trace"] = Trace;
        }

        if (Task is not null)
        {
            record["task"] = Task;
        }

        if (Imported)
        {
            record["_imported"] = true;
        }

        if (Truncated)
        {
            record["_truncated"] = true;
        }

        if (Sensitive)
        {
            record["_sensitive"] = true;
        }

        if (Fields is not null)
        {
            foreach (var (name, value) in Fields)
            {
                if (!record.ContainsKey(name))
                {
                    record[name] = value?.DeepClone();
                }
            }
        }

        var bytes = Encoding.UTF8.GetByteCount(record.ToJsonString());
        if (bytes <= MaxBytes)
        {
            return record;
        }

        var compact = record.DeepClone() as JsonObject ?? new JsonObject();
        compact["msg"] = Truncate(JsonUtil.String(record, "msg") ?? string.Empty, 4096);
        compact["_truncated"] = true;
        while (Encoding.UTF8.GetByteCount(compact.ToJsonString()) > MaxBytes)
        {
            var removable = compact
                .Where(pair => !KnownKeys.Contains(pair.Key))
                .OrderByDescending(pair => pair.Value is null ? 0 : pair.Value.ToJsonString().Length)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (removable is null)
            {
                compact["msg"] = string.Empty;
                if (Encoding.UTF8.GetByteCount(compact.ToJsonString()) > MaxBytes)
                {
                    return new JsonObject
                    {
                        ["ts"] = Ts,
                        ["pri"] = Pri,
                        ["level"] = Level,
                        ["seq"] = Seq,
                        ["_bootId"] = BootId,
                        ["_unit"] = Unit,
                        ["_truncated"] = true,
                    };
                }

                break;
            }

            compact.Remove(removable);
        }

        return compact;
    }

    /// <summary>Parses a record from the wire; used by the provider contract tests and by replay.</summary>
    public static bool TryParse(JsonObject json, out LogRecord record)
    {
        record = null!;
        var ts = JsonUtil.String(json, "ts");
        var pri = JsonUtil.Int(json, "pri");
        var level = JsonUtil.String(json, "level");
        var bootId = JsonUtil.String(json, "_bootId");
        var unit = JsonUtil.String(json, "_unit");
        if (ts is null || pri is null || level is null || bootId is null || unit is null)
        {
            return false;
        }

        record = new LogRecord
        {
            Ts = ts,
            Pri = pri.Value,
            Level = level,
            Msg = JsonUtil.String(json, "msg"),
            Evt = JsonUtil.String(json, "evt"),
            Seq = json["seq"] is JsonValue seqValue && seqValue.TryGetValue<long>(out var seqNumber)
                ? seqNumber
                : (JsonUtil.Int(json, "seq") ?? 0),
            BootId = bootId,
            Unit = unit,
            Pid = JsonUtil.Int(json, "_pid"),
            InvocationId = JsonUtil.String(json, "_invocationId"),
            StickId = JsonUtil.String(json, "_stickId"),
            MachineId = JsonUtil.String(json, "_machineId"),
            Transport = JsonUtil.String(json, "_transport"),
            Trace = JsonUtil.String(json, "trace"),
            Task = JsonUtil.String(json, "task"),
            Imported = JsonUtil.Bool(json, "_imported") ?? false,
            Truncated = JsonUtil.Bool(json, "_truncated") ?? false,
            Sensitive = JsonUtil.Bool(json, "_sensitive") ?? false,
        };

        var fields = new JsonObject();
        foreach (var (name, value) in json)
        {
            if (!KnownKeys.Contains(name))
            {
                fields[name] = value?.DeepClone();
            }
        }

        if (fields.Count > 0)
        {
            record.Fields = fields;
        }

        return true;
    }

    /// <summary>
    /// Validates a record produced by an extension: strips trusted `_` fields it may not set,
    /// drops forbidden (environment/secret) fields, and enforces the size limit.
    /// </summary>
    public static LogRecord SanitizeExtension(JsonObject raw, string unit, string bootId, string stickId, string machineId, string? invocationId, int? pid, out int spoofed, out int redacted)
    {
        spoofed = 0;
        redacted = 0;
        var sensitive = false;
        var clean = new JsonObject();
        foreach (var (name, value) in raw)
        {
            if (name.StartsWith('_'))
            {
                spoofed++;
                continue;
            }

            if (LogRedaction.IsBulkSnapshot(name))
            {
                redacted++;
                continue;
            }

            if (LogRedaction.IsSensitive(name))
            {
                sensitive = true;
            }

            clean[name] = SanitizeNode(value, ref sensitive, ref redacted, 0);
        }

        var ts = JsonUtil.String(clean, "ts") ?? TimeUtil.NowIso();
        var pri = JsonUtil.Int(clean, "pri") ?? 6;
        var level = JsonUtil.String(clean, "level") ?? "info";
        return new LogRecord
        {
            Ts = ts,
            Pri = Math.Clamp(pri, 0, 7),
            Level = level,
            Msg = JsonUtil.String(clean, "msg"),
            Evt = JsonUtil.String(clean, "evt"),
            Seq = 0,
            BootId = bootId,
            Unit = unit,
            Pid = pid,
            InvocationId = invocationId,
            StickId = stickId,
            MachineId = machineId,
            Transport = "extension",
            Trace = JsonUtil.String(clean, "trace"),
            Task = JsonUtil.String(clean, "task"),
            Fields = clean,
            Sensitive = sensitive,
        };
    }

    /// <summary>
    /// Recursively enforces the same field rules inside nested values: a nested "env" object is
    /// a whole snapshot too, and nested secret-looking names must mark the record sensitive.
    /// Depth and node counts are bounded so a hostile record cannot blow the stack or memory.
    /// </summary>
    private static JsonNode? SanitizeNode(JsonNode? node, ref bool sensitive, ref int redacted, int depth)
    {
        if (depth >= 8)
        {
            redacted++;
            return null;
        }

        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var clean = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    if (LogRedaction.IsBulkSnapshot(name))
                    {
                        redacted++;
                        continue;
                    }

                    if (LogRedaction.IsSensitive(name))
                    {
                        sensitive = true;
                    }

                    clean[name] = SanitizeNode(value, ref sensitive, ref redacted, depth + 1);
                }

                return clean;
            }
            case JsonArray array:
            {
                var clean = new JsonArray();
                foreach (var item in array)
                {
                    clean.Add(SanitizeNode(item, ref sensitive, ref redacted, depth + 1));
                }

                return clean;
            }
            default:
                return node.DeepClone();
        }
    }

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "ts", "pri", "level", "seq", "_bootId", "_unit", "msg", "evt", "_pid", "_invocationId",
        "_stickId", "_machineId", "_transport", "trace", "task", "_imported", "_truncated", "_sensitive",
    };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
