namespace PortableDev.Kernel;

/// <summary>Well-known locations inside the stick (architecture section 6).</summary>
public sealed class KernelPaths
{
    public KernelPaths(string root, string appRoot)
    {
        Root = root;
        AppRoot = appRoot;
    }

    public string Root { get; }

    public string AppRoot { get; }

    public string State => Path.Combine(Root, "state");

    public string Config => Path.Combine(Root, "config");

    public string Logs => Path.Combine(State, "logs");

    public string Intents => Path.Combine(State, "intent");

    public string Ledger => Path.Combine(State, "ledger");

    public string Records => Path.Combine(State, "records");

    public string Staging => Path.Combine(Root, "staging");

    public string Store => Path.Combine(Root, "store");

    public string Cache => Path.Combine(Root, "cache");

    public string Extensions => Path.Combine(Root, "extensions");

    public string AppExtensions => Path.Combine(AppRoot, "extensions");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    public string LockFile => Path.Combine(State, "lock.json");

    public string StickFile => Path.Combine(State, "stick.json");

    public string BootstrapLog => Path.Combine(Logs, "bootstrap.log");

    public string KernelLog => Path.Combine(Logs, "kernel.jsonl");

    /// <summary>Creates the minimum layout so a dev stick can boot (`--init`).</summary>
    public void EnsureLayout()
    {
        foreach (var directory in new[] { State, Config, Logs, Intents, Ledger, Records, Staging, Store, Cache, Extensions, AppExtensions })
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>A stick is usable when its state and config directories exist (kernel.md 1.3).</summary>
    public bool LooksLikeStick() => Directory.Exists(State) && Directory.Exists(Config);
}
