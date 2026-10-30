using System.Security;
using Microsoft.Win32;

namespace PortableDev.Win;

/// <summary>
/// Reads the user PATH and environment.
///
/// Critical rule (plan §4): the value is read with
/// DoNotExpandEnvironmentNames so that %USERPROFILE% style variables survive,
/// and it must be written back as REG_EXPAND_SZ. Using setx would silently
/// truncate at 1024 chars and downgrade the value to REG_SZ.
/// </summary>
public sealed class RegistryEnvironment
{
    private const string UserEnvKey = @"Environment";
    private const string RegistrySuffixTest = "-Test";

    private readonly RegistryKey _hive;
    private readonly string _subKey;

    /// <param name="testSuffix">
    /// When true, reads/writes HKCU\Environment&lt;suffix&gt; instead of the real key.
    /// Always used by unit tests so a bug cannot damage the live PATH.
    /// </param>
    public RegistryEnvironment(bool testSuffix = false)
    {
        _subKey = testSuffix ? UserEnvKey + RegistrySuffixTest : UserEnvKey;
        _hive = Registry.CurrentUser;
    }

    public string SubKeyName => _subKey;

    /// <summary>Raw (unexpanded) PATH value, or empty when absent.</summary>
    public string ReadUserPathRaw()
    {
        using var key = _hive.OpenSubKey(_subKey, writable: false);
        if (key is null) return string.Empty;

        // DoNotExpandEnvironmentNames is what preserves %VARS%.
        var value = key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value as string ?? string.Empty;
    }

    public RegistryValueKind ReadUserPathKind()
    {
        using var key = _hive.OpenSubKey(_subKey, writable: false);
        if (key is null) return RegistryValueKind.Unknown;

        // GetValueKind throws when the value is absent, which is the normal state
        // of a fresh user hive. Treat "no Path value" as Unknown instead of crashing.
        try
        {
            if (!key.GetValueNames().Any(n => n.Equals("Path", StringComparison.OrdinalIgnoreCase)))
                return RegistryValueKind.Unknown;

            return key.GetValueKind("Path");
        }
        catch (IOException)
        {
            return RegistryValueKind.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return RegistryValueKind.Unknown;
        }
    }

    /// <summary>Write raw PATH back as REG_EXPAND_SZ. Never uses setx.</summary>
    public void WriteUserPathRaw(string rawPath) => WriteUserPathRaw(rawPath, null);

    /// <summary>
    /// Write raw PATH back in the registry kind it had before the run. Restoring a value
    /// that was REG_SZ as REG_EXPAND_SZ would start expanding a literal "%PATH%" that the
    /// user meant literally.
    /// </summary>
    public void WriteUserPathRaw(string rawPath, string? kindName)
    {
        using var key = _hive.CreateSubKey(_subKey, writable: true)
            ?? throw new SecurityException($"cannot open HKCU\\{_subKey} for writing");

        key.SetValue("Path", rawPath ?? string.Empty, ParseKind(kindName, RegistryValueKind.ExpandString));
    }

    /// <summary>
    /// Remove the user PATH value entirely. Only for undo, when the value did not exist
    /// before the run: writing an empty string instead would leave a value behind that
    /// was never there.
    /// </summary>
    public void DeleteUserPath()
    {
        using var key = _hive.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue("Path", throwOnMissingValue: false);
    }

    /// <summary>Map a persisted kind name back to a registry kind.</summary>
    private static RegistryValueKind ParseKind(string? kindName, RegistryValueKind fallback) =>
        kindName switch
        {
            "ExpandString" => RegistryValueKind.ExpandString,
            "String" => RegistryValueKind.String,
            _ => fallback,
        };

    /// <summary>All user environment variables except PATH, unexpanded.</summary>
    public IReadOnlyDictionary<string, string> ReadUserEnvRaw()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, (value, _)) in ReadUserEnvRawWithKinds())
            result[name] = value;
        return result;
    }

    /// <summary>
    /// All user environment variables except PATH with their registry kind, unexpanded.
    /// Rollback needs the kind as well as the value, or a REG_SZ value comes back as an
    /// expanding one.
    /// </summary>
    public IReadOnlyDictionary<string, (string Value, RegistryValueKind Kind)> ReadUserEnvRawWithKinds()
    {
        var result = new Dictionary<string, (string, RegistryValueKind)>(StringComparer.OrdinalIgnoreCase);
        using var key = _hive.OpenSubKey(_subKey, writable: false);
        if (key is null) return result;

        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase)) continue;

            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (value is null) continue;

            RegistryValueKind kind;
            try { kind = key.GetValueKind(name); }
            catch { kind = RegistryValueKind.String; }

            result[name] = (value, kind);
        }
        return result;
    }

    /// <summary>Set one variable as REG_EXPAND_SZ (the default for manifest values).</summary>
    public void WriteEnv(string name, string value) => WriteEnv(name, value, null);

    /// <summary>Set one variable in a specific registry kind, preserving the original on undo.</summary>
    public void WriteEnv(string name, string value, string? kindName)
    {
        using var key = _hive.CreateSubKey(_subKey, writable: true)
            ?? throw new SecurityException($"cannot open HKCU\\{_subKey} for writing");

        key.SetValue(name, value, ParseKind(kindName, RegistryValueKind.ExpandString));
    }

    public void DeleteEnv(string name)
    {
        using var key = _hive.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>Export a .reg snapshot for rollback. Call before any write.</summary>
    public void ExportBackup(string destinationFile)
    {
        var dir = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lines = new List<string>
        {
            "Windows Registry Editor Version 5.00",
            "",
            $"[HKEY_CURRENT_USER\\{_subKey}]",
        };

        using var key = _hive.OpenSubKey(_subKey, writable: false);
        if (key is not null)
        {
            foreach (var name in key.GetValueNames().Where(n => !string.IsNullOrEmpty(n)))
            {
                try
                {
                    var line = RegLine(name, key);
                    if (line is not null) lines.Add(line);
                }
                catch
                {
                    // One exotic value must not cost the whole backup: skip it and keep
                    // exporting the values that can be represented.
                }
            }
        }

        File.WriteAllLines(destinationFile, lines);
    }

    /// <summary>
    /// One .reg line for one value, whatever its type.
    ///
    /// The old version exported only string values, so a DWORD or a REG_MULTI_SZ vanished
    /// from the backup and was silently lost on restore. Types the .reg format cannot
    /// represent return null and are skipped explicitly.
    /// </summary>
    private static string? RegLine(string name, RegistryKey key)
    {
        var kind = key.GetValueKind(name);
        var quoted = "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        switch (kind)
        {
            case RegistryValueKind.String:
            {
                var text = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                           ?? string.Empty;
                return quoted + "=\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }

            case RegistryValueKind.ExpandString:
            {
                var text = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                           ?? string.Empty;
                return quoted + "=hex(2):" + ToUtf16Hex(text + "\0");
            }

            case RegistryValueKind.MultiString:
            {
                var parts = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string[]
                            ?? Array.Empty<string>();
                // REG_MULTI_SZ is each string NUL-terminated, then a final empty string.
                var joined = string.Concat(parts.Select(p => p + "\0")) + "\0";
                return quoted + "=hex(7):" + ToUtf16Hex(joined);
            }

            case RegistryValueKind.Binary:
            {
                var bytes = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[]
                            ?? Array.Empty<byte>();
                return quoted + "=hex:" + string.Join(",", bytes.Select(b => b.ToString("x2")));
            }

            case RegistryValueKind.DWord:
            {
                var value = Convert.ToInt32(key.GetValue(name, 0));
                return quoted + "=dword:" + value.ToString("x8");
            }

            case RegistryValueKind.QWord:
            {
                var value = Convert.ToInt64(key.GetValue(name, 0L));
                return quoted + "=hex(b):" +
                       string.Join(",", BitConverter.GetBytes(value).Select(b => b.ToString("x2")));
            }

            default:
                return null;
        }
    }

    /// <summary>UTF-16LE bytes as the comma-separated hex a .reg file expects.</summary>
    private static string ToUtf16Hex(string value) =>
        string.Join(",", System.Text.Encoding.Unicode.GetBytes(value).Select(b => b.ToString("x2")));

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Compose the user environment as seen by new processes.</summary>
    public static IReadOnlyDictionary<string, string> ExpandAll(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            result["__expanded"] = Environment.ExpandEnvironmentVariables(raw ?? string.Empty);
        }
        catch
        {
            result["__expanded"] = raw ?? string.Empty;
        }
        return result;
    }
}
