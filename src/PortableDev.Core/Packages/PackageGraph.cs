using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PortableDev.Core.Packages;

/// <summary>
/// Orders a package and everything it depends on, dependencies first.
///
/// Scoop dependencies are plain names ("7zip") or bucket-qualified
/// ("extras/vcredist2022"); a bare name is resolved against the parent's bucket first.
/// </summary>
public static class PackageGraph
{
    public const int MaxDepth = 8;

    public static async Task<(List<ScoopPackage> Order, List<string> Errors)> ResolveAsync(
        ScoopPackage root,
        Func<string, CancellationToken, Task<(ScoopPackage? Package, string? Error)>> fetch,
        CancellationToken ct = default)
    {
        var order = new List<ScoopPackage>();
        var errors = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = visiting, 2 = done
        var path = new List<string>();

        await VisitAsync(root, 0);

        return (order, errors);

        async Task VisitAsync(ScoopPackage package, int depth)
        {
            var key = package.Resource;

            if (state.TryGetValue(key, out var current))
            {
                if (current == 2) return; // already placed
                errors.Add("循环依赖：" + string.Join(" → ", path) + " → " + key);
                return;
            }

            if (depth > MaxDepth)
            {
                errors.Add($"依赖链超过 {MaxDepth} 层：{key}");
                return;
            }

            state[key] = 1;
            path.Add(key);

            foreach (var dependency in package.Depends)
            {
                var resource = dependency.Contains('/') ? dependency : package.Bucket + "/" + dependency;
                var (dependencyPackage, error) = await fetch(resource, ct);

                if (dependencyPackage is null)
                {
                    errors.Add($"{key} 依赖 {resource} 取不到：{error}");
                    continue;
                }

                await VisitAsync(dependencyPackage, depth + 1);
            }

            path.RemoveAt(path.Count - 1);
            state[key] = 2;
            order.Add(package);
        }
    }
}
