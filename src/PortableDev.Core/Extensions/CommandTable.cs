using PortableDev.Core.Extensions;

namespace PortableDev.Core.Extensions;

/// <summary>
/// The single command table (architecture 3.8). Names and aliases share one namespace; conflicts are
/// recorded instead of silently overwritten.
/// </summary>
public sealed class CommandTable
{
    private readonly Dictionary<string, CommandEntry> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandEntry> _byAlias = new(StringComparer.Ordinal);
    private readonly List<CommandConflict> _conflicts = [];

    public IReadOnlyCollection<CommandEntry> All => _byName.Values;

    public IReadOnlyList<CommandConflict> Conflicts => _conflicts;

    public bool TryResolve(string name, out CommandEntry entry)
    {
        if (_byName.TryGetValue(name, out entry!))
        {
            return true;
        }

        if (_byAlias.TryGetValue(name, out entry!))
        {
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>Registers a command; returns the conflicting name when it cannot be added.</summary>
    public string? TryRegister(CommandEntry entry)
    {
        if (_byName.ContainsKey(entry.Name))
        {
            _conflicts.Add(new CommandConflict(entry.Name, _byName[entry.Name].ExtensionName, entry.ExtensionName));
            return entry.Name;
        }

        if (_byAlias.TryGetValue(entry.Name, out var aliasOwner))
        {
            _conflicts.Add(new CommandConflict(entry.Name, aliasOwner.ExtensionName, entry.ExtensionName));
            return entry.Name;
        }

        foreach (var alias in entry.Aliases)
        {
            if (_byName.TryGetValue(alias, out var existing) || _byAlias.TryGetValue(alias, out existing))
            {
                _conflicts.Add(new CommandConflict(alias, existing?.ExtensionName, entry.ExtensionName));
                return alias;
            }
        }

        _byName[entry.Name] = entry;
        foreach (var alias in entry.Aliases)
        {
            _byAlias[alias] = entry;
        }

        return null;
    }

    public void Unregister(string extensionName)
    {
        foreach (var entry in _byName.Values.Where(e => e.ExtensionName == extensionName).ToArray())
        {
            _byName.Remove(entry.Name);
            foreach (var alias in entry.Aliases)
            {
                _byAlias.Remove(alias);
            }
        }
    }

    /// <summary>Cheap suggestion used by `ERR_UNKNOWN_COMMAND` (commands.md 4.2 note).</summary>
    public string? Suggest(string input)
    {
        var best = (Distance: int.MaxValue, Name: (string?)null);
        foreach (var name in _byName.Keys.Concat(_byAlias.Keys))
        {
            var distance = Levenshtein(input, name);
            if (distance < best.Distance)
            {
                best = (distance, name);
            }
        }

        return best.Name is not null && best.Distance <= Math.Max(2, input.Length / 2) ? best.Name : null;
    }

    public static int Levenshtein(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

public sealed record CommandEntry
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public string? ExtensionName { get; init; }

    public bool IsBuiltin { get; init; }

    public bool Mutates { get; init; }

    public bool Parallel { get; init; }

    public string? Summary { get; init; }

    public string? Usage { get; init; }

    public string? Group { get; init; }

    public bool Hidden { get; init; }

    public bool Deprecated { get; init; }
}

public sealed record CommandConflict(string Name, string? KeptBy, string? RejectedBy);
