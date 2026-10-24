using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PortableDev.Gui.Engine;

public sealed class CliResult
{
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;

    public bool Ok => ExitCode == 0;

    /// <summary>Turn an exit code into the sentence the CLI documents for it.</summary>
    public string Describe() => ExitCode switch
    {
        0 => "done",
        1 => "failed",
        2 => "bad command line",
        3 => "manifest error",
        4 => "stick not found",
        5 => "blocked by a warning",
        6 => "not confirmed",
        7 => "junction failed",
        8 => "elevation declined or unavailable",
        9 => "home link failed",
        _ => "exit code " + ExitCode,
    };
}

/// <summary>
/// Runs the console front-end for operations that must not be duplicated in the GUI:
/// apply and clean go through the elevation broker, so re-implementing them here
/// would create a second, subtly different write path.
/// </summary>
public static class CliBridge
{
    // RunAsync may be called from several pages at once; the cache is shared state.
    private static readonly object CacheGate = new();
    private static string? _cached;

    /// <summary>
    /// Find PortableDev.exe: next to the GUI, then ../app (on the stick),
    /// then walking up to the stick root's app folder.
    /// </summary>
    public static string Locate(string? stickRoot)
    {
        lock (CacheGate)
        {
            if (_cached is not null && File.Exists(_cached)) return _cached;
        }

        var exe = "PortableDev.exe";
        var candidates = new List<string>();

        var here = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(here, exe));
        candidates.Add(Path.Combine(here, "..", "app", exe));
        candidates.Add(Path.Combine(here, "..", "..", "app", exe));

        if (!string.IsNullOrEmpty(stickRoot))
        {
            candidates.Add(Path.Combine(stickRoot, "app", exe));
#if DEBUG
            // Development builds run from source\...\bin\Release\net8.0-windows..., so the
            // sibling exe lives there. Never in a shipped build: a hard-coded TFM in
            // product code rots silently at the next retarget.
            candidates.Add(Path.Combine(stickRoot, "source", "src", "PortableDev.App", "bin", "Release",
                "net8.0-windows10.0.19041.0", exe));
#endif
        }

        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (!File.Exists(full)) continue;

                lock (CacheGate) _cached = full;
                return full;
            }
            catch
            {
                // ignore malformed candidates
            }
        }

        return string.Empty;
    }

    public static bool Available(string? stickRoot) => Locate(stickRoot).Length > 0;

    /// <summary>
    /// Run the CLI. Output lines are streamed to <paramref name="onLine"/> as they arrive
    /// so the progress panel can show the same lines the console would print.
    /// </summary>
    public static async Task<CliResult> RunAsync(
        string? stickRoot,
        IEnumerable<string> args,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var exe = Locate(stickRoot);
        if (exe.Length == 0)
            return new CliResult { ExitCode = 127, Error = "PortableDev.exe not found next to the GUI or under app\\" };

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        // UAC cannot be answered from a hidden console, but the helper needs a window
        // to be a legitimate elevation request; the CLI handles that itself.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var lines = new ConcurrentQueue<string>();

        // Process.Exited is raised on a thread-pool thread; the TCS turns it into an
        // awaitable, so completion no longer depends on noticing a 50 ms poll.
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            lines.Enqueue(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            lines.Enqueue(e.Data);
        };
        p.Exited += (_, _) => exited.TrySetResult(true);

        if (!p.Start())
            return new CliResult { ExitCode = 127, Error = "could not start " + exe };

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (p.HasExited) exited.TrySetResult(true);

        using (ct.Register(() =>
               {
                   try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                   exited.TrySetResult(true);
               }))
        {
            // Polling is kept only to stream the queued lines while the CLI talks; exit is
            // detected by the event above. The old loop condition (!HasExited) could finish
            // before the last batch of lines had been drained, dropping them.
            while (!exited.Task.IsCompleted)
            {
                Drain(lines, onLine);
                await Task.WhenAny(exited.Task, Task.Delay(50, CancellationToken.None));
            }
        }

        // The parameterless overload also waits for the asynchronous output readers, so a
        // line queued just before exit is not lost.
        try
        {
            if (ct.IsCancellationRequested) p.WaitForExit(2000);
            else p.WaitForExit();
        }
        catch
        {
            // the process may already be gone; keep whatever was captured
        }

        while (!lines.IsEmpty) Drain(lines, onLine);

        return new CliResult
        {
            ExitCode = p.HasExited ? p.ExitCode : -1,
            Output = stdout.ToString(),
            Error = stderr.ToString(),
        };
    }

    private static void Drain(ConcurrentQueue<string> q, Action<string>? onLine)
    {
        while (q.TryDequeue(out var line)) onLine?.Invoke(line);
    }
}
