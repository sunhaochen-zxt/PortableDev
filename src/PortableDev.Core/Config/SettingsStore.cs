using System.Text.Json;
using System.Text.Json.Nodes;
using PortableDev.Core.Util;

namespace PortableDev.Core.Config;

/// <summary>
/// Loads/saves `config\\settings.json`: defaults merged with the file, unknown keys preserved,
/// corrupt JSON renamed to `.bad` (kernel.md 2.2).
/// </summary>
public sealed class SettingsStore
{
    private readonly JsonObject _root;

    private SettingsStore(string path, JsonObject root, bool wasReset, string? backupPath)
    {
        Path = path;
        _root = root;
        WasReset = wasReset;
        BackupPath = backupPath;
    }

    public string Path { get; }

    public bool WasReset { get; }

    public string? BackupPath { get; }

    public JsonObject Root => _root;

    /// <summary>File mtime captured at load/save; used to warn "hand-edited, restart required".</summary>
    public DateTime? FileWriteTimeUtc { get; private set; }

    public bool FileChangedSinceLoad
        => File.Exists(Path) && (FileWriteTimeUtc is null || File.GetLastWriteTimeUtc(Path) > FileWriteTimeUtc.Value);

    private static SettingsStore Track(SettingsStore store)
    {
        store.FileWriteTimeUtc = File.Exists(store.Path) ? File.GetLastWriteTimeUtc(store.Path) : null;
        return store;
    }

    public static SettingsStore Load(string path)
    {
        var document = SettingCatalog.CreateDefaultDocument();
        if (!File.Exists(path))
        {
            var created = new SettingsStore(path, document, wasReset: false, backupPath: null);
            created.Save();
            return Track(created);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return Track(new SettingsStore(path, document, wasReset: true, backupPath: null));
        }

        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            var backup = path + ".bad";
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch (IOException)
            {
                backup = null;
            }

            return Track(new SettingsStore(path, document, wasReset: true, backupPath: backup));
        }

        Merge(document, parsed);
        return Track(new SettingsStore(path, document, wasReset: false, backupPath: null));
    }

    public string? GetString(string path)
    {
        var node = SettingCatalog.GetPath(_root, path);
        try
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public int GetInt(string path, int fallback)
    {
        var node = SettingCatalog.GetPath(_root, path);
        try
        {
            return node is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    public bool GetBool(string path, bool fallback)
    {
        var node = SettingCatalog.GetPath(_root, path);
        try
        {
            return node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    public TimeSpan GetDuration(string path, TimeSpan fallback)
    {
        var text = GetString(path);
        return TimeUtil.ParseDuration(text) ?? fallback;
    }

    /// <summary>Validates and stores one dotted key. Unknown keys are accepted but reported.</summary>
    public (bool Ok, string? Error, bool UnknownKey) Set(string dottedPath, string rawValue)
    {
        var definition = SettingCatalog.Find(dottedPath);
        if (definition is null)
        {
            SettingCatalog.SetPath(_root, dottedPath, rawValue);
            Save();
            return (true, null, true);
        }

        if (!SettingCatalog.TryCoerce(definition, rawValue, out var value, out var error))
        {
            return (false, error, false);
        }

        SettingCatalog.SetPath(_root, dottedPath, value);
        Save();
        return (true, null, false);
    }

    public void Save()
    {
        AtomicFile.WriteAllText(Path, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        FileWriteTimeUtc = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source)
        {
            if (value is JsonObject child && target[name] is JsonObject existing)
            {
                Merge(existing, child);
                continue;
            }

            target[name] = value?.DeepClone();
        }
    }
}
