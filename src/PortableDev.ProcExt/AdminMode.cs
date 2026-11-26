using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

namespace PortableDev.ProcExt;

/// <summary>
/// One-shot elevated helper: reads one request file, performs it, writes the result file and exits.
/// It never connects to the kernel and never stays resident (proc.md 10).
/// </summary>
internal static class AdminMode
{
    public static int Run(string requestPath) => Run(requestPath, GetArgvResultPath(requestPath));

    public static int Run(string requestPath, string? resultPathArg)
    {
        var result = new JsonObject { ["error"] = new JsonObject { ["code"] = "ERR_INTERNAL", ["message"] = "admin mode failed" } };
        string? resultPath = resultPathArg ?? GetArgvResultPath(requestPath);
        try
        {
            var request = JsonNode.Parse(File.ReadAllText(requestPath)) as JsonObject ?? new JsonObject();
            result = Execute(request);
        }
        catch (Exception ex)
        {
            result = new JsonObject { ["error"] = new JsonObject { ["code"] = "ERR_INTERNAL", ["message"] = ex.Message } };
        }

        if (string.IsNullOrEmpty(resultPath) || !IsWithinTmp(resultPath))
        {
            return 1;
        }

        try
        {
            var full = Path.GetFullPath(resultPath);
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(result.ToJsonString());
        }
        catch (Exception)
        {
            // Fail closed: never overwrite or follow a pre-created link.
        }

        return 0;
    }

    private static string GetTmpBase()
    {
        var root = Environment.GetEnvironmentVariable("PD_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return Path.GetTempPath();
        }

        return Path.Combine(root, "state", "tmp");
    }

    private static bool IsWithinTmp(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            // GetTempPath() may keep its trailing separator; appending another one would make
            // every child path fail the prefix test (so every result is rejected when PD_ROOT is unset).
            var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetTmpBase()));
            var full = Path.GetFullPath(candidate);
            return full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, baseDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? GetArgvResultPath(string requestPath)
    {
        try
        {
            var argv = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(argv, requestPath);
            if (index >= 0 && index + 1 < argv.Length && !string.IsNullOrWhiteSpace(argv[index + 1]))
            {
                return argv[index + 1];
            }

            if (argv.Length > 0)
            {
                var last = argv[argv.Length - 1];
                if (!string.Equals(last, requestPath, StringComparison.Ordinal) && last.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase))
                {
                    return last;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static JsonObject Execute(JsonObject request)
    {
        var action = request["action"]?.GetValue<string>() ?? string.Empty;
        try
        {
            if (action == "system")
            {
                return SystemProcesses.Snapshot(request["filter"]?.GetValue<string>());
            }

            if (action == "kill")
            {
                var pid = request["pid"]?.GetValue<int>() ?? 0;
                var forceCritical = request["forceCritical"]?.GetValue<bool>() == true;
                return SystemProcesses.Kill(pid, forceCritical);
            }

            return SystemProcesses.Error("ERR_BAD_ARGS", "unknown admin action: " + action);
        }
        catch (Exception ex)
        {
            return SystemProcesses.Error("ERR_INTERNAL", ex.Message);
        }
    }
}

/// <summary>Starts the one-shot admin helper (UAC, unless PD_PROC_NO_ELEVATE=1 for tests).</summary>
internal static class Elevated
{
    public static JsonObject Run(JsonObject request, int timeoutMs = 180000)
    {
        var root = Environment.GetEnvironmentVariable("PD_ROOT") ?? Path.GetTempPath();
        var directory = Path.Combine(root, "state", "tmp");
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, "proc-admin-" + Guid.NewGuid().ToString("N") + ".json");
        var resultPath = requestPath + ".result.json";
        try
        {
            var payload = (JsonObject)request.DeepClone();
            File.WriteAllText(requestPath, payload.ToJsonString());

            // Host detection mirrors PortableDev.Elevate: only the dotnet muxer needs the managed
            // dll as its first argument. An apphost *is* the entry point, so "proc.exe proc.dll
            // --admin ..." would pass the dll as argv[0] and Program.Main would never see --admin.
            var processPath = Environment.ProcessPath;
            var host = processPath ?? "dotnet";
            var hostedByDotnet = processPath is null
                || string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            var noElevate = Environment.GetEnvironmentVariable("PD_PROC_NO_ELEVATE") == "1";
            var startInfo = new ProcessStartInfo
            {
                FileName = host,
                UseShellExecute = !noElevate,
                CreateNoWindow = noElevate,
            };

            var arguments = new List<string>();
            if (hostedByDotnet)
            {
                var entry = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(entry))
                {
                    throw new InvalidOperationException("cannot locate the extension entry assembly");
                }

                arguments.Add(Path.GetFullPath(entry));
            }

            arguments.Add("--admin");
            arguments.Add(requestPath);
            arguments.Add(resultPath);

            if (!noElevate)
            {
                // ShellExecute ("runas") takes one command line, so quote every token ourselves.
                startInfo.Verb = "runas";
                startInfo.Arguments = string.Join(' ', arguments.Select(Quote));
            }
            else
            {
                // ShellExecute is off, so ArgumentList preserves the tokens verbatim.
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("cannot start the elevated helper");

            for (var waited = 0; waited < timeoutMs; waited += 100)
            {
                if (File.Exists(resultPath))
                {
                    break;
                }

                if (process.HasExited)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            if (!File.Exists(resultPath))
            {
                return SystemProcesses.Error("ERR_ELEVATION", "the elevated helper did not produce a result (UAC cancelled?)");
            }

            return JsonNode.Parse(File.ReadAllText(resultPath)) as JsonObject
                ?? SystemProcesses.Error("ERR_INTERNAL", "the elevated helper wrote an invalid result");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return SystemProcesses.Error("ERR_ELEVATION", "cannot elevate: " + ex.Message);
        }
        finally
        {
            try { File.Delete(requestPath); } catch (Exception) { }
            try { File.Delete(resultPath); } catch (Exception) { }
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
