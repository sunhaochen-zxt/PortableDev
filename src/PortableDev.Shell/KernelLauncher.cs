using System.Diagnostics;
using System.Text;

namespace PortableDev.Shell;

/// <summary>
/// Starts the kernel when no pipe is listening. The kernel is a resident daemon, so this
/// object never kills it on exit; it only drains its output (architecture 3.8 rule 3).
/// </summary>
public sealed class KernelLauncher
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private KernelLauncher(Process process) => _process = process;

    public string LastError
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    public int? ProcessId => _process.HasExited ? null : _process.Id;

    /// <summary>Exit code once the process exited; lets the shell propagate busy(10)/env(4) correctly.</summary>
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static (KernelLauncher? Launcher, string? Error) Start(string root, bool trace)
    {
        var (file, arguments) = Resolve(root);
        if (file is null)
        {
            return (null, "cannot start the kernel: set PD_KERNEL or install app\\pd-kernel.exe");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };

        // Children (extensions) must find the same runtime the kernel uses.
        if (PortableDev.Core.Util.DotnetRuntime.ResolveRoot(root) is { Length: > 0 } dotnetRoot)
        {
            startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(root);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var launcher = new KernelLauncher(process);
        process.OutputDataReceived += (_, e) => launcher.Drain(trace, "out", e.Data);
        process.ErrorDataReceived += (_, e) => launcher.Drain(trace, "err", e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return (null, "cannot start the kernel: " + ex.Message);
        }

        if (trace)
        {
            Console.Error.WriteLine("[pd] started kernel pid " + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return (launcher, null);
    }

    public void Dispose()
    {
        try
        {
            _process.CancelOutputRead();
            _process.CancelErrorRead();
        }
        catch (InvalidOperationException)
        {
            // Output was never redirected or the process already exited.
        }

        _process.Dispose();
    }

    private void Drain(bool trace, string stream, string? line)
    {
        if (line is null)
        {
            return;
        }

        if (stream == "err")
        {
            lock (_stderr)
            {
                _stderr.AppendLine(line);
            }
        }

        if (trace)
        {
            Console.Error.WriteLine("[kernel:" + stream + "] " + line);
        }
    }

    private static (string? File, List<string> Arguments) Resolve(string root)
    {
        var overridden = Environment.GetEnvironmentVariable("PD_KERNEL");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var parts = SplitCommandLine(overridden);
            if (parts.Count > 0)
            {
                return (parts[0], parts.Skip(1).ToList());
            }
        }

        var candidates = new[]
        {
            Path.Combine(root, "app", "pd-kernel.exe"),
            Path.Combine(AppContext.BaseDirectory, "pd-kernel.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "pd-kernel.exe"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return (full, []);
            }
        }

        // A portable stick ships the kernel as a dll and runs it with its own runtime.
        var dllCandidates = new[]
        {
            Path.Combine(root, "app", "pd-kernel.dll"),
            Path.Combine(AppContext.BaseDirectory, "pd-kernel.dll"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "pd-kernel.dll"),
        };

        foreach (var candidate in dllCandidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return (PortableDev.Core.Util.DotnetRuntime.ResolveHost(root), [full]);
            }
        }

        return (null, []);
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
