using System.Text.Json;
using PortableDev.Core;
using PortableDev.Core.Model;

namespace PortableDev.App;

/// <summary>Renders a Plan as either human-readable text or JSON.</summary>
public static class PlanRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void WriteText(Plan plan, TextWriter o)
    {
        o.WriteLine($"stable root : {plan.StableRoot}");
        o.WriteLine($"stick root  : {plan.StickRoot}");
        o.WriteLine($"mode        : {(plan.Degraded ? "degraded (no junction)" : "normal")}");
        o.WriteLine($"elevation   : {(plan.NeedsElevation ? "1 UAC prompt (creates the root junction)" : "not needed")}");
        o.WriteLine($"tx          : {plan.TxId}");
        o.WriteLine();

        var groups = new (string Kind, string Label)[]
        {
            ("junction", "Root junction"),
            ("home", "Home links (settings that follow the stick)"),
            ("path", "PATH"),
            ("env", "Environment"),
            ("launcher", "Launchers (generated .cmd at the stick root)"),
            ("tool", "Tools"),
        };

        foreach (var (kind, label) in groups)
        {
            var steps = plan.Steps.Where(s => s.Kind == kind).ToList();
            if (steps.Count == 0) continue;

            var changed = steps.Where(s => s.Change != ChangeKind.None).ToList();
            o.WriteLine($"{label} ({changed.Count} change(s))");

            foreach (var s in steps)
            {
                var mark = s.Change switch
                {
                    ChangeKind.Add => "+",
                    ChangeKind.Update => "~",
                    ChangeKind.Remove => "-",
                    _ => " ",
                };

                switch (kind)
                {
                    case "path":
                        o.WriteLine(s.Change == ChangeKind.None
                            ? "    (unchanged)"
                            : $"  {mark} {s.Target}");
                        break;

                    case "env":
                        o.WriteLine(s.Change switch
                        {
                            ChangeKind.Add => $"  + {s.Target} = {s.After}",
                            ChangeKind.Update => $"  ~ {s.Target}  ({s.Before} -> {s.After})",
                            ChangeKind.Remove => $"  - {s.Target}",
                            _ => $"    {s.Target} = {s.After}",
                        });
                        break;

                    case "junction":
                        o.WriteLine(s.Change switch
                        {
                            ChangeKind.Add => $"  + create {s.Target} -> {s.After}",
                            ChangeKind.Update => $"  ~ repoint {s.Target}: {s.Before} -> {s.After}",
                            _ => $"    {s.Target} -> {s.After} (ok)",
                        });
                        break;

                    case "home":
                        o.WriteLine(s.Change switch
                        {
                            ChangeKind.Add => $"  + link   {s.Target}: {s.After}",
                            ChangeKind.Update => $"  ~ relink {s.Target}: {s.Before} -> {s.After}",
                            _ => $"    {s.Target}  {s.After}",
                        });
                        break;

                    case "launcher":
                        o.WriteLine(s.Change switch
                        {
                            ChangeKind.Add => $"  + write  {s.Target}",
                            ChangeKind.Update => $"  ~ rewrite {s.Target}  (content differs from the manifest)",
                            _ => $"    {s.Target}  (up to date)",
                        });
                        break;

                    default:
                        o.WriteLine($"  {mark} {s.Target}  {s.After}");
                        break;
                }
            }
            o.WriteLine();
        }

        if (plan.Warnings.Count > 0)
        {
            o.WriteLine($"Warnings ({plan.Warnings.Count})");
            foreach (var w in plan.Warnings)
                o.WriteLine($"  [{(w.Blocker ? "BLOCK" : "warn")}] {w.Code}: {w.Message}");
            o.WriteLine();
        }

        o.WriteLine(plan.HasChanges
            ? "=> changes pending. Run 'apply' to execute."
            : "=> nothing to do; machine already matches the manifest.");
    }

    public static void WriteJson(Plan plan, TextWriter o)
    {
        var dto = new
        {
            txId = plan.TxId,
            stableRoot = plan.StableRoot,
            stickRoot = plan.StickRoot,
            degraded = plan.Degraded,
            needsElevation = plan.NeedsElevation,
            hasChanges = plan.HasChanges,
            newUserPath = plan.NewUserPathRaw,
            env = plan.EnvToSet,
            steps = plan.Steps.Select(s => new
            {
                kind = s.Kind,
                change = s.Change.ToString().ToLowerInvariant(),
                target = s.Target,
                before = s.Before,
                after = s.After,
            }),
            warnings = plan.Warnings.Select(w => new { code = w.Code, message = w.Message, blocker = w.Blocker }),
        };

        o.WriteLine(JsonSerializer.Serialize(dto, JsonOptions));
    }
}
