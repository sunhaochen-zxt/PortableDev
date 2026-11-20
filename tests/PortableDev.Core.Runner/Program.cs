// Self-contained test runner: bypasses the dotnet test testhost parent-process issue.
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Core.Packages;
using PortableDev.Core.Update;

var failures = 0;
var passed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine($"PASS  {name}"); }
    else { failures++; Console.WriteLine($"FAIL  {name}{(detail is null ? "" : "  :: " + detail)}"); }
}

void Eq(string name, string expected, string actual) =>
    Check(name, expected == actual, $"expected [{expected}] got [{actual}]");

const string Root = @"C:\Dev";

// The real stick root is found by walking up from the test binary to the marker file.
// Hard-coding "E:\PortableDev" made the suite pass on one machine and fail on the next,
// which is exactly the kind of environment coupling a test must not have.
var stickRoot = FindStickRoot(AppContext.BaseDirectory);
Check("Stick root found", stickRoot.Length > 0,
    "no .portabledev-root above " + AppContext.BaseDirectory);

static string FindStickRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        try
        {
            if (File.Exists(Path.Combine(dir.FullName, ".portabledev-root")))
                return dir.FullName;
        }
        catch
        {
            // unreadable ancestor: keep looking
        }
    }
    return string.Empty;
}

Check("Split drops empties", PathEditor.Split(@"a;b;;  ;c").SequenceEqual(new[] { "a", "b", "c" }));

Check("IsUnderRoot child", PathEditor.IsUnderRoot(@"C:\Dev\python", Root));
Check("IsUnderRoot case-insensitive", PathEditor.IsUnderRoot(@"c:\dev\python", Root));
Check("IsUnderRoot root itself", PathEditor.IsUnderRoot(@"C:\Dev", Root));
Check("IsUnderRoot trailing slash", PathEditor.IsUnderRoot(@"C:\Dev\", Root));
Check("IsUnderRoot prefix-not-child", !PathEditor.IsUnderRoot(@"C:\Development\bin", Root));
Check("IsUnderRoot other drive", !PathEditor.IsUnderRoot(@"D:\Dev", Root));

Eq("Rebuild prepends+preserves",
   @"C:\Dev\python;C:\Dev\node;C:\Windows;C:\Program Files\Git\cmd;%USERPROFILE%\bin",
   PathEditor.Rebuild(@"C:\Windows;C:\Program Files\Git\cmd;%USERPROFILE%\bin", Root,
       new[] { @"C:\Dev\python", @"C:\Dev\node" }));

{
    var raw = @"C:\Windows;C:\Dev\old;C:\Tools";
    var once = PathEditor.Rebuild(raw, Root, new[] { @"C:\Dev\python" });
    var twice = PathEditor.Rebuild(once, Root, new[] { @"C:\Dev\python" });
    Eq("Rebuild idempotent", once, twice);
    Check("Rebuild drops stale managed", !once.Contains(@"C:\Dev\old"));
    Check("Rebuild keeps foreign", once.Contains(@"C:\Windows") && once.Contains(@"C:\Tools"));
}

Eq("Rebuild keeps %VARS%",
   @"C:\Dev\python;%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;C:\Users\admin\go\bin",
   PathEditor.Rebuild(@"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;C:\Users\admin\go\bin", Root,
       new[] { @"C:\Dev\python" }));

Eq("Rebuild strips quotes on foreign",
   @"C:\Dev\python;C:\Program Files\Git\cmd;C:\Tools",
   PathEditor.Rebuild("\"C:\\Program Files\\Git\\cmd\";C:\\Tools", Root, new[] { @"C:\Dev\python" }));

Eq("Rebuild dedups foreign", @"C:\Tools",
   PathEditor.Rebuild(@"C:\Tools;C:\Tools;c:\tools", Root, Array.Empty<string>()));

{
    var raw = @"C:\Dev\a;C:\Win;C:\Dev\b;C:\Program Files";
    Check("Managed partition", PathEditor.ManagedEntries(raw, Root).SequenceEqual(new[] { @"C:\Dev\a", @"C:\Dev\b" }));
    Check("Foreign partition", PathEditor.ForeignEntries(raw, Root).SequenceEqual(new[] { @"C:\Win", @"C:\Program Files" }));
}

Eq("Rebuild null raw", @"C:\Dev\python", PathEditor.Rebuild(null, Root, new[] { @"C:\Dev\python" }));
Eq("Rebuild null raw no desired", "", PathEditor.Rebuild(null, Root, Array.Empty<string>()));

{
    var r = PathEditor.Rebuild(@"C:\Windows", Root, new[] { @"C:\Dev\python" });
    Check("No foreign drive letter leaks", !r.Contains("E:") && !r.Contains("D:"));
}

const string Stable = @"C:\Dev";
const string Stick = @"E:\PortableDev";

RootManifest MakeManifest()
{
    var py = new ToolManifest
    {
        Tool = new ToolManifest.ToolSection { Id = "python", Version = "3.13.17", Kind = "tar.gz", Provides = new[] { "python" } },
        Path = new[] { new ToolManifest.PathEntry { Dir = "." }, new ToolManifest.PathEntry { Dir = "Scripts" } },
        Env = new[] { new EnvVarSpec { Name = "PYTHONUTF8", Value = "1" } },
    };
    var node = new ToolManifest
    {
        Tool = new ToolManifest.ToolSection { Id = "nodejs", Version = "24.22.1", Kind = "zip", Provides = new[] { "node" } },
        Path = new[] { new ToolManifest.PathEntry { Dir = "." } },
    };
    return new RootManifest
    {
        Root = new RootManifest.RootSection { Stable = Stable, Marker = ".portabledev-root" },
        PathOrder = new[] { "python", "nodejs" },
        Env = new[] { new EnvVarSpec { Name = "HOME", Value = "{{ROOT}}\\home" } },
        Tools = new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] = py,
            ["nodejs"] = node,
        },
    };
}

MachineState State(string userPath = @"C:\Windows;C:\Users\admin\go\bin", string? junctionTarget = null) => new()
{
    UserPathRaw = userPath,
    StickRoot = Stick,
    JunctionExists = junctionTarget is not null,
    JunctionTarget = junctionTarget,
};

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(State());
    var j = plan.Steps.Single(s => s.Kind == "junction");
    Check("Plan junction add", j.Change == ChangeKind.Add && j.After == Stick);
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var j = planner.Build(State(junctionTarget: Stick)).Steps.Single(s => s.Kind == "junction");
    Check("Plan junction unchanged", j.Change == ChangeKind.None);
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var j = planner.Build(State(junctionTarget: @"F:\Other")).Steps.Single(s => s.Kind == "junction");
    Check("Plan junction repair", j.Change == ChangeKind.Update && j.Before == @"F:\Other" && j.After == Stick);
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(State());
    var p = plan.NewUserPathRaw;
    Check("Path has python root", p.Contains(@"C:\Dev\tools\python\3.13.17"));
    Check("Path has python Scripts", p.Contains(@"C:\Dev\tools\python\3.13.17\Scripts"));
    Check("Path has nodejs", p.Contains(@"C:\Dev\tools\nodejs\24.22.1"));
    Check("Path keeps foreign C:\\Windows", p.Contains(@"C:\Windows"));
    Check("Path keeps foreign go bin", p.Contains(@"C:\Users\admin\go\bin"));
    Check("Path has no stick drive", !p.Contains("E:"));
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var first = planner.Build(State());
    var second = planner.Build(State(userPath: first.NewUserPathRaw));
    Eq("Plan idempotent", first.NewUserPathRaw, second.NewUserPathRaw);
    Check("Plan 2nd run has no path changes",
        !second.Steps.Any(s => s.Kind == "path" && s.Change != ChangeKind.None),
        string.Join(",", second.Steps.Where(s => s.Kind == "path" && s.Change != ChangeKind.None).Select(s => s.Change + ":" + s.Target)));
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(State(userPath: @"C:\Dev\tools\python\3.12.0;C:\Windows"));
    Check("Plan removes stale managed", !plan.NewUserPathRaw.Contains("3.12.0"));
    Check("Plan emits remove step", plan.Steps.Any(s => s.Kind == "path" && s.Change == ChangeKind.Remove));
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(State());
    Check("Env HOME resolved", plan.EnvToSet["HOME"] == @"C:\Dev\home");
    Check("Env PYTHONUTF8 resolved", plan.EnvToSet["PYTHONUTF8"] == "1");
}

{
    var state = new MachineState
    {
        UserPathRaw = "",
        StickRoot = Stick,
        CurrentEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PYTHONUTF8"] = "0" },
    };
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(state);
    var py = plan.Steps.Single(s => s.Kind == "env" && s.Target == "PYTHONUTF8");
    Check("Env update detected", py.Change == ChangeKind.Update && py.Before == "0");
    var home = plan.Steps.Single(s => s.Kind == "env" && s.Target == "HOME");
    Check("Env add detected", home.Change == ChangeKind.Add);
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(State(), new PlannerOptions { NoJunction = true });
    Check("Degraded flag", plan.Degraded);
    Check("Degraded uses stick path", plan.NewUserPathRaw.Contains(@"E:\PortableDev\tools\python\3.13.17"));
    Check("Degraded warning", plan.Warnings.Any(w => w.Code == "degraded"));
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(State(), new PlannerOptions
    {
        DirectoryExists = _ => false,
        TreatMissingToolsAsBlocker = true,
    });
    var missing = plan.Warnings.Where(w => w.Code == "tool-missing").ToList();
    Check("Missing tools warned", missing.Count == 2);
    Check("Missing tools blocker", missing.All(w => w.Blocker));
}

{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));
    var plan = planner.Build(State(userPath: @"C:\Python39\python;C:\Windows"));
    Check("Shadow warning", plan.Warnings.Any(w => w.Code == "shadowed"));
    Check("Shadow not removed", plan.NewUserPathRaw.Contains(@"C:\Python39\python"));
}

{
    var m = MakeManifest();
    var disabled = new ToolManifest
    {
        Tool = new ToolManifest.ToolSection { Id = "nodejs", Version = "24.22.1", Enabled = false },
        Path = new[] { new ToolManifest.PathEntry { Dir = "." } },
    };
    var withDisabled = new RootManifest
    {
        Root = m.Root,
        PathOrder = m.PathOrder,
        Tools = new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase) { ["nodejs"] = disabled },
    };
    var planner = new Planner(withDisabled, new PathResolver(Stable, Stick));
    var plan = planner.Build(State());
    Check("Disabled tool excluded", !plan.NewUserPathRaw.Contains("nodejs"));
}

{
    // ---- PlannerOptions.Live: the hooks both front-ends must share ----------
    // The GUI once assembled its options by hand and omitted FileText/DirectoryExists, so
    // its preview planned against an empty disk and promised changes that did not exist.
    var live = PlannerOptions.Live();
    var liveFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "pd-live-" + Guid.NewGuid().ToString("N") + ".txt");
    try
    {
        System.IO.File.WriteAllText(liveFile, "probe");
        Check("PlannerOptions.Live reads file text", live.FileText(liveFile) == "probe");
        Check("PlannerOptions.Live reports a missing file as null", live.FileText(liveFile + ".nope") is null);
        Check("PlannerOptions.Live sees real directories", live.DirectoryExists(System.IO.Path.GetTempPath()));
        Check("PlannerOptions.Live does not invent directories",
            !live.DirectoryExists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
    finally
    {
        if (System.IO.File.Exists(liveFile)) System.IO.File.Delete(liveFile);
    }
}

{
    // ---- [[launch]]: generated launchers -----------------------------------
    var launchTool = new ToolManifest
    {
        Tool = new ToolManifest.ToolSection { Id = "app", Version = "1.0", Kind = "prebuilt", Dir = "tools/app/1.0" },
        Launch = new[]
        {
            new LaunchSpec
            {
                Name = "App",
                Exe = "app.exe",
                Args = new[] { "-profile", "{{STICK}}home\\app" },
                Env = new[] { new EnvVarSpec { Name = "APP_PORTABLE", Value = "1" } },
            },
        },
    };

    var launchManifest = new RootManifest
    {
        Tools = new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase) { ["app"] = launchTool },
    };
    var launchResolver = new PathResolver(Stable, Stick);
    var spec = launchTool.Launch[0];
    var rendered = LaunchWriter.Render(spec, launchTool, launchResolver);

    Check("Launcher starts the exe from its own folder",
        rendered.Contains(@"start """" /d ""%~dp0tools\app\1.0"" ""%~dp0tools\app\1.0\app.exe"""),
        rendered);
    Check("Launcher sets its env for the child", rendered.Contains("set \"APP_PORTABLE=1\""), rendered);
    Check("Launcher expands {{STICK}} to %~dp0 and quotes it",
        rendered.Contains(@"-profile ""%~dp0home\app"""), rendered);
    Check("Launcher says it is generated", rendered.Contains("do not edit"), rendered);
    Check("Launcher path is <stick>\\<Name>.cmd",
        LaunchWriter.Path(Stick, spec) == System.IO.Path.Combine(Stick, "App.cmd"),
        LaunchWriter.Path(Stick, spec));

    // Missing file -> Add; identical content -> None; different content -> Update.
    var launcherPath = LaunchWriter.Path(Stick, spec);

    Plan PlanWith(Func<string, string?> fileText) =>
        new Planner(launchManifest, launchResolver).Build(
            new MachineState { UserPathRaw = @"C:\Windows", StickRoot = Stick },
            new PlannerOptions { FileText = fileText, DirectoryExists = _ => true });

    var addPlan = PlanWith(_ => null);
    var addStep = addPlan.Steps.First(s => s.Kind == "launcher");
    Check("Launcher missing -> Add", addStep.Change == ChangeKind.Add, addStep.Change.ToString());
    Check("Launcher Add points at the stick root", addStep.Target == launcherPath, addStep.Target);

    var samePlan = PlanWith(p => p == launcherPath ? rendered : null);
    Check("Launcher unchanged -> None",
        samePlan.Steps.First(s => s.Kind == "launcher").Change == ChangeKind.None);

    var driftPlan = PlanWith(p => p == launcherPath ? "@echo off\r\nrem old\r\n" : null);
    Check("Launcher content drift -> Update",
        driftPlan.Steps.First(s => s.Kind == "launcher").Change == ChangeKind.Update);
}

{
    try
    {
        var m = ManifestLoader.Load(stickRoot);
        Check("Manifest loads", true);
        Check("Manifest tool count == 18", m.Tools.Count == 18, $"got {m.Tools.Count}");
        Check("Manifest stable root", m.Root.Stable == @"C:\Dev", m.Root.Stable);
        Check("Manifest path order == 15", m.PathOrder.Count == 15, $"got {m.PathOrder.Count}");
        Check("Manifest env non-empty", m.Env.Count > 0);

        var resolver = new PathResolver(m.Root.Stable, Stick);
        var planner = new Planner(m, resolver);
        var plan = planner.Build(new MachineState { UserPathRaw = @"C:\Windows", StickRoot = Stick });

        var toolSteps = plan.Steps.Where(s => s.Kind == "tool").ToList();
        Check("All 18 tools present", toolSteps.Count == 18, $"got {toolSteps.Count}");
        Check("All 18 tools found on disk", toolSteps.All(s => s.Change == ChangeKind.None),
            string.Join(",", toolSteps.Where(s => s.Change != ChangeKind.None).Select(s => s.Target)));

        // Derive the versions from the manifest instead of hard-coding them: this test is
        // about "the prebuilt dir resolves to a stable-root path", not about which version
        // happens to be installed, and a literal here turns every tool upgrade red.
        var vscodeVersion = m.Tools["vscode"].Tool.Version;
        var llvmVersion = m.Tools["llvm-mingw"].Tool.Version;
        Check("VSCode prebuilt dir resolves", plan.NewUserPathRaw.Contains(@"C:\Dev\tools\vscode\" + vscodeVersion + @"\bin"),
            plan.NewUserPathRaw);
        Check("llvm-mingw bin resolves", plan.NewUserPathRaw.Contains(@"C:\Dev\tools\llvm-mingw\" + llvmVersion + @"\bin"));

        var second = planner.Build(new MachineState { UserPathRaw = plan.NewUserPathRaw, StickRoot = Stick });
        Check("Real manifest plan idempotent", second.NewUserPathRaw == plan.NewUserPathRaw);
        Check("Real manifest 2nd run no path churn",
            !second.Steps.Any(s => s.Kind == "path" && s.Change != ChangeKind.None));
        Check("Real plan has no stick drive in PATH", !plan.NewUserPathRaw.Contains(@"E:\"));

        // Profiles: declared inside a tool manifest, collected globally, never applied.
        var profile = m.FindProfile("msvc");
        Check("Profile msvc declared", profile is not null);
        Check("Profile is owned by its tool", profile?.ToolId == "msvc", profile?.ToolId);
        Check("Profile has env", (profile?.Env.Count ?? 0) >= 6, $"{profile?.Env.Count}");
        Check("Profile lookup is case-insensitive", m.FindProfile("MSVC") is not null);
        Check("Unknown profile returns null", m.FindProfile("nope") is null);
        Check("Profile env never reaches PATH", !plan.NewUserPathRaw.Contains("EnterpriseWDK"));

        // {{TOOL}} inside a profile must expand to that tool's directory on the stable root.
        var toolSpec = m.Tools["msvc"];
        var expanded = resolver.Expand(
            profile?.Env.First(e => e.Name == "VSINSTALLDIR").Value ?? "", toolSpec);
        Check("Profile {{TOOL}} expands to the tool dir",
            expanded == Path.Combine(Stable, "tools", "msvc"), expanded);
    }
    catch (Exception ex)
    {
        Check("Manifest loads", false, ex.Message);
    }
}

// ---- Home links: directories that must travel on the stick ----
{
    var m = MakeManifest();
    var withHome = new RootManifest
    {
        Root = m.Root,
        Apply = new RootManifest.ApplySection { HomeAdopt = true },
        PathOrder = m.PathOrder,
        Env = m.Env,
        Tools = m.Tools,
        HomeDirs = new[]
        {
            new RootManifest.HomeDirSection { Root = "USERPROFILE", Sub = ".config", Store = "home/.config" },
            new RootManifest.HomeDirSection { Root = "APPDATA", Sub = "Code", Store = "home/Code" },
        },
    };

    var planner = new Planner(withHome, new PathResolver(Stable, Stick));
    var plan = planner.Build(State());
    var home = plan.Steps.Where(s => s.Kind == "home").ToList();

    Check("Home link planned per declared dir", home.Count == 2, $"got {home.Count}");
    Check("Home link is an addition when nothing exists",
        home.All(s => s.Change == ChangeKind.Add), string.Join(",", home.Select(s => s.Change)));
    Check("Home link points into the stick", home.All(s => s.After!.Contains(@"C:\Dev\home")),
        string.Join(" | ", home.Select(s => s.After)));
    // State() reports no junction, so this plan also wants to create the root junction,
    // which is what needs the prompt; the home links themselves must not add to that.
    var elevatedState = new MachineState
    {
        StickRoot = Stick,
        IsElevated = false,
        JunctionExists = true,
        JunctionTarget = Stick,
    };
    Check("Home links alone need no elevation",
        !new Planner(withHome, new PathResolver(Stable, Stick)).Build(elevatedState).NeedsElevation);

    // An existing real directory full of data must never be moved silently.
    var occupied = new MachineState
    {
        StickRoot = Stick,
        IsElevated = true,
        HomeLinks = new[]
        {
            new HomeLinkState
            {
                Display = "USERPROFILE/.config", RootVar = "USERPROFILE", Sub = ".config",
                HostPath = @"C:\Users\admin\.config", StorePath = @"C:\Dev\home\.config",
                Exists = true, IsLink = false, IsRealWithContent = true,
            },
        },
    };

    var blocked = planner.Build(occupied);
    Check("Occupied slot is not linked by default",
        blocked.Steps.First(s => s.Kind == "home" && s.Target == "USERPROFILE/.config").Change == ChangeKind.None);
    Check("Occupied slot raises home-blocked", blocked.Warnings.Any(w => w.Code == "home-blocked"));
    Check("home-blocked does not block apply", !blocked.Warnings.Where(w => w.Code == "home-blocked").Any(w => w.Blocker));

    var adoptPlanner = new Planner(withHome, new PathResolver(Stable, Stick));
    var adopted = adoptPlanner.Build(occupied, new PlannerOptions { HomeAdopt = true });
    Check("Adopt moves the occupied slot",
        adopted.Steps.First(s => s.Kind == "home" && s.Target == "USERPROFILE/.config").Change == ChangeKind.Add,
        string.Join(",", adopted.Steps.Where(s => s.Kind == "home").Select(s => s.Change)));

    // A link already pointing at the right store is a no-op, which keeps apply idempotent.
    var linked = new MachineState
    {
        StickRoot = Stick,
        IsElevated = true,
        HomeLinks = new[]
        {
            new HomeLinkState
            {
                Display = "USERPROFILE/.config", RootVar = "USERPROFILE", Sub = ".config",
                HostPath = @"C:\Users\admin\.config", StorePath = @"C:\Dev\home\.config",
                Exists = true, IsLink = true, LinkTarget = @"C:\Dev\home\.config",
            },
        },
    };
    var noop = new Planner(withHome, new PathResolver(Stable, Stick)).Build(linked);
    Check("Correct home link is unchanged",
        noop.Steps.First(s => s.Kind == "home" && s.Target == "USERPROFILE/.config").Change == ChangeKind.None);

    // A link pointing at an old location is repointed.
    var stale = new MachineState
    {
        StickRoot = Stick,
        IsElevated = true,
        HomeLinks = new[]
        {
            new HomeLinkState
            {
                Display = "USERPROFILE/.config", RootVar = "USERPROFILE", Sub = ".config",
                HostPath = @"C:\Users\admin\.config", StorePath = @"C:\Dev\home\.config",
                Exists = true, IsLink = true, LinkTarget = @"D:\elsewhere\config",
            },
        },
    };
    var repointed = new Planner(withHome, new PathResolver(Stable, Stick)).Build(stale);
    Check("Stale home link is repointed",
        repointed.Steps.First(s => s.Kind == "home" && s.Target == "USERPROFILE/.config").Change == ChangeKind.Update);
}

// ---- Windows command line quoting (used when re-launching the elevated helper) ----
Eq("Quote plain untouched", "abc", CommandLine.Quote("abc"));
Eq("Quote space", "\"a b\"", CommandLine.Quote("a b"));
Eq("Quote trailing backslash", @"""C:\Dev\\""", CommandLine.Quote(@"C:\Dev\"));
Eq("Quote embedded quote", "\"a\\\"b\"", CommandLine.Quote("a\"b"));
Eq("Quote space plus trailing backslash", @"""C:\Program Files\\""", CommandLine.Quote(@"C:\Program Files\"));
Eq("Quote empty", "\"\"", CommandLine.Quote(""));

Eq("Join mixes quoted and plain",
   @"--link C:\Dev --target ""E:\My Stick""",
   CommandLine.Join("--link", @"C:\Dev", "--target", @"E:\My Stick"));

// ---- Elevation requirement is derived, never guessed ----
{
    var planner = new Planner(MakeManifest(), new PathResolver(Stable, Stick));

    Check("NeedsElevation when junction missing and not elevated",
        planner.Build(State()).NeedsElevation);

    Check("No elevation needed when already elevated",
        !planner.Build(new MachineState { StickRoot = Stick, IsElevated = true }).NeedsElevation);

    Check("No elevation needed when the junction is already correct",
        !planner.Build(State(junctionTarget: Stick)).NeedsElevation);

    Check("No elevation needed to repair a stale junction target",
        planner.Build(State(junctionTarget: @"F:\Other")).NeedsElevation);

    var degraded = planner.Build(State(), new PlannerOptions { NoJunction = true });
    Check("No elevation needed in degraded mode", !degraded.NeedsElevation);
}

// ---- Re-pointing a manifest after an upgrade ----
{
    var toml = """
schema = 1

[tool]
id      = 'powershell'
version = "7.6.6"
dir     = 'tools/powershell/7.6.6'

[[verify]]
run    = 'pwsh --version'
expect = 'PowerShell 7.6.6'
""";
    var r = PortableDev.Gui.Engine.ManifestEditor.Retarget(
        toml, "7.6.6", "7.6.8", "downloads/powershell/7.6.8/x.zip", "sha", "tools/powershell/7.6.8");

    Check("Retarget returns text", r is not null);
    Check("Retarget bumps version", r!.Contains("version = \"7.6.8\""), r);
    Check("Retarget moves dir", r.Contains("dir     = \"tools/powershell/7.6.8\""), r);
    Check("Retarget moves expect", r.Contains("expect = 'PowerShell 7.6.8'"), r);
    Check("Retarget keeps id", r.Contains("id      = 'powershell'"), r);
    Check("Retarget keeps verify command", r.Contains("run    = 'pwsh --version'"), r);

    var noDir = """
[tool]
version = "1.0"
""";
    var nd = PortableDev.Gui.Engine.ManifestEditor.Retarget(noDir, "1.0", "1.1", "a.zip", "s", "tools/x/1.1")!;
    Check("Retarget does not invent a dir line", !nd.Contains("dir"), nd);
}

// ---- Stick selection: never silently pick one of several ----
{
    var none = StickSelection.Select(Array.Empty<StickCandidate>());
    Check("No candidates is unresolved", !none.Resolved && none.Count == 0 && !none.Ambiguous);
    Check("No candidates says so", none.Error!.Contains("no portable-dev root"), none.Error);

    var one = StickSelection.Select(new[] { new StickCandidate(@"E:\PortableDev", "pd-1") });
    Check("Single candidate resolves", one.Resolved && one.Root == @"E:\PortableDev" && one.Count == 1);

    var many = StickSelection.Select(new[]
    {
        new StickCandidate(@"E:\PortableDev", "pd-1"),
        new StickCandidate(@"F:\Stick", "pd-2"),
    });
    Check("Two candidates do not resolve", !many.Resolved, many.Root ?? "(null)");
    Check("Two candidates are flagged ambiguous", many.Ambiguous && many.Count == 2);
    Check("Ambiguous error names every root",
        many.Error!.Contains(@"E:\PortableDev") && many.Error.Contains(@"F:\Stick") && many.Error.Contains("--root"),
        many.Error);

    var picked = StickSelection.Select(many.Candidates, @"F:\Stick\");
    Check("Explicit root wins among many", picked.Resolved && picked.Root == @"F:\Stick", picked.Root ?? "(null)");
    Check("Explicit root is matched ignoring the trailing separator", !picked.Ambiguous);

    var miss = StickSelection.Select(many.Candidates, @"D:\Nope");
    Check("Unknown explicit root does not fall back to the first", !miss.Resolved && !miss.Ambiguous);

    Check("Normalize strips separators",
        StickSelection.Normalize(@"E:\PortableDev\\") == @"E:\PortableDev" && StickSelection.Normalize(" e:\\x ") == @"e:\x");
}

// ---- Tokenizing a verify command line ----
{
    Check("Tokenize plain", CommandLine.Tokenize("git --version").SequenceEqual(new[] { "git", "--version" }));
    Check("Tokenize empty", CommandLine.Tokenize("").Count == 0);
    Check("Tokenize extra whitespace", CommandLine.Tokenize("  rg   -V  ").SequenceEqual(new[] { "rg", "-V" }));

    var quotedExe = CommandLine.Tokenize(@"""C:\Program Files\tool\x.exe"" --version");
    Check("Tokenize keeps a quoted exe path whole",
        quotedExe.SequenceEqual(new[] { @"C:\Program Files\tool\x.exe", "--version" }),
        string.Join(" | ", quotedExe));

    var quotedArg = CommandLine.Tokenize(@"python -c ""import ssl,sqlite3;print(1)""");
    Check("Tokenize keeps a quoted argument whole",
        quotedArg.SequenceEqual(new[] { "python", "-c", "import ssl,sqlite3;print(1)" }),
        string.Join(" | ", quotedArg));

    Check("Tokenize handles an escaped quote",
        CommandLine.Tokenize(@"a \""b c\"" d").SequenceEqual(new[] { "a", "\"b", "c\"", "d" }),
        string.Join(" | ", CommandLine.Tokenize(@"a \""b c\"" d")));

    Check("Tokenize empty argument", CommandLine.Tokenize(@"a """" b").SequenceEqual(new[] { "a", "", "b" }));

    // Round trip: what Join produces must come back as the same tokens.
    var tokens = new[] { @"C:\Program Files\x.exe", "--flag", "a b", "" };
    Check("Tokenize is the inverse of Join",
        CommandLine.Tokenize(CommandLine.Join(tokens)).SequenceEqual(tokens),
        string.Join(" | ", CommandLine.Tokenize(CommandLine.Join(tokens))));
}

// ---- Rewriting a manifest must not invent version numbers ----
{
    var toml = """
[tool]
version = "7.6.8"

[[verify]]
run    = 'pwsh --version'
expect = 'PowerShell 7.6.8'
""";

    // The regression this guards: a partial old version used to turn 7.6.8 into 7.7.8,
    // a release that does not exist, and verify then failed against a invented number.
    var partial = PortableDev.Gui.Engine.ManifestEditor.Retarget(toml, "7.6", "7.7", null, null)!;
    Check("Partial version does not corrupt expect",
        partial.Contains("expect = 'PowerShell 7.6.8'") && !partial.Contains("7.7.8"), partial);

    var exact = PortableDev.Gui.Engine.ManifestEditor.Retarget(toml, "7.6.8", "7.7.0", null, null)!;
    Check("Exact version still rewrites expect", exact.Contains("expect = 'PowerShell 7.7.0'"), exact);

    Eq("ReplaceVersionToken leaves a longer version alone",
       "expect = 'PowerShell 7.6.8'",
       PortableDev.Gui.Engine.ManifestEditor.ReplaceVersionToken("expect = 'PowerShell 7.6.8'", "7.6", "7.7"));
    Eq("ReplaceVersionToken replaces a standalone version",
       "expect = 'Node v26.12.1'",
       PortableDev.Gui.Engine.ManifestEditor.ReplaceVersionToken("expect = 'Node v26.12.0'", "26.12.0", "26.12.1"));
    Eq("ReplaceVersionToken does not eat a regex dollar",
       "expect = '$1'",
       PortableDev.Gui.Engine.ManifestEditor.ReplaceVersionToken("expect = '$1'", "1.0", "1.0"));
}

// ---- Undo stack for a failed apply: last mutation is undone first ----
{
    var undo = new UndoStack();
    Check("Empty undo stack is empty", undo.Count == 0 && undo.Reverse().Count == 0);

    undo.Push(new UndoStep { Kind = UndoKind.Env, Target = "HOME", Value = @"%USERPROFILE%\h", Existed = true, ValueKind = "ExpandString" });
    undo.Push(new UndoStep { Kind = UndoKind.Env, Target = "PD_NEW", Existed = false });
    undo.Push(new UndoStep { Kind = UndoKind.Path, Target = "PATH", Value = @"%USERPROFILE%\bin;C:\Windows", ValueKind = "String" });
    undo.Push(new UndoStep
    {
        Kind = UndoKind.HomeLink,
        Target = @"C:\Users\admin\.config",
        StorePath = @"C:\Dev\home\.config",
        PreviousTarget = @"D:\old\.config",
    });

    var reverse = undo.Reverse();
    Check("Undo runs in reverse order (link first)",
        reverse[0].Kind == UndoKind.HomeLink && reverse[0].Target == @"C:\Users\admin\.config",
        reverse[0].Kind + " " + reverse[0].Target);
    Check("Undo reaches PATH before the env vars",
        reverse[1].Kind == UndoKind.Path, reverse[1].Kind.ToString());
    Check("Undo keeps the env vars in reverse write order",
        reverse[2].Target == "PD_NEW" && reverse[3].Target == "HOME",
        reverse[2].Target + "," + reverse[3].Target);
    Check("Undo restores the raw PATH, never an expanded one",
        reverse[1].Value is not null && reverse[1].Value!.Contains("%USERPROFILE%"), reverse[1].Value);
    Check("Undo remembers the PATH registry kind",
        reverse[1].ValueKind == "String", reverse[1].ValueKind);
    Check("Undo remembers the env registry kind",
        reverse[3].ValueKind == "ExpandString", reverse[3].ValueKind);
    Check("Undo deletes an env var that did not exist before", !reverse[2].Existed);
    Check("Undo removes only the link this run created",
        reverse[0].StorePath == @"C:\Dev\home\.config", reverse[0].StorePath);
    Check("Undo retargets a link this run repointed, never deletes it",
        reverse[0].PreviousTarget == @"D:\old\.config", reverse[0].PreviousTarget);
    Check("Undo stack count survives reversal", undo.Count == 4);

    // An empty real directory that became a link must come back as a directory, not vanish.
    var empty = new UndoStep
    {
        Kind = UndoKind.HomeLink,
        Target = @"C:\Users\admin\.portable",
        StorePath = @"C:\Dev\home\.portable",
        WasEmptyDirectory = true,
    };
    Check("Undo recreates a replaced empty directory", empty.WasEmptyDirectory && empty.PreviousTarget is null);
}

// ---- Checksum sidecars: the SHA256 pipeline must never invent a hash ----
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    Eq("Sha256 parses a sha256sum line", Hash,
        Sha256Text.Parse(Hash + "  ripgrep-15.2.0-x86_64-pc-windows-msvc.zip") ?? "(null)");
    Eq("Sha256 parses a bare uppercase hash", Hash,
        Sha256Text.Parse("  " + Hash.ToUpperInvariant() + "\r\n") ?? "(null)");
    Eq("Sha256 parses a BSD-style line", Hash,
        Sha256Text.Parse("SHA256 (x.zip) = " + Hash) ?? "(null)");
    Check("Sha256 rejects a truncated hash", Sha256Text.Parse("0123456789abc") is null);
    Check("Sha256 rejects prose without a hash", Sha256Text.Parse("no checksum here") is null);
    Check("Sha256 rejects empty body", Sha256Text.Parse(null) is null && Sha256Text.Parse("  ") is null);
    Check("Sha256 ignores a 65-digit hex run", Sha256Text.Parse(new string('a', 65)) is null);
    Check("UpdateInfo starts with no checksum",
        new UpdateInfo { ToolId = "x", CurrentVersion = "1", AvailableVersion = "2", DownloadUrl = "u" }.Sha256 is null);
}

// ---- PATH entries on another root, and env conflicts ----
{
    var m = MakeManifest();
    var resolver = new PathResolver(Stable, Stick);

    var onStick = resolver.BuildPathEntries(m, Stick).Select(e => e.Entry).ToList();
    Check("Path entries can be built on the stick root",
        onStick.Count > 0 && onStick.All(e => e.StartsWith(@"E:\PortableDev\tools\", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", onStick));

    var onStable = resolver.BuildPathEntries(m).Select(e => e.Entry).ToList();
    Check("Path entries default to the stable root",
        onStable.All(e => e.StartsWith(@"C:\Dev\tools\", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", onStable));

    // Two declarations of one name used to flip silently with file order.
    var conflict = new RootManifest
    {
        Root = m.Root,
        PathOrder = m.PathOrder,
        Env = new[]
        {
            new EnvVarSpec { Name = "HOME", Value = @"{{ROOT}}\home" },
            new EnvVarSpec { Name = "HOME", Value = @"{{ROOT}}\other" },
        },
        Tools = m.Tools,
    };
    var conflicted = new Planner(conflict, resolver).Build(State());
    Check("Duplicate env with different values warns",
        conflicted.Warnings.Any(w => w.Code == "env-conflict"),
        string.Join(",", conflicted.Warnings.Select(w => w.Code)));
    Check("Duplicate env resolves to the last declaration", conflicted.EnvToSet["HOME"] == @"C:\Dev\other",
        conflicted.EnvToSet["HOME"]);
    Check("Env conflict does not block apply",
        !conflicted.Warnings.Where(w => w.Code == "env-conflict").Any(w => w.Blocker));

    var same = new RootManifest
    {
        Root = m.Root,
        PathOrder = m.PathOrder,
        Env = new[]
        {
            new EnvVarSpec { Name = "HOME", Value = @"{{ROOT}}\home" },
            new EnvVarSpec { Name = "HOME", Value = @"{{ROOT}}\home" },
        },
        Tools = m.Tools,
    };
    Check("Duplicate env with the same value is not a conflict",
        !new Planner(same, resolver).Build(State()).Warnings.Any(w => w.Code == "env-conflict"));
}

// ---- Update downloads: resuming must prove the file is still the same object ----
{
    var probe = new ProbeInfo(1000, true, "\"v1\"", "2026-01-01T00:00:00.0000000+00:00");
    var stored = PartSource.From("https://example.test/a.zip", probe, 2, 500);

    Check("Resume accepts the same object",
        stored.Matches("https://example.test/a.zip", probe, 2, 500));

    var reuploaded = probe with { ETag = "\"v2\"" };
    Check("Resume rejects a re-uploaded asset of the same length",
        !stored.Matches("https://example.test/a.zip", reuploaded, 2, 500));

    Check("Resume rejects a changed length",
        !stored.Matches("https://example.test/a.zip", probe with { Total = 999 }, 2, 500));

    Check("Resume rejects a different url",
        !stored.Matches("https://mirror.test/a.zip", probe, 2, 500));

    Check("Resume rejects a plan that no longer lines up",
        !stored.Matches("https://example.test/a.zip", probe, 4, 250));

    Check("Resume rejects unprovable identity (validator vanished)",
        !stored.Matches("https://example.test/a.zip", new ProbeInfo(1000, true), 2, 500));

    var bare = PartSource.From("https://example.test/a.zip", new ProbeInfo(1000, true), 2, 500);
    Check("Resume without validators falls back to url and length",
        bare.Matches("https://example.test/a.zip", new ProbeInfo(1000, true), 2, 500));

    // ---- Chunk planning: the ranges must cover the file exactly once ----
    const long tenMb = 10L * 1024 * 1024;
    var plan = Downloader.BuildPlan(tenMb, 16);
    Check("Chunk plan honours the connection count", plan.Count == 16, plan.Count.ToString());
    Check("Chunk plan covers every byte exactly once",
        plan[0].Start == 0
        && plan[^1].End == tenMb - 1
        && plan.Zip(plan.Skip(1)).All(p => p.Second.Start == p.First.End + 1)
        && plan.Sum(r => r.Length) == tenMb,
        string.Join(",", plan.Select(r => r.Length)));

    var small = Downloader.BuildPlan(3L * 1024 * 1024, 16);
    Check("Chunk plan never makes a chunk below the minimum", small.Count == 6, small.Count.ToString());
}

// ---- Scoop bucket manifests: map only what an install can honour ----
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    string Json(string body) => body.Replace("__HASH__", Hash);

    var archJson = Json("""
    {
      "version": "15.2.0",
      "architecture": {
        "64bit": {
          "url": "https://example.test/ripgrep-15.2.0-x64.zip",
          "hash": "__HASH__",
          "extract_dir": "ripgrep-15.2.0-x64"
        },
        "32bit": {
          "url": "https://example.test/ripgrep-32.zip",
          "hash": "__HASH__"
        }
      },
      "bin": ["rg.exe"],
      "depends": ["7zip"]
    }
    """);

    var (arch, archError) = ScoopManifest.Parse(archJson, "ripgrep", "Main");
    Check("Scoop: 64bit entry wins", arch is not null && arch.Url.EndsWith("-x64.zip", StringComparison.Ordinal),
        archError);
    Check("Scoop: extract_dir is read", arch?.ExtractDir == "ripgrep-15.2.0-x64", arch?.ExtractDir);
    Check("Scoop: hash is normalised", arch?.Sha256 == Hash);
    Check("Scoop: bin and depends are read",
        arch is not null && arch.Bin.SequenceEqual(new[] { "rg.exe" }) && arch.Depends.SequenceEqual(new[] { "7zip" }));

    var (renamed, renameError) = ScoopManifest.Parse(Json("""
    {"version":"1.0.238","url":"https://example.test/tabby-setup.exe#/dl.7z","hash":"__HASH__","bin":"Tabby.exe"}
    """), "tabby", "Extras");
    Check("Scoop: #/rename fragment becomes RenameTo",
        renamed is not null && renamed.RenameTo == "dl.7z" && !renamed.Url.Contains('#'), renameError);
    Check("Scoop: a single bin string is accepted", renamed?.Bin.SequenceEqual(new[] { "Tabby.exe" }) == true);

    var (sha512, sha512Error) = ScoopManifest.Parse("""
    {"version":"1.0","url":"https://example.test/a.zip","hash":"sha512:da732cb94ce449cc901c1d29b2fd9ab9822070d01675087ba"}
    """, "app", "Extras");
    Check("Scoop: sha512 is refused with the reason",
        sha512 is null && sha512Error?.Contains("sha512", StringComparison.OrdinalIgnoreCase) == true, sha512Error);

    var (noHash, noHashError) = ScoopManifest.Parse("""
    {"version":"1.0","url":"https://example.test/a.zip"}
    """, "app", "Main");
    Check("Scoop: a manifest without hash is refused", noHash is null && noHashError?.Contains("hash") == true, noHashError);

    var (multiUrl, multiError) = ScoopManifest.Parse("""
    {"version":"1.0","url":["https://example.test/a.exe","https://example.test/b.exe"],"hash":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}
    """, "app", "Main");
    Check("Scoop: multi-url manifests are refused", multiUrl is null, multiError);

    var (persisted, persistError) = ScoopManifest.Parse(Json("""
    {"version":"1.0","url":"https://example.test/a.zip","hash":"__HASH__",
     "persist":["config",["data","data-store"]]}
    """), "app", "Main");
    Check("Scoop: persist normalises string and pair forms",
        persisted is not null
        && persisted.Persist.Count == 2
        && persisted.Persist[0].Source == "config" && persisted.Persist[0].Target == "config"
        && persisted.Persist[1].Source == "data" && persisted.Persist[1].Target == "data-store",
        persistError);

    Check("Scoop: bin directory is the PATH entry", GeneratedManifest.DirOf(@"bin\rg.exe") == "bin");
    Check("Scoop: a root-level bin gets '.'", GeneratedManifest.DirOf("rg.exe") == ".");

    var rendered = GeneratedManifest.Render(arch!, new[] { "bin" });
    Check("Scoop: generated manifest names its source",
        rendered.Contains("source  = 'scoop:Main/ripgrep'", StringComparison.Ordinal), rendered);
    Check("Scoop: generated manifest puts bin on PATH",
        rendered.Contains("[[path]]", StringComparison.Ordinal) && rendered.Contains("dir = 'bin'", StringComparison.Ordinal));
    Check("Scoop: generated manifest claims its binaries",
        rendered.Contains("provides = ['rg']", StringComparison.Ordinal), rendered);
}

// ---- Dependency order: dependencies first, cycles refused ----
{
    ScoopPackage Pkg(string id, string bucket, params string[] depends) => new(
        id, bucket, "1.0", "https://example.test/" + id + ".zip",
        new string('a', 64), null, null,
        Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<ToolManifest.PersistSpec>(), depends.ToList(), false,
        Array.Empty<string>(), false);

    var packages = new Dictionary<string, ScoopPackage>(StringComparer.OrdinalIgnoreCase);
    Task<(ScoopPackage? Package, string? Error)> Fetch(string resource, CancellationToken ct) =>
        Task.FromResult(packages.TryGetValue(resource, out var p) ? (p, (string?)null) : ((ScoopPackage?)null, "not found"));

    packages["Main/a"] = Pkg("a", "Main");
    packages["Main/b"] = Pkg("b", "Main", "a");
    packages["Main/c"] = Pkg("c", "Main", "b");

    var (order, errors) = PackageGraph.ResolveAsync(packages["Main/c"], Fetch).GetAwaiter().GetResult();
    Check("Deps: resolved dependencies first",
        errors.Count == 0 && order.Select(p => p.Id).SequenceEqual(new[] { "a", "b", "c" }),
        string.Join(",", order.Select(p => p.Id)) + " | " + string.Join(",", errors));

    packages["Main/loop1"] = Pkg("loop1", "Main", "loop2");
    packages["Main/loop2"] = Pkg("loop2", "Main", "loop1");
    var (_, loopErrors) = PackageGraph.ResolveAsync(packages["Main/loop1"], Fetch).GetAwaiter().GetResult();
    Check("Deps: a cycle is reported", loopErrors.Any(e => e.Contains("循环")), string.Join(";", loopErrors));

    packages["Main/needy"] = Pkg("needy", "Main", "ghost");
    var (_, missing) = PackageGraph.ResolveAsync(packages["Main/needy"], Fetch).GetAwaiter().GetResult();
    Check("Deps: a missing dependency is reported", missing.Any(e => e.Contains("ghost")), string.Join(";", missing));
}

Console.WriteLine();
Console.WriteLine($"==== {passed} passed, {failures} failed ====");
return failures == 0 ? 0 : 1;


