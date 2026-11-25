using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using PortableDev.Proc;

if (args.Length >= 2 && string.Equals(args[0], "--helper-breakaway", StringComparison.Ordinal))
{
    var pidFile = args[1];
    var (grandchild, _) = ProcessSpawner.Spawn(new ProcessStartSpec
    {
        FileName = CommandInterpreter(),
        Arguments = ["/c", "ping", "-n", "30", "127.0.0.1"],
        Detached = true,
    });
    File.WriteAllText(pidFile, grandchild.Pid.ToString(CultureInfo.InvariantCulture));
    return 0;
}

var tests = new List<(string Name, Func<Task> Body)>
{
    ("spawn.exit-code", SpawnExitCode),
    ("job.kill-on-close", JobKillOnClose),
    ("job.terminate", JobTerminate),
    ("spawn.timeout", SpawnTimeout),
    ("job.active-process-limit", JobActiveProcessLimit),
    ("spawn.detach-survives", DetachSurvives),
    ("stdio.spawn-proxy", StdioProxy),
    ("stdio.exchange", StdioExchange),
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try
    {
        await body().ConfigureAwait(false);
        Console.WriteLine($"  ok   {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"\n{tests.Count - failures} passed, {failures} failed");
return failures == 0 ? 0 : 1;

static (string FileName, List<string> Arguments) HelperCommand(params string[] extra)
{
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("cannot locate the test runner");

    // Under 'dotnet runner.dll' the entry assembly is the dll and the host must be told to run it.
    if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(entry) && File.Exists(entry))
        {
            return (processPath, [entry, .. extra]);
        }
    }

    return (processPath, [.. extra]);
}

static async Task StdioProxy()
{
    var suffix = Guid.NewGuid().ToString("N")[..8];
    var stdinName = "pd-test-" + suffix + "-in";
    var stdoutName = "pd-test-" + suffix + "-out";
    var stderrName = "pd-test-" + suffix + "-err";

    using var stdinServer = new NamedPipeServerStream(stdinName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var stdoutServer = new NamedPipeServerStream(stdoutName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var stderrServer = new NamedPipeServerStream(stderrName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    var connected = Task.WhenAll(
        stdinServer.WaitForConnectionAsync(),
        stdoutServer.WaitForConnectionAsync(),
        stderrServer.WaitForConnectionAsync());

    var pipes = StdioPipes.Open(stdinName, stdoutName, stderrName);
    SpawnedProcess process;
    JobObject? job;
    try
    {
        (process, job) = ProcessSpawner.Spawn(new ProcessStartSpec
        {
            FileName = CommandInterpreter(),
            Arguments = ["/c", "echo", "hello"],
            StandardInput = pipes.Stdin,
            StandardOutput = pipes.Stdout,
            StandardError = pipes.Stderr,
        }, new JobLimits());
    }
    finally
    {
        pipes.Dispose();
    }

    using (process)
    using (job)
    {
        await connected.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        using var reader = new StreamReader(stdoutServer);
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        if (!string.Equals(line?.Trim(), "hello", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unexpected stdout: " + (line ?? "<null>"));
        }

        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException("child did not exit");
        }
    }
}

static async Task StdioExchange()
{
    var suffix = Guid.NewGuid().ToString("N")[..8];
    var stdinName = "pd-test-" + suffix + "-in";
    var stdoutName = "pd-test-" + suffix + "-out";
    var stderrName = "pd-test-" + suffix + "-err";

    using var stdinServer = new NamedPipeServerStream(stdinName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var stdoutServer = new NamedPipeServerStream(stdoutName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var stderrServer = new NamedPipeServerStream(stderrName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    var connected = Task.WhenAll(
        stdinServer.WaitForConnectionAsync(),
        stdoutServer.WaitForConnectionAsync(),
        stderrServer.WaitForConnectionAsync());

    const string script = "import sys;l=sys.stdin.readline().strip();sys.stdout.write('GOT:'+l+'\\n');sys.stdout.flush()";
    var pipes = StdioPipes.Open(stdinName, stdoutName, stderrName);
    SpawnedProcess process;
    JobObject? job;
    try
    {
        (process, job) = ProcessSpawner.Spawn(new ProcessStartSpec
        {
            FileName = "python",
            Arguments = ["-c", script],
            StandardInput = pipes.Stdin,
            StandardOutput = pipes.Stdout,
            StandardError = pipes.Stderr,
        }, new JobLimits());
    }
    finally
    {
        pipes.Dispose();
    }

    using (process)
    using (job)
    {
        await connected.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await using (var writer = new StreamWriter(stdinServer) { AutoFlush = true })
        {
            await writer.WriteLineAsync("ping").ConfigureAwait(false);
        }

        using var reader = new StreamReader(stdoutServer);
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        if (!string.Equals(line, "GOT:ping", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unexpected echo: " + (line ?? "<null>"));
        }

        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException("child did not exit");
        }
    }
}

static string CommandInterpreter()
    => Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

static ProcessStartSpec LongPing() => new()
{
    FileName = CommandInterpreter(),
    Arguments = ["/c", "ping", "-n", "60", "127.0.0.1"],
};

static Task SpawnExitCode()
{
    var (process, job) = ProcessSpawner.Spawn(new ProcessStartSpec
    {
        FileName = CommandInterpreter(),
        Arguments = ["/c", "exit", "7"],
    }, new JobLimits());

    using (process)
    using (job)
    {
        if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
        {
            throw new InvalidOperationException("child did not exit");
        }

        if (!process.TryGetExitCode(out var exitCode) || exitCode != 7)
        {
            throw new InvalidOperationException($"expected exit code 7, got {exitCode}");
        }
    }

    return Task.CompletedTask;
}

static Task JobKillOnClose()
{
    var (process, job) = ProcessSpawner.Spawn(LongPing(), new JobLimits());
    using (process)
    {
        job!.Dispose();
        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("child survived job close");
        }
    }

    return Task.CompletedTask;
}

static Task JobTerminate()
{
    var (process, job) = ProcessSpawner.Spawn(LongPing(), new JobLimits());
    using (process)
    using (job)
    {
        job!.Terminate();
        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("child survived job termination");
        }
    }

    return Task.CompletedTask;
}

static Task SpawnTimeout()
{
    var (process, job) = ProcessSpawner.Spawn(LongPing(), new JobLimits());
    using (process)
    using (job)
    {
        if (process.WaitForExit(TimeSpan.FromMilliseconds(300)))
        {
            throw new InvalidOperationException("child exited before the timeout window");
        }

        job!.Terminate();
        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("child survived job termination");
        }
    }

    return Task.CompletedTask;
}

static Task JobActiveProcessLimit()
{
    using var job = JobObject.Create(new JobLimits { ActiveProcesses = 1 });
    using var first = ProcessSpawner.SpawnInto(LongPing(), job);

    var rejected = false;
    try
    {
        using var second = ProcessSpawner.SpawnInto(LongPing(), job);
    }
    catch (Exception)
    {
        rejected = true;
    }

    if (!rejected)
    {
        throw new InvalidOperationException("a second process was allowed into a one-process job");
    }

    return Task.CompletedTask;
}

static async Task DetachSurvives()
{
    var pidFile = Path.Combine(Path.GetTempPath(), "pd-proc-detach-" + Guid.NewGuid().ToString("N") + ".pid");
    var (helperFile, helperArguments) = HelperCommand("--helper-breakaway", pidFile);
    using var job = JobObject.Create(new JobLimits());
    using var helper = ProcessSpawner.SpawnInto(new ProcessStartSpec
    {
        FileName = helperFile,
        Arguments = helperArguments,
    }, job);

    var deadline = DateTime.UtcNow.AddSeconds(20);
    string? text = null;
    while (DateTime.UtcNow < deadline)
    {
        if (File.Exists(pidFile))
        {
            text = File.ReadAllText(pidFile).Trim();
            if (text.Length > 0) break;
        }

        await Task.Delay(100).ConfigureAwait(false);
    }

    if (string.IsNullOrEmpty(text))
    {
        throw new InvalidOperationException("helper did not report the detached pid");
    }

    var grandchild = int.Parse(text, CultureInfo.InvariantCulture);
    job.Dispose();
    await Task.Delay(1500).ConfigureAwait(false);

    try
    {
        using var alive = Process.GetProcessById(grandchild);
        if (alive.HasExited)
        {
            throw new InvalidOperationException("detached process died with the job");
        }

        alive.Kill();
    }
    finally
    {
        try { File.Delete(pidFile); } catch (IOException) { }
    }
}
