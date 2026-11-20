using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PortableDev.Core.Model;

namespace PortableDev.Core.Packages;

/// <summary>
/// A package described by a Scoop bucket manifest.
///
/// Only the fields an install needs are kept. Scoop's runtime features that this tool
/// deliberately does not implement (shims, shortcuts, installer scripts, env_set) are
/// recorded so the caller can report them instead of silently ignoring them.
/// </summary>
public sealed record ScoopPackage(
    string Id,
    string Bucket,
    string Version,
    string Url,
    string? Sha256,
    string? RenameTo,
    string? ExtractDir,
    IReadOnlyList<string> Bin,
    IReadOnlyList<string> EnvAddPath,
    IReadOnlyList<ToolManifest.PersistSpec> Persist,
    IReadOnlyList<string> Depends,
    bool HasInstaller,
    IReadOnlyList<string> Scripts,
    bool HasEnvSet)
{
    /// <summary>The <c>bucket/app</c> resource this package was read from.</summary>
    public string Resource => Bucket + "/" + Id;

    /// <summary>One-line description from the manifest, for search results.</summary>
    public string? Description { get; init; }

    /// <summary>True when the package is a plain archive this tool can unpack.</summary>
    public bool IsPortable => !HasInstaller;
}

/// <summary>Reads a Scoop bucket manifest into <see cref="ScoopPackage"/>.</summary>
public static class ScoopManifest
{
    /// <summary>
    /// Parse one manifest. The 64-bit architecture is always the one selected: this tool
    /// only ships x64 builds.
    /// </summary>
    public static (ScoopPackage? Package, string? Error) Parse(string json, string id, string bucket)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            return (null, "清单不是合法 JSON：" + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "清单不是 JSON 对象");

            var version = Str(root, "version");
            if (string.IsNullOrWhiteSpace(version)) return (null, "清单缺少 version");

            var arch = EffectiveArch(root);

            var url = Str(arch, "url") ?? Str(root, "url");
            if (string.IsNullOrWhiteSpace(url))
                return (null, "清单没有 url（多 url 或仅 arm64 的清单不支持）");
            if (arch.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.Array)
                return (null, "清单的 url 是数组（多文件安装），不支持");

            // Scoop uses "file.exe#/dl.7z" to rename a download before extraction.
            string? rename = null;
            var hashIdx = url.IndexOf('#');
            if (hashIdx >= 0)
            {
                var fragment = url[(hashIdx + 1)..].TrimStart('/');
                if (fragment.Length > 0) rename = fragment;
                url = url[..hashIdx];
            }

            var (sha256, hashError) = NormalizeHash(Str(arch, "hash") ?? Str(root, "hash"));
            if (hashError is not null) return (null, hashError);

            var installer = arch.TryGetProperty("installer", out var inst) && inst.ValueKind == JsonValueKind.Object
                || root.TryGetProperty("installer", out var inst2) && inst2.ValueKind == JsonValueKind.Object;

            // bin / env_add_path are normally at the manifest root even when url/hash are
            // inside architecture.64bit; read the architecture object first, then the root.
            var bin = ReadStringList(arch, "bin");
            if (bin.Count == 0) bin = ReadStringList(root, "bin");

            var envAddPath = ReadStringList(arch, "env_add_path");
            if (envAddPath.Count == 0) envAddPath = ReadStringList(root, "env_add_path");

            var package = new ScoopPackage(
                id,
                bucket,
                version,
                url,
                sha256,
                rename,
                Str(arch, "extract_dir") ?? Str(root, "extract_dir"),
                bin,
                envAddPath,
                ReadPersist(root),
                ReadStringList(root, "depends"),
                installer,
                ReadScripts(root),
                root.TryGetProperty("env_set", out var es) && es.ValueKind == JsonValueKind.Object)
            {
                Description = Str(root, "description"),
            };

            return (package, null);
        }
    }

    /// <summary>
    /// The object that holds url/hash for x64: <c>architecture.64bit</c> when present,
    /// otherwise the manifest root (the simple single-architecture form).
    /// </summary>
    private static JsonElement EffectiveArch(JsonElement root)
    {
        if (root.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object
            && arch.TryGetProperty("64bit", out var x64) && x64.ValueKind == JsonValueKind.Object)
            return x64;

        return root;
    }

    private static (string? Hash, string? Error) NormalizeHash(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "清单没有 hash；本工具拒绝安装无法校验的包");

        var h = raw.Trim();
        var colon = h.IndexOf(':');
        if (colon > 0)
        {
            var algo = h[..colon].ToLowerInvariant();
            if (algo != "sha256") return (null, $"清单 hash 使用 {algo}，本工具只校验 SHA256");
            h = h[(colon + 1)..];
        }

        if (h.Length != 64 || !h.All(Uri.IsHexDigit))
            return (null, "hash 不是 64 位十六进制（可能是多文件清单）");

        return (h.ToLowerInvariant(), null);
    }

    /// <summary>
    /// A Scoop string-or-array field. <c>bin</c> can also be a list of
    /// <c>[target, alias]</c> pairs; the alias is dropped because this tool has no shims.
    /// </summary>
    public static IReadOnlyList<string> ReadStringList(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return Array.Empty<string>();

        var list = new List<string>();
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                Add(value.GetString());
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) Add(item.GetString());
                    else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0
                             && item[0].ValueKind == JsonValueKind.String)
                        Add(item[0].GetString());
                }
                break;
        }

        return list;

        void Add(string? s)
        {
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
        }
    }

    private static IReadOnlyList<ToolManifest.PersistSpec> ReadPersist(JsonElement root)
    {
        if (!root.TryGetProperty("persist", out var value)) return Array.Empty<ToolManifest.PersistSpec>();

        var list = new List<ToolManifest.PersistSpec>();
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                Add(value.GetString(), null);
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) Add(item.GetString(), null);
                    else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0
                             && item[0].ValueKind == JsonValueKind.String)
                        Add(item[0].GetString(), item.GetArrayLength() > 1 && item[1].ValueKind == JsonValueKind.String
                            ? item[1].GetString()
                            : null);
                }
                break;
        }

        return list;

        void Add(string? source, string? target)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            list.Add(new ToolManifest.PersistSpec
            {
                Source = source!.Trim(),
                Target = (string.IsNullOrWhiteSpace(target) ? source : target)!.Trim(),
            });
        }
    }

    private static IReadOnlyList<string> ReadScripts(JsonElement root)
    {
        var list = new List<string>();
        foreach (var key in new[] { "pre_install", "post_install" })
        {
            if (!root.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) list.Add(value.GetString() ?? string.Empty);
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? string.Empty);
        }
        return list;
    }

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
