using System.Text.Json;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>
/// Executes a plan step by step, journalling each mutation before it happens so
/// an interrupted run can be rolled back. Never writes anything that is not in
/// the plan.
///
/// Privilege model: only the stable junction needs administrator rights, so it is
/// delegated to a short-lived elevated helper (see <see cref="Broker"/>). PATH and
/// environment variables are written from this process, under the user's own token.
/// </summary>
public static class Applier
{
    /// <summary>Exit codes. Documented in README so scripts can branch on them.</summary>
    public static class Exit
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int StickNotFound = 4;
        public const int Blocked = 5;
        public const int NotConfirmed = 6;
        public const int JunctionFailed = 7;
        public const int ElevationUnavailable = 8;

        /// <summary>
        /// A home-link slot could not be linked. Distinct from <see cref="JunctionFailed"/>
        /// on purpose: a script that deals with the root junction (e.g. by running once
        /// elevated) must be able to tell the two apart.
        /// </summary>
        public const int HomeLinkFailed = 9;
    }

    public static int Run(CommandOptions opts)
    {
        var (root, count) = VolumeLocator.Resolve(opts.Root);
        if (root is null)
        {
            Console.Error.WriteLine(count == 0 ? "stick not found" : "multiple sticks found; use --root");
            return Exit.StickNotFound;
        }

        var manifest = ManifestLoader.Load(root);
        var resolver = new PathResolver(opts.Stable ?? manifest.Root.Stable, root);
        var state = MachineStateReader.Read(root, resolver.StableRoot, opts.TestRegistry,
            opts.NoJunction ? root : null);
        var plan = Program.BuildPlan(manifest, resolver, state, opts, root);

        if (plan.Warnings.Any(w => w.Blocker))
        {
            Console.Error.WriteLine("refusing to apply: blocking warnings present");
            foreach (var w in plan.Warnings.Where(w => w.Blocker))
                Console.Error.WriteLine($"  [BLOCK] {w.Code}: {w.Message}");
            return Exit.Blocked;
        }

        if (!plan.HasChanges)
        {
            Console.WriteLine("nothing to do; machine already matches the manifest.");
            return Exit.Ok;
        }

        var junctionStep = plan.Steps.FirstOrDefault(s => s.Kind == "junction");
        var needsJunction = junctionStep is not null && junctionStep.Change != ChangeKind.None;
        var elevated = RegistryEnvironment.IsElevated();

        // Fail before touching anything, not halfway through.
        if (needsJunction && !elevated && opts.NoElevate)
        {
            ExplainElevationRequired(resolver.StableRoot);
            return Exit.ElevationUnavailable;
        }

        if (!opts.Yes && !Confirm(plan, needsJunction && !elevated)) return Exit.NotConfirmed;

        var journalDir = Path.Combine(root, manifest.Apply.JournalDir.Replace('/', '\\'));
        var backupDir = Path.Combine(root, manifest.Apply.BackupDir.Replace('/', '\\'));
        var tempDir = Path.Combine(root, manifest.Apply.TempDir.Replace('/', '\\'));
        Directory.CreateDirectory(journalDir);
        Directory.CreateDirectory(backupDir);

        var journal = new Journal(journalDir, plan.TxId);
        var reg = new RegistryEnvironment(opts.TestRegistry);

        // Undo entries are pushed right before each mutation, so a failure halfway through
        // a loop still knows exactly what had already been changed.
        var undo = new UndoStack();
        var rollbackNotes = new List<string>();

        try
        {
            // 1. Backup before any mutation.
            var backupFile = Path.Combine(backupDir, $"path-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
            reg.ExportBackup(backupFile);
            File.WriteAllText(Path.ChangeExtension(backupFile, ".txt"), state.UserPathRaw);
            journal.Record("backup", new { backupFile, originalPath = state.UserPathRaw });
            Console.WriteLine($"backup   -> {backupFile}");

            // 2. Junction (elevated helper when needed).
            if (needsJunction)
            {
                journal.Record("junction.write", new
                {
                    link = resolver.StableRoot,
                    target = plan.StickRoot,
                    viaBroker = !elevated,
                });

                var (ok, err) = elevated
                    ? Junction.Create(resolver.StableRoot, plan.StickRoot)
                    : BrokerJunction(ElevatedJob.JunctionCreate, resolver.StableRoot, plan.StickRoot, tempDir, journal);

                if (!ok)
                {
                    var code = err is not null && err.Contains("declined", StringComparison.OrdinalIgnoreCase)
                        ? Exit.ElevationUnavailable
                        : Exit.JunctionFailed;
                    return Abort(opts, undo, rollbackNotes, reg, journal, code, $"junction failed: {err}");
                }

                // Trust but verify: the helper is a separate process.
                if (!Broker.JunctionMatches(resolver.StableRoot, plan.StickRoot))
                {
                    var msg = $"{resolver.StableRoot} is not a junction to {plan.StickRoot} after the write";
                    return Abort(opts, undo, rollbackNotes, reg, journal, Exit.JunctionFailed,
                        "junction failed: " + msg);
                }

                Console.WriteLine($"junction -> {resolver.StableRoot} => {plan.StickRoot}");
            }

            // 2b. Home links: directories that must live on the stick.
            foreach (var step in plan.Steps.Where(s => s.Kind == "home" && s.Change != ChangeKind.None))
            {
                var link = plan.HomeLinks.First(h => h.Display == step.Target);
                journal.Record("home.link", new { step.Target, link.HostPath, link.StorePath });

                // Only a link this run creates from nothing is undoable. A slot that already
                // held a real directory may have had its data moved onto the stick (adopt);
                // moving that data back would be a new risk, and it is safe where it is.
                if (!link.IsLink && !(link.Exists && link.IsRealWithContent))
                {
                    undo.Push(new UndoStep
                    {
                        Kind = UndoKind.HomeLink,
                        Target = link.HostPath,
                        StorePath = link.StorePath,
                    });
                }
                else if (link.Exists && !link.IsLink && link.IsRealWithContent)
                {
                    rollbackNotes.Add(link.HostPath + " moved onto the stick; a rollback leaves the data there");
                }

                var (ok, err) = HomeLinks.Apply(
                    link, manifest.Apply.HomeAdopt, journal, plan.StableRoot, plan.StickRoot);
                if (!ok)
                {
                    return Abort(opts, undo, rollbackNotes, reg, journal, Exit.HomeLinkFailed,
                        $"home link failed ({step.Target}): {err}");
                }
                Console.WriteLine($"home     -> {step.Target}  {link.HostPath} => {link.StorePath}");
            }

            // 3. Environment variables.
            // The RAW (unexpanded) values are the only correct rollback source:
            // state.CurrentEnv is expanded, and writing that back would turn %USERPROFILE%
            // into a literal path.
            var envBefore = reg.ReadUserEnvRaw();
            foreach (var (name, value) in plan.EnvToSet)
            {
                journal.Record("env.write", new { name, value });
                var had = envBefore.TryGetValue(name, out var previousRaw);
                undo.Push(new UndoStep
                {
                    Kind = UndoKind.Env,
                    Target = name,
                    Value = previousRaw,
                    Existed = had,
                });
                reg.WriteEnv(name, value);
            }
            Console.WriteLine($"env      -> {plan.EnvToSet.Count} variable(s)");

            // 4. PATH last, so a failure above leaves PATH untouched.
            if (!string.Equals(plan.NewUserPathRaw, state.UserPathRaw, StringComparison.Ordinal))
            {
                journal.Record("path.write", new { before = state.UserPathRaw, after = plan.NewUserPathRaw });
                undo.Push(new UndoStep
                {
                    Kind = UndoKind.Path,
                    Target = "PATH",
                    Value = state.UserPathRaw,
                });
                reg.WriteUserPathRaw(plan.NewUserPathRaw);
                Console.WriteLine("path     -> updated");
            }

            // 5. Tell the shell. Writing the registry is not enough: Explorer caches
            //    its environment block, so without this broadcast every program it
            //    starts afterwards (a new terminal included) sees the old PATH.
            var notified = EnvironmentBroadcast.Notify();
            journal.Record("broadcast", new { ok = notified });
            Console.WriteLine(notified
                ? "notify   -> environment change broadcast"
                : "notify   -> WARNING broadcast failed; log off/on to refresh Explorer");

            // UIPI blocks WM_SETTINGCHANGE from a high-integrity process to a
            // medium-integrity Explorer, so an elevated apply cannot refresh the
            // shell. Say so instead of leaving the user with "command not found".
            if (RegistryEnvironment.IsElevated())
            {
                Console.WriteLine("note     -> this ran elevated: Windows drops the notification for");
                Console.WriteLine("            Explorer (UIPI), so already-open and newly opened");
                Console.WriteLine("            terminals may still show the old PATH.");
                Console.WriteLine("            Use 'PortableDev.cmd shell' for a terminal with the");
                Console.WriteLine("            environment applied, or log off and on once.");
            }

            journal.Record("done", new { });
            Console.WriteLine();
            Console.WriteLine("applied. Open a NEW terminal for PATH changes to take effect.");
            return Exit.Ok;
        }
        catch (Exception ex)
        {
            return Abort(opts, undo, rollbackNotes, reg, journal, Exit.Failed, "apply failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Report a failed apply and undo whatever this run had already changed.
    ///
    /// Without this, a failure between the env write and the PATH write left the machine
    /// with half an environment and 'clean' as the only way out — and clean removes
    /// everything, including what was there before, instead of restoring the previous state.
    /// </summary>
    private static int Abort(
        CommandOptions opts,
        UndoStack undo,
        IReadOnlyList<string> rollbackNotes,
        RegistryEnvironment reg,
        Journal journal,
        int exitCode,
        string message)
    {
        Console.Error.WriteLine(message);
        journal.Record("failed", new { error = message });
        Console.Error.WriteLine($"journal: {journal.Path}");

        if (opts.NoRollback)
        {
            journal.Record("rollback.skipped", new { reason = "--no-rollback" });
            Console.Error.WriteLine("--no-rollback: the partially applied environment is left in place.");
            Console.Error.WriteLine("run 'clean' to remove everything this tool manages.");
            return exitCode;
        }

        if (undo.Count == 0)
        {
            Console.Error.WriteLine("nothing had been written yet; nothing to roll back.");
            return exitCode;
        }

        Console.Error.WriteLine("rolling back...");
        var (undone, failed) = Rollback(undo, reg, journal);

        foreach (var note in rollbackNotes)
        {
            journal.Record("rollback.note", new { note });
            Console.Error.WriteLine("note: " + note);
        }

        Console.Error.WriteLine(failed == 0
            ? $"rolled back {undone} change(s); the machine is back to its previous state."
            : $"rolled back {undone} change(s), {failed} could not be undone (see {journal.Path}).");
        return exitCode;
    }

    /// <summary>
    /// Replay the undo stack in reverse order. One failing step must not stop the rest:
    /// a stuck PATH write is no reason to leave every environment variable behind too.
    /// Returns (undone, failed).
    /// </summary>
    private static (int Undone, int Failed) Rollback(UndoStack undo, RegistryEnvironment reg, Journal journal)
    {
        var undone = 0;
        var failed = 0;

        foreach (var step in undo.Reverse())
        {
            try
            {
                switch (step.Kind)
                {
                    case UndoKind.Env:
                        if (step.Existed) reg.WriteEnv(step.Target, step.Value ?? string.Empty);
                        else reg.DeleteEnv(step.Target);
                        break;

                    case UndoKind.Path:
                        reg.WriteUserPathRaw(step.Value ?? string.Empty);
                        break;

                    case UndoKind.HomeLink:
                        // Only remove a link this run created, and only while it still points
                        // where we put it: somebody else may have repointed it since.
                        var actual = Junction.GetTarget(step.Target);
                        if (actual is null) break;
                        if (step.StorePath is not null && !PathEditor.PathsEqual(actual, step.StorePath)) break;

                        var (removed, removeError) = Junction.Remove(step.Target);
                        if (!removed) throw new IOException(removeError ?? "junction removal failed");
                        break;
                }

                undone++;
                journal.Record("rollback.undone", new { kind = step.Kind.ToString(), step.Target });
            }
            catch (Exception ex)
            {
                failed++;
                journal.Record("rollback.failed", new
                {
                    kind = step.Kind.ToString(),
                    step.Target,
                    error = ex.Message,
                });
            }
        }

        return (undone, failed);
    }

    public static int Clean(CommandOptions opts)
    {
        var (root, count) = VolumeLocator.Resolve(opts.Root);
        if (root is null)
        {
            Console.Error.WriteLine(count == 0 ? "stick not found" : "multiple sticks found; use --root");
            return Exit.StickNotFound;
        }

        var manifest = ManifestLoader.Load(root);
        var stable = opts.Stable ?? manifest.Root.Stable;
        var resolver = new PathResolver(stable, root);
        var reg = new RegistryEnvironment(opts.TestRegistry);

        var raw = reg.ReadUserPathRaw();
        var newPath = PathEditor.Rebuild(raw, stable, Array.Empty<string>());
        var junctionExists = Directory.Exists(stable) && Junction.IsReparsePoint(stable);
        var elevated = RegistryEnvironment.IsElevated();

        Console.WriteLine($"PATH before: {raw}");
        Console.WriteLine($"PATH after : {newPath}");
        Console.WriteLine($"junction   : {stable}{(junctionExists ? " (exists)" : " (absent)")}");

        if (junctionExists && !elevated && opts.NoElevate)
        {
            ExplainElevationRequired(stable);
            return Exit.ElevationUnavailable;
        }

        if (!opts.Yes && !ConfirmRaw("Remove the junction and restore PATH?")) return Exit.NotConfirmed;

        var journalDir = Path.Combine(root, manifest.Apply.JournalDir.Replace('/', '\\'));
        var backupDir = Path.Combine(root, manifest.Apply.BackupDir.Replace('/', '\\'));
        var tempDir = Path.Combine(root, manifest.Apply.TempDir.Replace('/', '\\'));
        Directory.CreateDirectory(journalDir);
        Directory.CreateDirectory(backupDir);

        var txId = "clean-" + Guid.NewGuid().ToString("N")[..12];
        var journal = new Journal(journalDir, txId);

        var backupFile = Path.Combine(backupDir, $"path-{DateTime.Now:yyyyMMdd-HHmmss}.reg");

        // Every mutation is wrapped: an exception used to escape to Program.Main and become
        // a bare "error: ..." with no journal entry, so the user could not tell how far
        // clean had got. Now it is always journalled and the stopping point is named.
        try
        {
            reg.ExportBackup(backupFile);
            journal.Record("backup", new { backupFile, originalPath = raw });
            Console.WriteLine($"backup   -> {backupFile}");

            // Home links first: they are the outermost thing pointing into the stick, so
            // removing them before the junction keeps every state consistent.
            var homeState = MachineStateReader.ReadHomeLinks(root, stable);
            foreach (var link in homeState.Where(h => h.IsLink))
            {
                journal.Record("home.unlink", new { link.Display, link.HostPath, link.LinkTarget });
                var (ok, err) = Junction.Remove(link.HostPath);
                if (!ok)
                {
                    Console.Error.WriteLine($"home link removal failed ({link.Display}): {err}");
                    journal.Record("failed", new { step = "home.unlink", link.Display, err });
                    return Exit.HomeLinkFailed;
                }
                Console.WriteLine($"home     -> removed link {link.Display} (data stays on the stick)");
            }

            // Junction next, PATH last: same ordering rule as apply.
            if (junctionExists)
            {
                journal.Record("junction.remove", new { link = stable });
                var (ok, err) = elevated
                    ? Junction.Remove(stable)
                    : BrokerJunction(ElevatedJob.JunctionRemove, stable, null, tempDir, journal);

                if (!ok)
                {
                    Console.Error.WriteLine($"junction removal failed: {err}");
                    journal.Record("failed", new { step = "junction", err });
                    return Exit.JunctionFailed;
                }
            }

            if (!string.Equals(raw, newPath, StringComparison.Ordinal))
            {
                journal.Record("path.write", new { before = raw, after = newPath });
                reg.WriteUserPathRaw(newPath);
            }

            var notified = EnvironmentBroadcast.Notify();
            journal.Record("broadcast", new { ok = notified });

            journal.Record("done", new { });
            Console.WriteLine("cleaned. Open a NEW terminal for the change to take effect.");
            return Exit.Ok;
        }
        catch (Exception ex)
        {
            journal.Record("failed", new { error = ex.Message });
            Console.Error.WriteLine("clean failed: " + ex.Message);
            Console.Error.WriteLine($"journal: {journal.Path}");
            Console.Error.WriteLine("the machine may be partially cleaned; fix the cause and run clean again.");
            return Exit.Failed;
        }
    }

    /// <summary>
    /// Routes a junction operation through the elevated helper and journals the
    /// outcome. Returns (ok, error).
    /// </summary>
    private static (bool Ok, string? Error) BrokerJunction(
        string job, string link, string? target, string tempDir, Journal journal)
    {
        Console.WriteLine("elevating-> waiting for the UAC prompt (junction only)...");
        var outcome = Broker.RunJunctionJob(job, link, target, tempDir);

        journal.Record("elevate", new
        {
            job,
            ok = outcome.Ok,
            cancelled = outcome.Cancelled,
            exitCode = outcome.ExitCode,
            error = outcome.Error,
            result = outcome.Result,
        });

        return (outcome.Ok, outcome.Error);
    }

    private static void ExplainElevationRequired(string stableRoot)
    {
        Console.Error.WriteLine($"elevation required: creating or removing '{stableRoot}' needs administrator rights.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Either:");
        Console.Error.WriteLine("  - run apply again without --no-elevate and approve the UAC prompt, or");
        Console.Error.WriteLine("  - start this program from an administrator terminal, or");
        Console.Error.WriteLine($"  - create the junction once yourself:  cmd /c mklink /J {stableRoot} <stick path>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Nothing was changed.");
    }

    public static int RunElevatedJob(CommandOptions opts) => ElevatedJob.Run(opts);

    private static bool Confirm(Plan plan, bool elevationPrompt)
    {
        var changes = plan.Steps.Count(s => s.Change != ChangeKind.None);
        Console.WriteLine($"apply {changes} change(s)?{(elevationPrompt ? " (one UAC prompt will appear)" : "")} [y/N] ");
        var answer = Console.ReadLine();
        return answer is not null && answer.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static bool ConfirmRaw(string message)
    {
        Console.WriteLine(message + " [y/N] ");
        var answer = Console.ReadLine();
        return answer is not null && answer.Trim().ToLowerInvariant() is "y" or "yes";
    }
}

/// <summary>Append-only JSONL journal, one file per transaction.</summary>
public sealed class Journal
{
    public string Path { get; }

    public Journal(string dir, string txId)
    {
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, $"{txId}.jsonl");
    }

    public void Record(string kind, object payload)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.Now.ToString("O"),
            kind,
            payload,
        });
        File.AppendAllText(Path, line + Environment.NewLine);
    }
}
