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
    public static int Run(string requestPath)
    {
        var result = new JsonObject { ["error"] = new JsonObject { ["code"] = "ERR_INTERNAL", ["message"] = "admin mode failed" } };
        string? resultPath = null;
        try
        {
            var request = JsonNode.Parse(File.ReadAllText(requestPath)) as JsonObject ?? new JsonObject();
            resultPath = request["resultPath"]?.GetValue<string>();
            result = Execute(request);
        }
        catch (Exception ex)
        {
            result = new JsonObject { ["error"] = new JsonObject { ["code"] = "ERR_INTERNAL", ["message"] = ex.Message } };
        }

        if (!string.IsNullOrEmpty(resultPath))
        {
            try
            {
                File.WriteAllText(resultPath, result.ToJsonString());
            }
            catch (Exception)
            {
                // Nothing else we can do here.
            }
        }

        return 0;
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
            payload["resultPath"] = resultPath;
            File.WriteAllText(requestPath, payload.ToJsonString());

            var entry = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("cannot locate the extension entry assembly");
            var host = Environment.ProcessPath ?? "dotnet";
            var noElevate = Environment.GetEnvironmentVariable("PD_PROC_NO_ELEVATE") == "1";
            var startInfo = new ProcessStartInfo
            {
                FileName = host,
                UseShellExecute = !noElevate,
                CreateNoWindow = noElevate,
            };
            if (!noElevate)
            {
                startInfo.Verb = "runas";
                startInfo.Arguments = "\"" + entry + "\" --admin \"" + requestPath + "\"";
            }
            else
            {
                startInfo.ArgumentList.Add(entry);
                startInfo.ArgumentList.Add("--admin");
                startInfo.ArgumentList.Add(requestPath);
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
            try { File.Delete(requestPath); } catch (IOException) { }
            try { File.Delete(resultPath); } catch (IOException) { }
        }
    }
}
