using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortableDev.Core.Protocol;

/// <summary>
/// The common envelope (spec 3.3). Unknown top-level fields are preserved in <see cref="Extras"/> so a
/// message can be relayed without losing fields this build does not understand (`x-` zone, spec 0).
/// </summary>
public sealed class Envelope
{
    public int V { get; set; } = ProtocolConstants.Version;

    public string Kind { get; set; } = string.Empty;

    public string? Id { get; set; }

    public long? Seq { get; set; }

    public string? Ts { get; set; }

    public string? Trace { get; set; }

    public string? Parent { get; set; }

    public JsonObject Data { get; set; } = new();

    public JsonObject Meta { get; set; } = new();

    /// <summary>Top-level fields this build does not know; relayed verbatim.</summary>
    public JsonObject Extras { get; set; } = new();

    public static Envelope Parse(ReadOnlySpan<byte> utf8)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"frame is not valid JSON: {ex.Message}");
        }

        return Parse(node);
    }

    public static Envelope Parse(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            throw new ProtocolException("envelope must be a JSON object");
        }

        var envelope = new Envelope();

        foreach (var (name, value) in obj)
        {
            switch (name)
            {
                case "v":
                    envelope.V = ReadInt(value) ?? throw new ProtocolException("\"v\" must be an integer");
                    break;
                case "kind":
                    envelope.Kind = ReadString(value) ?? throw new ProtocolException("\"kind\" must be a string");
                    break;
                case "id":
                    envelope.Id = ReadString(value);
                    break;
                case "seq":
                    envelope.Seq = ReadLong(value);
                    break;
                case "ts":
                    envelope.Ts = ReadString(value);
                    break;
                case "trace":
                    envelope.Trace = ReadString(value);
                    break;
                case "parent":
                    envelope.Parent = ReadString(value);
                    break;
                case "data":
                    envelope.Data = AsObject(value) ?? new JsonObject { ["value"] = value?.DeepClone() };
                    break;
                case "meta":
                    envelope.Meta = AsObject(value) ?? new JsonObject { ["value"] = value?.DeepClone() };
                    break;
                default:
                    envelope.Extras[name] = value?.DeepClone();
                    break;
            }
        }

        if (envelope.Kind.Length == 0)
        {
            throw new ProtocolException("\"kind\" is required");
        }

        if (envelope.V != ProtocolConstants.Version)
        {
            throw new ProtocolException($"unsupported envelope version {envelope.V} (this kernel speaks {ProtocolConstants.Version})");
        }

        return envelope;
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["v"] = V,
            ["kind"] = Kind,
            ["data"] = Data.DeepClone(),
            ["meta"] = Meta.DeepClone(),
        };

        if (Id is not null)
        {
            obj["id"] = Id;
        }

        if (Seq is not null)
        {
            obj["seq"] = Seq.Value;
        }

        if (Ts is not null)
        {
            obj["ts"] = Ts;
        }

        if (Trace is not null)
        {
            obj["trace"] = Trace;
        }

        if (Parent is not null)
        {
            obj["parent"] = Parent;
        }

        foreach (var (name, value) in Extras)
        {
            if (!obj.ContainsKey(name))
            {
                obj[name] = value?.DeepClone();
            }
        }

        return obj;
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(ToJson());

    public static Envelope Make(string kind, JsonObject? data = null) => new()
    {
        Kind = kind,
        Data = data ?? new JsonObject(),
        Ts = Util.TimeUtil.NowIso(),
    };

    /// <summary>Builds an `evt` envelope carrying one event (spec 4.3).</summary>
    public static Envelope Event(string? id, long seq, JsonObject data) => new()
    {
        Kind = Kinds.Evt,
        Id = id,
        Seq = seq,
        Ts = Util.TimeUtil.NowIso(),
        Data = data,
    };

    /// <summary>Builds a terminal `done` event (spec 4.3).</summary>
    public static Envelope Done(string? id, long seq, bool ok, int exitCode, JsonObject? error = null, string? summary = null, JsonObject? metrics = null)
    {
        var data = new JsonObject
        {
            ["type"] = EventTypes.Done,
            ["ok"] = ok,
            ["exitCode"] = exitCode,
        };

        if (summary is not null)
        {
            data["summary"] = summary;
        }

        if (error is not null)
        {
            data["error"] = error;
        }

        if (metrics is not null)
        {
            data["metrics"] = metrics;
        }

        return Event(id, seq, data);
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.String => node.GetValue<string>(),
                JsonValueKind.Null => null,
                _ => node.ToJsonString(),
            };
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var value) ? value : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<long>(out var value) ? value : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonObject? AsObject(JsonNode? node) => node as JsonObject;
}
