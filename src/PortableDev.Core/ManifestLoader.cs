using PortableDev.Core.Model;
using Tomlyn;
using Tomlyn.Model;

namespace PortableDev.Core;

/// <summary>Loads manifest.toml plus every manifest.d/*.toml on the stick.</summary>
public static class ManifestLoader
{
    public const string RootFileName = "manifest.toml";
    public const string ToolDirName = "manifest.d";

    public static RootManifest Load(string stickRoot)
    {
        var errors = new List<string>();
        var manifest = Load(stickRoot, errors);
        if (errors.Count > 0)
            throw new ManifestException(errors);
        return manifest;
    }

    public static RootManifest Load(string stickRoot, List<string> errors)
    {
        var rootFile = Path.Combine(stickRoot, "manifest", RootFileName);
        if (!File.Exists(rootFile))
            throw new ManifestException(new[] { $"manifest not found: {rootFile}" });

        var rootTable = ParseFile(rootFile, errors);
        if (rootTable is null)
            return EmptyManifest();

        var rootSection = GetTable(rootTable, "root");
        var applySection = GetTable(rootTable, "apply");

        var pathOrder = GetStringArray(rootTable, "path_order");
        var env = ReadEnv(rootTable, "env", errors, rootFile);

        var tools = new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<ProfileSpec>();
        var toolDir = Path.Combine(stickRoot, "manifest", ToolDirName);
        if (Directory.Exists(toolDir))
        {
            foreach (var file in Directory.GetFiles(toolDir, "*.toml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var table = ParseFile(file, errors);
                if (table is null) continue;

                var tool = ReadTool(table, errors, file);
                if (tool is null) continue;

                if (tools.ContainsKey(tool.Tool.Id))
                {
                    errors.Add($"{file}: duplicate tool id '{tool.Tool.Id}'");
                    continue;
                }
                tools[tool.Tool.Id] = tool;

                // Profiles are declared inside a tool manifest so that {{TOOL}} has an
                // owner to expand to; stamp the id here, then collect them globally.
                foreach (var profile in tool.Profiles)
                {
                    if (profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"{file}: duplicate [[profile]] '{profile.Name}'");
                        continue;
                    }

                    profiles.Add(new ProfileSpec
                    {
                        Name = profile.Name,
                        ToolId = tool.Tool.Id,
                        Env = profile.Env,
                        Notes = profile.Notes,
                    });
                }
            }
        }

        return new RootManifest
        {
            Schema = GetInt(rootTable, "schema", 1),
            Root = new RootManifest.RootSection
            {
                Stable = GetString(rootSection, "stable", @"C:\Dev"),
                Marker = GetString(rootSection, "marker", ".portabledev-root"),
            },
            Apply = new RootManifest.ApplySection
            {
                BackupDir = GetString(applySection, "backup_dir", "state/backup"),
                JournalDir = GetString(applySection, "journal_dir", "state/journal"),
                HomeAdopt = GetBool(applySection, "home_adopt", false),
            },
            HomeDirs = ReadHomeDirs(rootTable, errors, rootFile),
            Profiles = profiles,
            PathOrder = pathOrder,
            Env = env,
            Tools = tools,
        };
    }

    private static RootManifest EmptyManifest() => new();

    private static TomlTable? ParseFile(string path, List<string> errors)
    {
        try
        {
            var text = File.ReadAllText(path);
            var doc = Toml.Parse(text);
            if (doc.HasErrors)
            {
                foreach (var d in doc.Diagnostics)
                    errors.Add($"{path}: {d}");
                return null;
            }
            return doc.ToModel();
        }
        catch (Exception ex)
        {
            errors.Add($"{path}: {ex.Message}");
            return null;
        }
    }

    private static ToolManifest? ReadTool(TomlTable table, List<string> errors, string file)
    {
        var t = GetTable(table, "tool");
        if (t is null)
        {
            errors.Add($"{file}: missing [tool] section");
            return null;
        }

        var id = GetString(t, "id", "");
        var version = GetString(t, "version", "");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
        {
            errors.Add($"{file}: [tool] requires id and version");
            return null;
        }

        var paths = new List<ToolManifest.PathEntry>();
        if (table.TryGetValue("path", out var pv) && pv is TomlTableArray pta)
        {
            foreach (var pt in pta)
                paths.Add(new ToolManifest.PathEntry { Dir = GetString(pt, "dir", ".") });
        }

        var verifies = new List<ToolManifest.VerifySpec>();
        if (table.TryGetValue("verify", out var vv) && vv is TomlTableArray vta)
        {
            foreach (var vt in vta)
            {
                var run = GetString(vt, "run", "");
                if (string.IsNullOrWhiteSpace(run)) continue;
                verifies.Add(new ToolManifest.VerifySpec
                {
                    Run = run,
                    Expect = GetStringOrNull(vt, "expect"),
                    ExpectAny = GetStringArray(vt, "expect_any"),
                });
            }
        }

        return new ToolManifest
        {
            Schema = GetInt(table, "schema", 1),
            Tool = new ToolManifest.ToolSection
            {
                Id = id,
                Version = version,
                Title = GetStringOrNull(t, "title"),
                Kind = GetString(t, "kind", "zip"),
                Archive = GetStringOrNull(t, "archive"),
                Sha256 = GetStringOrNull(t, "sha256"),
                Strip = GetInt(t, "strip", 0),
                Rename = GetStringOrNull(t, "rename"),
                Dir = GetStringOrNull(t, "dir"),
                Data = GetStringOrNull(t, "data"),
                Notes = GetStringOrNull(t, "notes"),
                Provides = GetStringArray(t, "provides"),
                Conflicts = GetStringArray(t, "conflicts"),
                Enabled = GetBool(t, "enabled", true),
            },
            Path = paths,
            Env = ReadEnv(table, "env", errors, file),
            Profiles = ReadProfiles(table, errors, file),
            Launch = ReadLaunches(table, errors, file),
            Verify = verifies,
            Update = ReadUpdate(table),
            Prepare = ReadHook(table, "prepare"),
            Post = ReadHook(table, "post"),
        };
    }

    private static UpdateSpec? ReadUpdate(TomlTable table)
    {
        var t = GetTable(table, "update");
        if (t is null) return null;

        return new UpdateSpec
        {
            Kind = GetString(t, "kind", "github"),
            Repo = GetStringOrNull(t, "repo"),
            Index = GetStringOrNull(t, "index"),
            Pattern = GetString(t, "pattern", ""),
            Url = GetStringOrNull(t, "url"),
            Sha256Url = GetStringOrNull(t, "sha256_url"),
            StripPrefix = GetStringOrNull(t, "strip_prefix"),
            SkipVersionPattern = GetStringOrNull(t, "skip_version_pattern"),
        };
    }

    private static ToolManifest.HookSpec? ReadHook(TomlTable table, string key)
    {
        var t = GetTable(table, key);
        if (t is null) return null;
        var script = GetString(t, "script", "");
        if (string.IsNullOrWhiteSpace(script)) return null;
        return new ToolManifest.HookSpec { Script = script, TimeoutSeconds = GetInt(t, "timeout_seconds", 120) };
    }

    /// <summary>Reads [[home_dir]]: directories on the machine that must live on the stick.</summary>
    private static List<RootManifest.HomeDirSection> ReadHomeDirs(TomlTable table, List<string> errors, string file)
    {
        var list = new List<RootManifest.HomeDirSection>();
        if (!table.TryGetValue("home_dir", out var v) || v is not TomlTableArray arr) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ht in arr)
        {
            var sub = GetString(ht, "path", "").Trim();
            if (string.IsNullOrWhiteSpace(sub))
            {
                errors.Add($"{file}: [[home_dir]] requires path");
                continue;
            }
            if (sub.StartsWith("..", StringComparison.Ordinal) || sub.Contains("..\\", StringComparison.Ordinal))
            {
                errors.Add($"{file}: [[home_dir]] path must not contain '..': {sub}");
                continue;
            }

            var rootVar = GetString(ht, "root", "USERPROFILE");
            var store = GetStringOrNull(ht, "store") ?? "home/" + sub.Replace('\\', '/').Trim('/');
            var display = rootVar + "/" + sub.Replace('\\', '/');
            if (!seen.Add(display))
            {
                errors.Add($"{file}: duplicate [[home_dir]] {display}");
                continue;
            }

            list.Add(new RootManifest.HomeDirSection
            {
                Root = rootVar,
                Sub = sub,
                Store = store,
                For = GetStringOrNull(ht, "for"),
            });
        }
        return list;
    }

    private static List<EnvVarSpec> ReadEnv(TomlTable table, string key, List<string> errors, string file)
    {
        var list = new List<EnvVarSpec>();
        if (!table.TryGetValue(key, out var v) || v is not TomlTableArray arr) return list;

        foreach (var et in arr)
        {
            var name = GetString(et, "name", "");
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{file}: [[env]] requires name");
                continue;
            }
            list.Add(new EnvVarSpec { Name = name, Value = GetString(et, "value", "") });
        }
        return list;
    }

    /// <summary>
    /// Read [[profile]] sections: a name plus an inline env table.
    ///
    ///   [[profile]]
    ///   name = 'msvc'
    ///   env  = { EnterpriseWDK = 'True', VSINSTALLDIR = '{{TOOL}}' }
    ///
    /// Inline rather than repeated [[profile.env]] on purpose: a profile is one indivisible
    /// set of variables, so it reads better as one line per profile and cannot be split in
    /// half by a typo in a nested header.
    /// </summary>
    private static List<ProfileSpec> ReadProfiles(TomlTable table, List<string> errors, string file)
    {
        var list = new List<ProfileSpec>();
        if (!table.TryGetValue("profile", out var v) || v is not TomlTableArray arr) return list;

        foreach (var pt in arr)
        {
            var name = GetString(pt, "name", "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{file}: [[profile]] requires name");
                continue;
            }

            var env = new List<EnvVarSpec>();
            if (GetTable(pt, "env") is { } envTable)
            {
                foreach (var pair in envTable)
                {
                    if (pair.Value is not string value)
                    {
                        errors.Add($"{file}: [[profile]] '{name}' env.{pair.Key} must be a string");
                        continue;
                    }
                    env.Add(new EnvVarSpec { Name = pair.Key, Value = value });
                }
            }

            if (env.Count == 0)
                errors.Add($"{file}: [[profile]] '{name}' declares no env");

            list.Add(new ProfileSpec
            {
                Name = name,
                Env = env,
                Notes = GetStringOrNull(pt, "notes"),
            });
        }
        return list;
    }

    /// <summary>
    /// Read [[launch]] sections: one generated .cmd at the stick root per entry.
    ///
    ///   [[launch]]
    ///   name = 'Tabby'
    ///   exe  = 'Tabby.exe'
    ///
    /// The name doubles as the file name, so it must stay a plain file name: a name holding
    /// a path separator or a drive letter would let a manifest write outside the stick root.
    /// </summary>
    private static List<LaunchSpec> ReadLaunches(TomlTable table, List<string> errors, string file)
    {
        var list = new List<LaunchSpec>();
        if (!table.TryGetValue("launch", out var v) || v is not TomlTableArray arr) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lt in arr)
        {
            var name = GetString(lt, "name", "").Trim();
            var exe = GetString(lt, "exe", "").Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{file}: [[launch]] requires name");
                continue;
            }
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name.Contains("..", StringComparison.Ordinal))
            {
                errors.Add($"{file}: [[launch]] name must be a plain file name: {name}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(exe))
            {
                errors.Add($"{file}: [[launch]] '{name}' requires exe");
                continue;
            }
            if (!seen.Add(name))
            {
                errors.Add($"{file}: duplicate [[launch]] '{name}'");
                continue;
            }

            var env = new List<EnvVarSpec>();
            if (GetTable(lt, "env") is { } envTable)
            {
                foreach (var pair in envTable)
                {
                    if (pair.Value is not string value)
                    {
                        errors.Add($"{file}: [[launch]] '{name}' env.{pair.Key} must be a string");
                        continue;
                    }
                    env.Add(new EnvVarSpec { Name = pair.Key, Value = value });
                }
            }

            list.Add(new LaunchSpec
            {
                Name = name,
                Exe = exe,
                Dir = GetString(lt, "dir", "."),
                Args = GetStringArray(lt, "args"),
                Env = env,
                Notes = GetStringOrNull(lt, "notes"),
            });
        }
        return list;
    }

    private static TomlTable? GetTable(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is TomlTable t ? t : null;

    private static string GetString(TomlTable? table, string key, string fallback) =>
        table is not null && table.TryGetValue(key, out var v) && v is string s ? s : fallback;

    private static string? GetStringOrNull(TomlTable? table, string key) =>
        table is not null && table.TryGetValue(key, out var v) && v is string s ? s : null;

    private static int GetInt(TomlTable? table, string key, int fallback)
    {
        if (table is null || !table.TryGetValue(key, out var v)) return fallback;
        return v switch
        {
            long l => (int)l,
            int i => i,
            _ => fallback,
        };
    }

    private static bool GetBool(TomlTable? table, string key, bool fallback)
    {
        if (table is null || !table.TryGetValue(key, out var v)) return fallback;
        return v is bool b ? b : fallback;
    }

    private static IReadOnlyList<string> GetStringArray(TomlTable? table, string key)
    {
        if (table is null || !table.TryGetValue(key, out var v) || v is not TomlArray arr)
            return Array.Empty<string>();

        return arr.OfType<string>().ToList();
    }
}

public sealed class ManifestException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ManifestException(IEnumerable<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors.ToList();
    }
}
