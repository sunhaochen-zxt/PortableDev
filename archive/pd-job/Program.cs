using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PortableDev.Job;

// Runs one package script inside a Windows Job Object so the interpreter cannot spawn unbounded
// children or allocate unbounded memory. stdin/stdout/stderr are proxied byte-for-byte, therefore
// the pkgmgr script protocol keeps working unchanged. Result file: { ok, exitCode, timedOut }.
internal static class Program
{
    private const uint JobObjectLimitKillOnClose = 0x2000;
    private const uint JobObjectLimitActiveProcess = 0x8;
    private const uint JobObjectLimitProcessMemory = 0x100;
    private const int ExtendedLimitInfoClass = 9;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: pd-job <config.json>");
            return 2;
        }

        var configPath = args[0];
        JsonObject config;
        try
        {
            config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                ?? throw new InvalidOperationException("config must be a JSON object");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("pd-job: bad config: " + ex.Message);
            return 2;
        }

        var resultPath = config["resultPath"]?.GetValue<string>() ?? Path.ChangeExtension(configPath, ".result.json");
        var command = config["command"]?.GetValue<string>() ?? "";
        var timeoutMs = config["timeoutMs"]?.GetValue<int>() ?? 600000;
        var memoryLimitMb = config["memoryLimitMB"]?.GetValue<int>() ?? 0;
        var activeProcessLimit = config["activeProcessLimit"]?.GetValue<int>() ?? 0;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return Fail(resultPath, "CreateJobObject failed: " + Marshal.GetLastWin32Error());
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnClose,
            },
        };
        if (activeProcessLimit > 0)
        {
            limits.BasicLimitInformation.LimitFlags |= JobObjectLimitActiveProcess;
            limits.BasicLimitInformation.ActiveProcessLimit = (uint)activeProcessLimit;
        }

        if (memoryLimitMb > 0)
        {
            limits.BasicLimitInformation.LimitFlags |= JobObjectLimitProcessMemory;
            limits.ProcessMemoryLimit = (nuint)((ulong)memoryLimitMb * 1024UL * 1024UL);
        }

        if (!SetInformationJobObject(job, ExtendedLimitInfoClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = "SetInformationJobObject failed: " + Marshal.GetLastWin32Error();
            CloseHandle(job);
            return Fail(resultPath, error);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = config["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory,
        };
        foreach (var node in config["args"] as JsonArray ?? new JsonArray())
        {
            if (node is not null)
            {
                startInfo.ArgumentList.Add(node.GetValue<string>());
            }
        }

        startInfo.Environment.Clear();
        if (config["env"] is JsonObject environment)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value?.GetValue<string>() ?? "";
            }
        }

        Process child;
        try
        {
            child = Process.Start(startInfo) ?? throw new InvalidOperationException("cannot start the script");
        }
        catch (Exception ex)
        {
            CloseHandle(job);
            return Fail(resultPath, "cannot start the script: " + ex.Message);
        }

        try
        {
            if (!AssignProcessToJobObject(job, child.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                try { child.Kill(entireProcessTree: true); } catch { /* ignore */ }
                CloseHandle(job);
                return Fail(resultPath, "AssignProcessToJobObject failed: " + error);
            }
        }
        catch (Exception ex)
        {
            try { child.Kill(entireProcessTree: true); } catch { /* ignore */ }
            CloseHandle(job);
            return Fail(resultPath, "cannot attach the script to the job: " + ex.Message);
        }

        var stdin = Task.Run(() => Proxy(Console.OpenStandardInput(), child.StandardInput.BaseStream));
        var stdout = Task.Run(() => Proxy(child.StandardOutput.BaseStream, Console.OpenStandardOutput()));
        var stderr = Task.Run(() => Proxy(child.StandardError.BaseStream, Console.OpenStandardError()));

        var finished = child.WaitForExit(timeoutMs);
        var timedOut = false;
        if (!finished)
        {
            timedOut = true;
            TerminateJobObject(job, 1);
            try { child.WaitForExit(10000); } catch { /* ignore */ }
        }

        try
        {
            Task.WaitAll(new[] { stdout, stderr }, 5000);
        }
        catch
        {
            // the script may still hold its pipes; we already killed the job
        }

        var exitCode = 0;
        try
        {
            exitCode = finished ? child.ExitCode : 124;
        }
        catch
        {
            exitCode = 124;
        }

        try { child.Dispose(); } catch { /* ignore */ }

        // Closing the job kills any children the script left behind (KILL_ON_JOB_CLOSE).
        CloseHandle(job);
        if (!finished)
        {
            try { child.WaitForExit(5000); } catch { /* already gone */ }
        }

        WriteResult(resultPath, new JsonObject
        {
            ["schema"] = 1,
            ["ok"] = true,
            ["exitCode"] = exitCode,
            ["timedOut"] = timedOut,
            ["timeoutMs"] = timeoutMs,
        });
        return timedOut ? 124 : exitCode;
    }

    private static async Task Proxy(Stream input, Stream output)
    {
        try
        {
            await input.CopyToAsync(output);
            await output.FlushAsync();
        }
        catch
        {
            // broken pipe / killed child
        }
    }

    private static int Fail(string resultPath, string message)
    {
        WriteResult(resultPath, new JsonObject
        {
            ["schema"] = 1,
            ["ok"] = false,
            ["error"] = new JsonObject { ["code"] = "ERR_JOB", ["message"] = message },
        });
        Console.Error.WriteLine("pd-job: " + message);
        return 125;
    }

    private static void WriteResult(string path, JsonObject document)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, document.ToJsonString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("pd-job: cannot write result: " + ex.Message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
