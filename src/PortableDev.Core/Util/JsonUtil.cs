using System.Text.Json.Nodes;

namespace PortableDev.Core.Util;

/// <summary>Small, allocation-conscious helpers for the tolerant JSON style the protocol uses.</summary>
public static class JsonUtil
{
    public static string? String(JsonObject? obj, string name)
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => node.ToJsonString(),
            };
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    /// <summary>Reads a raw node as a string without re-wrapping it in an object.</summary>
    public static string? StringValue(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() == System.Text.Json.JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    public static int? Int(JsonObject? obj, string name)
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() == System.Text.Json.JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var value) ? value : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static bool? Bool(JsonObject? obj, string name)
    {
        if (obj is null || !obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static JsonArray? Array(JsonObject? obj, string name)
        => obj is not null && obj.TryGetPropertyValue(name, out var node) ? node as JsonArray : null;

    public static JsonObject? Object(JsonObject? obj, string name)
        => obj is not null && obj.TryGetPropertyValue(name, out var node) ? node as JsonObject : null;

    public static IEnumerable<string> Strings(JsonArray? array)
    {
        if (array is null)
        {
            yield break;
        }

        foreach (var node in array)
        {
            if (node is null)
            {
                continue;
            }

            string? text = null;
            try
            {
                if (node.GetValueKind() == System.Text.Json.JsonValueKind.String)
                {
                    text = node.GetValue<string>();
                }
            }
            catch (InvalidOperationException)
            {
                // A non-string entry is ignored on purpose: enums are open sets (spec 2.11).
                text = null;
            }

            if (text is not null)
            {
                yield return text;
            }
        }
    }
}
