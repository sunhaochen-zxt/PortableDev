using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace PortableDev.Elevate;

// Elevated worker used by the storage extension for HKLM writes and machine paths outside the
// user's own directories. One request file in, one result file out; the parent never needs to be
// elevated. Without --fake the process relaunches itself with UAC ("runas") when needed.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: pd-elevate <request.json> [--fake <state.json>] [--no-elevate]");
            return 2;
        }

        var requestPath = args[0];
        string? fakePath = null;
        var noElevate = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--fake" && index + 1 < args.Length)
            {
                fakePath = args[++index];
            }
            else if (args[index] == "--no-elevate")
            {
                noElevate = true;
            }
        }

        JsonObject request;
        try
        {
            request = JsonNode.Parse(File.ReadAllText(requestPath)) as JsonObject
                ?? throw new InvalidOperationException("request must be a JSON object");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("pd-elevate: bad request: " + ex.Message);
            return 2;
        }

        var resultPath = request["result"]?.GetValue<string>() ?? Path.ChangeExtension(requestPath, ".result.json");

        if (fakePath is null && !noElevate && !IsAdministrator())
        {
            // Relaunch ourselves with UAC. When we were started as "dotnet pd-elevate.dll ..." the
            // relaunch must keep the dll path, otherwise "runas dotnet request.json" would fail.
            var processPath = Environment.ProcessPath!;
            var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            var selfDll = Environment.GetCommandLineArgs().FirstOrDefault() ?? string.Empty;
            var start = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = isDotnetHost
                    ? Quote(Path.GetFullPath(selfDll)) + " " + Quote(requestPath)
                    : Quote(requestPath),
            };
            try
            {
                using var child = Process.Start(start);
                child?.WaitForExit();
                return child?.ExitCode ?? 1;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                WriteResult(resultPath, false, "elevation cancelled: " + ex.Message, new JsonArray());
                return 1;
            }
        }

        var fake = fakePath is null ? null : LoadFake(fakePath);
        var results = new JsonArray();
        var allOk = true;
        var ops = request["ops"] as JsonArray ?? new JsonArray();
        foreach (var node in ops)
        {
            var op = node as JsonObject ?? new JsonObject();
            var entry = new JsonObject { ["op"] = op["op"]?.GetValue<string>() ?? "" };
            try
            {
                var before = Execute(op, fake);
                entry["ok"] = true;
                entry["before"] = before?.DeepClone();
                results.Add(entry);
            }
            catch (Exception ex)
            {
                allOk = false;
                entry["ok"] = false;
                entry["error"] = new JsonObject
                {
                    ["code"] = ex is UnauthorizedAccessException ? "ERR_PERMISSION" : "ERR_IO",
                    ["message"] = ex.Message,
                };
                results.Add(entry);
            }
        }

        if (fake is not null && fakePath is not null)
        {
            File.WriteAllText(fakePath, fake.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        WriteResult(resultPath, allOk, allOk ? null : "one or more elevated operations failed", results);
        return allOk ? 0 : 1;
    }

    private static JsonNode? Execute(JsonObject op, JsonObject? fake)
    {
        var name = op["op"]?.GetValue<string>() ?? "";
        return name switch
        {
            "registry.set" => RegistrySet(op, fake, delete: false),
            "registry.delete" => RegistrySet(op, fake, delete: true),
            "registry.get" => RegistryGet(op, fake),
            "link.create" => LinkCreate(op, fake),
            "link.remove" => LinkRemove(op, fake),
            "copy.create" => CopyCreate(op, fake),
            "copy.remove" => CopyRemove(op, fake),
            _ => throw new InvalidOperationException("unknown elevated op: " + name),
        };
    }

    private static string RegistryId(JsonObject op)
        => (op["hive"]?.GetValue<string>() ?? "HKCU").ToUpperInvariant() + "\\" +
           (op["key"]?.GetValue<string>() ?? "") + "\\" +
           (op["name"]?.GetValue<string>() ?? "");

    private static JsonNode? RegistryGet(JsonObject op, JsonObject? fake)
    {
        if (fake is not null)
        {
            var registry = fake["registry"] as JsonObject ?? new JsonObject();
            return registry[RegistryId(op)]?.DeepClone();
        }

        using var root = OpenHive(op);
        using var key = root.OpenSubKey(op["key"]?.GetValue<string>() ?? "", writable: false);
        var valueName = op["name"]?.GetValue<string>() ?? "";
        if (key is null)
        {
            return null;
        }

        if (!key.GetValueNames().Any(candidate => string.Equals(candidate, valueName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var kind = key.GetValueKind(valueName);
        if (kind == RegistryValueKind.Unknown)
        {
            return null;
        }

        return new JsonObject
        {
            ["type"] = KindName(kind),
            ["value"] = key.GetValue(valueName)?.ToString() ?? "",
        };
    }

    private static JsonNode? RegistrySet(JsonObject op, JsonObject? fake, bool delete)
    {
        var before = RegistryGet(op, fake);
        if (fake is not null)
        {
            var registry = fake["registry"] as JsonObject ?? new JsonObject();
            if (delete)
            {
                registry.Remove(RegistryId(op));
            }
            else
            {
                registry[RegistryId(op)] = new JsonObject
                {
                    ["type"] = op["type"]?.GetValue<string>() ?? "REG_SZ",
                    ["value"] = op["value"]?.GetValue<string>() ?? "",
                };
            }

            fake["registry"] = registry;
            return before;
        }

        using var root = OpenHive(op);
        var keyPath = op["key"]?.GetValue<string>() ?? "";
        var valueName = op["name"]?.GetValue<string>() ?? "";
        using var key = root.CreateSubKey(keyPath, writable: true)
            ?? throw new IOException("cannot open registry key " + keyPath);
        if (delete)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else
        {
            var kind = ParseKind(op["type"]?.GetValue<string>());
            key.SetValue(valueName, op["value"]?.GetValue<string>() ?? "", kind);
        }

        return before;
    }

    private static RegistryKey OpenHive(JsonObject op)
    {
        var hive = (op["hive"]?.GetValue<string>() ?? "HKCU").ToUpperInvariant();
        return hive switch
        {
            "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
            "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default),
            _ => throw new InvalidOperationException("unsupported hive: " + hive),
        };
    }

    private static string KindName(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => "REG_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.DWord => "REG_DWORD",
        RegistryValueKind.QWord => "REG_QWORD",
        RegistryValueKind.MultiString => "REG_MULTI_SZ",
        RegistryValueKind.Binary => "REG_BINARY",
        _ => "REG_SZ",
    };

    private static RegistryValueKind ParseKind(string? text) => (text ?? "REG_SZ").ToUpperInvariant() switch
    {
        "REG_SZ" => RegistryValueKind.String,
        "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
        "REG_DWORD" => RegistryValueKind.DWord,
        "REG_QWORD" => RegistryValueKind.QWord,
        "REG_MULTI_SZ" => RegistryValueKind.MultiString,
        "REG_BINARY" => RegistryValueKind.Binary,
        _ => RegistryValueKind.String,
    };

    private static JsonNode? LinkCreate(JsonObject op, JsonObject? fake)
    {
        var target = Path.GetFullPath(op["target"]?.GetValue<string>() ?? "");
        var source = Path.GetFullPath(op["source"]?.GetValue<string>() ?? "");
        if (fake is not null)
        {
            var links = fake["links"] as JsonObject ?? new JsonObject();
            var before = links[target]?.DeepClone();
            if (before is not null)
            {
                throw new IOException("target already is a link: " + target);
            }

            links[target] = source;
            fake["links"] = links;
            return null;
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException("target already exists: " + target);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var command = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        command.ArgumentList.Add("/c");
        command.ArgumentList.Add("mklink");
        command.ArgumentList.Add("/J");
        command.ArgumentList.Add(target);
        command.ArgumentList.Add(source);
        using var process = Process.Start(command) ?? throw new IOException("cannot run mklink");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException("mklink failed with exit code " + process.ExitCode);
        }

        return null;
    }

    private static JsonNode? LinkRemove(JsonObject op, JsonObject? fake)
    {
        var target = Path.GetFullPath(op["target"]?.GetValue<string>() ?? "");
        if (fake is not null)
        {
            var links = fake["links"] as JsonObject ?? new JsonObject();
            var existing = links[target]?.DeepClone();
            links.Remove(target);
            fake["links"] = links;
            return existing;
        }

        var info = new DirectoryInfo(target);
        var linkTarget = info.Exists ? info.LinkTarget : null;
        if (info.Exists)
        {
            Directory.Delete(target, recursive: false);
        }

        return linkTarget is null ? null : new JsonObject { ["target"] = linkTarget };
    }

    private static JsonNode? CopyCreate(JsonObject op, JsonObject? fake)
    {
        var target = Path.GetFullPath(op["target"]?.GetValue<string>() ?? "");
        var source = Path.GetFullPath(op["source"]?.GetValue<string>() ?? "");
        if (fake is not null)
        {
            var copies = fake["copies"] as JsonObject ?? new JsonObject();
            if (copies[target] is not null)
            {
                throw new IOException("target already exists: " + target);
            }

            copies[target] = source;
            fake["copies"] = copies;
            return null;
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException("target already exists: " + target);
        }

        CopyTree(source, target);
        return null;
    }

    private static JsonNode? CopyRemove(JsonObject op, JsonObject? fake)
    {
        var target = Path.GetFullPath(op["target"]?.GetValue<string>() ?? "");
        if (fake is not null)
        {
            var copies = fake["copies"] as JsonObject ?? new JsonObject();
            var before = copies[target]?.DeepClone();
            copies.Remove(target);
            fake["copies"] = copies;
            return before;
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }

        return null;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static JsonObject LoadFake(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception)
        {
            return new JsonObject();
        }
    }

    private static void WriteResult(string path, bool ok, string? error, JsonArray results)
    {
        var document = new JsonObject
        {
            ["schema"] = 1,
            ["ok"] = ok,
            ["elevated"] = IsAdministrator(),
            ["results"] = results,
        };
        if (error is not null)
        {
            document["error"] = new JsonObject { ["code"] = "ERR_ELEVATION", ["message"] = error };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
