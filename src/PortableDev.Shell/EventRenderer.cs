using System.Text;
using System.Text.Json.Nodes;
using PortableDev.Core.Util;

namespace PortableDev.Shell;

/// <summary>
/// Renders the kernel event stream. In NDJSON mode each event is printed verbatim (commands.md 6.2);
/// otherwise it is formatted for a terminal, with ANSI colour only on a real console.
/// </summary>
public sealed class EventRenderer
{
    private readonly TextWriter _out;
    private readonly bool _json;
    private readonly bool _quiet;
    private readonly bool _verbose;
    private readonly bool _color;
    private readonly bool _tty;
    private readonly Dictionary<string, int> _lastPercent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TableChunk> _tables = new(StringComparer.Ordinal);
    private bool _partial;

    public EventRenderer(TextWriter output, bool json, bool quiet, bool verbose, bool color)
    {
        _out = output;
        _json = json;
        _quiet = quiet;
        _verbose = verbose;
        _tty = !Console.IsOutputRedirected;
        _color = color && !json && _tty;
    }

    public bool JsonMode => _json;

    public void Render(JsonObject data)
    {
        if (_json)
        {
            _out.WriteLine(data.ToJsonString());
            _out.Flush();
            return;
        }

        switch (JsonUtil.String(data, "type") ?? string.Empty)
        {
            case "progress":
                Progress(data);
                break;
            case "log":
                Log(data);
                break;
            case "step":
                Step(data);
                break;
            case "table":
                Table(data);
                break;
            case "kv":
                Kv(data);
                break;
            case "notice":
                Notice(data);
                break;
            case "link":
                Link(data);
                break;
            case "tree":
                Tree(data);
                break;
            case "ext":
                Ext(data);
                break;
            case "done":
                Done(data);
                break;
            default:
                if (_verbose)
                {
                    Line(data.ToJsonString());
                }

                break;
        }
    }

    public void Prompt(JsonObject data)
    {
        if (_json)
        {
            return;
        }

        EndPartial();
        var message = JsonUtil.String(data, "message") ?? string.Empty;
        var kind = JsonUtil.String(data, "kind") ?? "input";
        _out.Write(Paint("? ", "36") + message);
        _out.Write(kind == "confirm" ? " [y/N] " : ": ");
        _out.Flush();
    }

    public void Note(string text)
    {
        if (_json || _quiet)
        {
            return;
        }

        EndPartial();
        _out.WriteLine(Paint(text, "90"));
    }

    public void Error(string code, string message, string? hint)
    {
        EndPartial();
        if (_json)
        {
            // --json is a machine contract: stdout must stay valid NDJSON even on shell-level errors.
            var payload = new JsonObject
            {
                ["type"] = "error",
                ["code"] = code,
                ["message"] = message,
            };
            if (hint is not null)
            {
                payload["hint"] = hint;
            }

            _out.WriteLine(payload.ToJsonString());
            _out.Flush();
            return;
        }

        _out.WriteLine(Paint("x " + code + ": " + message, "31"));
        if (hint is not null)
        {
            _out.WriteLine(Paint("  hint: " + hint, "90"));
        }

        _out.Flush();
    }

    private void Progress(JsonObject data)
    {
        if (_quiet)
        {
            return;
        }

        var task = JsonUtil.String(data, "task") ?? "task";
        var label = JsonUtil.String(data, "label") ?? task;
        var percent = JsonUtil.Int(data, "pct");

        if (_tty)
        {
            var text = percent is null ? label : label + " " + percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            var width = Math.Max(20, SafeWidth() - 1);
            if (text.Length > width)
            {
                text = text[..width];
            }

            _out.Write("\r" + Paint(text.PadRight(width), "36"));
            _out.Flush();
            _partial = true;
            return;
        }

        if (percent is null)
        {
            Line("  " + label);
            return;
        }

        var bucket = percent.Value / 10 * 10;
        if (_lastPercent.TryGetValue(task, out var previous) && bucket <= previous && percent.Value < 100)
        {
            return;
        }

        _lastPercent[task] = bucket;
        Line("  " + label + " " + percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%");
    }

    private void Log(JsonObject data)
    {
        // Providers may emit either a flat log event or a journal record under "record".
        var record = JsonUtil.Object(data, "record");
        if (record is not null)
        {
            var recordLevel = JsonUtil.String(record, "level") ?? LogLevelFromPriority(JsonUtil.Int(record, "pri"));
            var recordMessage = JsonUtil.String(record, "msg") ?? string.Empty;
            WriteLog(recordLevel, recordMessage);
            return;
        }

        WriteLog(JsonUtil.String(data, "level") ?? "info", JsonUtil.String(data, "msg") ?? string.Empty);
    }

    private static string LogLevelFromPriority(int? priority) => priority switch
    {
        <= 3 => "error",
        4 => "warn",
        7 => "debug",
        _ => "info",
    };

    private void WriteLog(string level, string message)
    {
        if (_quiet && level is not ("warn" or "error"))
        {
            return;
        }

        var prefix = level switch
        {
            "error" => Paint("x ", "31"),
            "warn" => Paint("! ", "33"),
            "debug" => _verbose ? Paint(". ", "90") : string.Empty,
            _ => string.Empty,
        };

        if (level == "debug" && !_verbose)
        {
            return;
        }

        Line(prefix + message);
    }

    private void Step(JsonObject data)
    {
        if (_quiet)
        {
            return;
        }

        var title = JsonUtil.String(data, "title") ?? JsonUtil.String(data, "stepId") ?? "step";
        var state = JsonUtil.String(data, "state") ?? "running";
        var marker = state switch
        {
            "done" => Paint("[ok] ", "32"),
            "failed" => Paint("[x]  ", "31"),
            "skipped" => Paint("[-]  ", "90"),
            "running" => Paint("[..] ", "36"),
            _ => Paint("[ ]  ", "90"),
        };
        Line(marker + title);
    }

    private sealed class TableChunk(string key)
    {
        public string Key { get; } = key;

        public string? Title { get; set; }

        public JsonArray? Columns { get; set; }

        public SortedDictionary<int, JsonArray> Rows { get; } = new();
    }

    private void Table(JsonObject data)
    {
        if (_quiet)
        {
            return;
        }

        var key = JsonUtil.String(data, "key");
        var chunk = JsonUtil.Int(data, "chunk");
        var final = JsonUtil.Bool(data, "final");
        if (key is null && chunk is null && final is null)
        {
            RenderTable(JsonUtil.String(data, "title"), JsonUtil.Array(data, "columns"), JsonUtil.Array(data, "rows") ?? []);
            return;
        }

        // spec 4.5: chunks with the same key accumulate by chunk index and render on final:true.
        key ??= "table";
        var accumulator = _tables.TryGetValue(key, out var existing) ? existing : new TableChunk(key);
        _tables[key] = accumulator;
        var title = JsonUtil.String(data, "title");
        if (!string.IsNullOrEmpty(title))
        {
            accumulator.Title = title;
        }

        var columns = JsonUtil.Array(data, "columns");
        if (columns is not null)
        {
            accumulator.Columns = columns;
        }

        var rows = JsonUtil.Array(data, "rows");
        if (rows is not null)
        {
            accumulator.Rows[chunk ?? accumulator.Rows.Count] = rows;
        }

        if (final != true && chunk is not null)
        {
            return;
        }

        _tables.Remove(key);
        var all = new JsonArray();
        foreach (var pair in accumulator.Rows)
        {
            foreach (var row in pair.Value)
            {
                all.Add(row?.DeepClone());
            }
        }

        RenderTable(accumulator.Title, accumulator.Columns, all);
    }

    private void RenderTable(string? title, JsonArray? columns, JsonArray rows)
    {
        if (!string.IsNullOrEmpty(title))
        {
            Line(Paint(title, "1"));
        }
        var headers = new List<string>();
        if (columns is not null)
        {
            foreach (var column in columns)
            {
                headers.Add(JsonUtil.String(column as JsonObject, "title") ?? JsonUtil.String(column as JsonObject, "key") ?? string.Empty);
            }
        }

        var width = headers.Count;
        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var values = new List<string>();
            if (row is JsonArray array)
            {
                foreach (var value in array)
                {
                    values.Add(CellText(value));
                }
            }

            width = Math.Max(width, values.Count);
            cells.Add(values.ToArray());
        }

        if (width == 0)
        {
            return;
        }

        for (var i = headers.Count; i < width; i++)
        {
            headers.Add(string.Empty);
        }

        var widths = new int[width];
        for (var i = 0; i < width; i++)
        {
            widths[i] = DisplayWidth(headers[i]);
        }

        foreach (var cell in cells)
        {
            for (var i = 0; i < width && i < cell.Length; i++)
            {
                widths[i] = Math.Max(widths[i], Math.Min(DisplayWidth(cell[i]), 48));
            }
        }

        Line("  " + string.Join("  ", headers.Select((header, index) => PadToWidth(header, widths[index]))).TrimEnd());
        Line("  " + new string('-', Math.Min(120, widths.Sum() + (width - 1) * 2)));
        foreach (var cell in cells)
        {
            var parts = new List<string>();
            for (var i = 0; i < width; i++)
            {
                var value = i < cell.Length ? cell[i] : string.Empty;
                if (DisplayWidth(value) > widths[i])
                {
                    value = TruncateToWidth(value, widths[i]);
                }

                parts.Add(PadToWidth(value, widths[i]));
            }

            Line("  " + string.Join("  ", parts).TrimEnd());
        }
    }

    /// <summary>Terminal columns a string occupies (CJK/full-width glyphs count as two).</summary>
    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(int codePoint)
    {
        if (codePoint is >= 0x1100 and <= 0x115F)
        {
            return true;
        }

        if (codePoint is 0x2329 or 0x232A)
        {
            return true;
        }

        return codePoint is >= 0x2E80 and <= 0xA4CF
            || codePoint is >= 0xAC00 and <= 0xD7A3
            || codePoint is >= 0xF900 and <= 0xFAFF
            || codePoint is >= 0xFE30 and <= 0xFE6F
            || codePoint is >= 0xFF00 and <= 0xFF60
            || codePoint is >= 0xFFE0 and <= 0xFFE6
            || codePoint is >= 0x1F300 and <= 0x1F64F
            || codePoint is >= 0x1F900 and <= 0x1F9FF
            || codePoint is >= 0x20000 and <= 0x3FFFD;
    }

    private static string PadToWidth(string text, int width)
        => text + new string(' ', Math.Max(0, width - DisplayWidth(text)));

    private static string TruncateToWidth(string text, int width)
    {
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = IsWide(rune.Value) ? 2 : 1;
            if (used + runeWidth > Math.Max(0, width - 1))
            {
                break;
            }

            builder.Append(rune.ToString());
            used += runeWidth;
        }

        return builder.Append('…').ToString();
    }

    private void Kv(JsonObject data)
    {
        if (_quiet)
        {
            return;
        }

        var title = JsonUtil.String(data, "title");
        if (!string.IsNullOrEmpty(title))
        {
            Line(Paint(title, "1"));
        }

        var items = JsonUtil.Object(data, "items");
        if (items is null)
        {
            return;
        }

        var width = items.Select(pair => pair.Key.Length).DefaultIfEmpty(0).Max();
        foreach (var (name, value) in items)
        {
            Line("  " + name.PadRight(width) + "  " + CellText(value));
        }
    }

    private void Notice(JsonObject data)
    {
        var level = JsonUtil.String(data, "level") ?? "info";
        if (_quiet && level is not ("warn" or "error"))
        {
            return;
        }

        var message = JsonUtil.String(data, "message") ?? string.Empty;
        Line((level == "error" ? Paint("x ", "31") : Paint("! ", "33")) + message);
    }

    private void Link(JsonObject data)
    {
        if (_quiet)
        {
            return;
        }

        var text = JsonUtil.String(data, "text") ?? JsonUtil.String(data, "target") ?? string.Empty;
        Line("  " + text);
    }

    private void Tree(JsonObject data)
    {
        if (_quiet)
        {
            return;
        }

        var title = JsonUtil.String(data, "title");
        if (!string.IsNullOrEmpty(title))
        {
            Line(Paint(title, "1"));
        }

        foreach (var node in JsonUtil.Array(data, "nodes") ?? [])
        {
            Line("  " + CellText(node));
        }
    }

    private void Ext(JsonObject data)
    {
        if (!_verbose || _quiet)
        {
            return;
        }

        var name = JsonUtil.String(data, "name") ?? "extension";
        var state = JsonUtil.String(data, "state") ?? "unknown";
        var reason = JsonUtil.String(data, "reason");
        Line(Paint("[ext] " + name + " " + state, "90") + (reason is null ? string.Empty : " (" + reason + ")"));
    }

    private void Done(JsonObject data)
    {
        var ok = JsonUtil.Bool(data, "ok") ?? false;
        var error = JsonUtil.Object(data, "error");
        EndPartial();

        if (ok)
        {
            var summary = JsonUtil.String(data, "summary");
            if (!_quiet && !string.IsNullOrEmpty(summary))
            {
                _out.WriteLine(Paint(summary, "32"));
            }

            _out.Flush();
            return;
        }

        var code = JsonUtil.String(error, "code") ?? "ERR_INTERNAL";
        var message = JsonUtil.String(error, "message") ?? "command failed";
        var hint = JsonUtil.String(error, "hint");
        Error(code, message, hint);
    }

    private static string CellText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
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

    private void Line(string text)
    {
        EndPartial();
        _out.WriteLine(text);
        _out.Flush();
    }

    private void EndPartial()
    {
        if (!_partial)
        {
            return;
        }

        _out.WriteLine();
        _out.Flush();
        _partial = false;
    }

    private string Paint(string text, string code) => _color ? "\u001b[" + code + "m" + text + "\u001b[0m" : text;

    private static int SafeWidth()
    {
        try
        {
            return Math.Max(40, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 80;
        }
    }
}
