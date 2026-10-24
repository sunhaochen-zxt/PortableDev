using System.Collections.Generic;
using System.Text.Json;

namespace PortableDev.Gui.ViewModels;

/// <summary>
/// Parser for 'PortableDev verify --json'. The CLI emits a JSON array of
/// { tool, command, ok, output, exitCode }; we only care about the first four.
/// </summary>
public static class VerifyJson
{
    public sealed record Item(string Tool, string Command, bool Ok, string? Output);

    public static List<Item> Parse(string json)
    {
        var list = new List<Item>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        // The CLI prints a banner before the array on some builds; take the last '[' onward.
        var start = json.IndexOf('[');
        if (start < 0) return list;

        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var tool = e.TryGetProperty("tool", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var command = e.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                var ok = e.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                var output = e.TryGetProperty("output", out var ou) && ou.ValueKind == JsonValueKind.String
                    ? ou.GetString()
                    : null;
                list.Add(new Item(tool, command, ok, output));
            }
        }
        catch (JsonException)
        {
            // A malformed payload means "no results", not a crash: the GUI then falls
            // back to table presence rather than lying about verification.
        }

        return list;
    }
}
