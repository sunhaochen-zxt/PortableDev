using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PortableDev.Core;

namespace PortableDev.Gui.Engine;

/// <summary>Reads and writes manifest.d\*.toml without ever leaving a broken file behind.</summary>
public static class ManifestEditor
{
    public static string ToolDir(string stickRoot) => Path.Combine(stickRoot, "manifest", "manifest.d");

    public static string PathFor(string stickRoot, string toolId) =>
        Path.Combine(ToolDir(stickRoot), toolId + ".toml");

    public static bool Exists(string stickRoot, string toolId) => File.Exists(PathFor(stickRoot, toolId));

    public static string Read(string stickRoot, string toolId)
    {
        var p = PathFor(stickRoot, toolId);
        return File.Exists(p) ? File.ReadAllText(p) : string.Empty;
    }

    /// <summary>Render a new tool manifest. Only the fields the GUI asks for.</summary>
    public static string BuildToolToml(NewToolSpec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("schema = 1");
        sb.AppendLine();
        sb.AppendLine("[tool]");
        sb.AppendLine("id      = " + Quote(spec.Id));
        sb.AppendLine("version = " + Quote(spec.Version));
        sb.AppendLine("title   = " + Quote(string.IsNullOrWhiteSpace(spec.Title) ? spec.Id : spec.Title));
        sb.AppendLine("kind    = " + Quote(spec.Kind));
        sb.AppendLine("dir     = " + Quote(spec.Dir.Replace('\\', '/')));
        if (!string.IsNullOrWhiteSpace(spec.Notes))
            sb.AppendLine("notes   = " + Quote(spec.Notes));
        if (!spec.Enabled)
            sb.AppendLine("enabled = false");

        foreach (var p in spec.PathDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            sb.AppendLine();
            sb.AppendLine("[[path]]");
            sb.AppendLine("dir = " + Quote(p.Trim()));
        }

        foreach (var v in spec.Verifies.Where(v => !string.IsNullOrWhiteSpace(v.Run)))
        {
            sb.AppendLine();
            sb.AppendLine("[[verify]]");
            sb.AppendLine("run    = " + Quote(v.Run.Trim()));
            if (!string.IsNullOrWhiteSpace(v.Expect))
                sb.AppendLine("expect = " + Quote(v.Expect.Trim()));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Repoint a manifest after an upgrade.
    ///
    /// Changing only "version" is not enough and leaves a manifest that lies:
    /// the verify expectation still names the old version (so every check turns
    /// red), and archive/sha256 still describe the old package. All of them are
    /// updated here in one pass, line by line, so comments survive.
    ///
    /// A manifest that pins [tool].dir (vscode, sublime-text, powershell …) must have that
    /// line moved too. Leaving it behind installs the new payload next to the old one and then
    /// keeps verifying, and putting on PATH, the version that was just replaced.
    ///
    /// Returns null when the [tool] table has no version line.
    /// </summary>
    public static string? Retarget(
        string text, string oldVersion, string newVersion,
        string? archiveRelPath, string? sha256, string? dirRelPath = null)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var inTool = false;
        var versionSeen = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                inTool = trimmed.Equals("[tool]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inTool)
            {
                if (Regex.IsMatch(trimmed, @"^version\s*=", RegexOptions.IgnoreCase))
                {
                    lines[i] = "version = " + Quote(newVersion);
                    versionSeen = true;
                    continue;
                }

                if (archiveRelPath is not null && Regex.IsMatch(trimmed, @"^archive\s*=", RegexOptions.IgnoreCase))
                {
                    lines[i] = "archive = " + Quote(archiveRelPath);
                    continue;
                }

                if (sha256 is not null && Regex.IsMatch(trimmed, @"^sha256\s*=", RegexOptions.IgnoreCase))
                {
                    lines[i] = "sha256  = " + Quote(sha256);
                    continue;
                }

                if (dirRelPath is not null && Regex.IsMatch(trimmed, @"^dir\s*=", RegexOptions.IgnoreCase))
                {
                    lines[i] = "dir     = " + Quote(dirRelPath.Replace('\\', '/'));
                    continue;
                }
            }

            // Expectation strings that name the old version would fail the moment the
            // new binary is in place, so they follow the upgrade.
            if (Regex.IsMatch(trimmed, @"^expect(_any)?\s*=", RegexOptions.IgnoreCase)
                && !string.IsNullOrEmpty(oldVersion)
                && lines[i].Contains(oldVersion, StringComparison.Ordinal))
            {
                lines[i] = ReplaceVersionToken(lines[i], oldVersion, newVersion);
            }
        }

        return versionSeen ? string.Join(Environment.NewLine, lines) : null;
    }

    /// <summary>
    /// Replace a version inside an expectation string only when it stands alone.
    ///
    /// A plain string.Replace is a corruption bug: upgrading "7.6" to "7.7" would turn
    /// <c>expect = 'PowerShell 7.6.8'</c> into <c>'PowerShell 7.7.8'</c>, a version that
    /// never existed, and verify would then fail against a number nobody wrote. Leaving
    /// such a line untouched is the honest outcome — the check goes red and a human
    /// decides, instead of the file quietly growing a lie.
    /// </summary>
    internal static string ReplaceVersionToken(string line, string oldVersion, string newVersion)
    {
        // Only a longer *version* must block the replacement, i.e. a digit or a dot on
        // either side. Excluding letters as well silently skipped the real manifests:
        // nodejs expects 'v26.12.1' and ripgrep 'ripgrep 15.2.0', so a version that hugs
        // a letter was never rewritten and the upgraded tool failed its own self-check.
        var pattern = @"(?<![0-9.])" + Regex.Escape(oldVersion) + @"(?![0-9.])";
        return Regex.Replace(line, pattern, newVersion.Replace("$", "$$"));
    }

    /// <summary>
    /// Replace the version inside the [tool] table, leaving comments and every other
    /// key byte-identical. Returns null when no such line exists.
    /// </summary>
    public static string? SetVersion(string text, string newVersion)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var inTool = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("["))
            {
                inTool = t.Equals("[tool]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inTool) continue;
            if (!Regex.IsMatch(t, @"^version\s*=", RegexOptions.IgnoreCase)) continue;

            lines[i] = "version = " + Quote(newVersion);
            return string.Join(Environment.NewLine, lines);
        }
        return null;
    }

    /// <summary>
    /// Write the file and prove the whole manifest still loads. On any error the
    /// previous content is restored, so the stick can never be left unusable.
    /// </summary>
    public static (bool Ok, IReadOnlyList<string> Errors) TryWrite(
        string stickRoot, string toolId, string newText)
    {
        var path = PathFor(stickRoot, toolId);
        var existed = File.Exists(path);
        var backup = existed ? File.ReadAllText(path) : null;

        try
        {
            Directory.CreateDirectory(ToolDir(stickRoot));
            File.WriteAllText(path, newText, new UTF8Encoding(false));

            // Full reload: catches syntax errors and duplicate ids in one shot.
            ManifestLoader.Load(stickRoot);
            return (true, Array.Empty<string>());
        }
        catch (ManifestException ex)
        {
            var mine = ex.Errors.Where(e => e.Contains(toolId + ".toml", StringComparison.OrdinalIgnoreCase)
                                         || e.Contains("manifest.d", StringComparison.OrdinalIgnoreCase))
                                .ToList();
            Restore(path, existed, backup);
            return (false, mine.Count > 0 ? mine : ex.Errors);
        }
        catch (Exception ex)
        {
            Restore(path, existed, backup);
            return (false, new[] { ex.Message });
        }
    }

    private static void Restore(string path, bool existed, string? backup)
    {
        try
        {
            if (existed && backup is not null) File.WriteAllText(path, backup, new UTF8Encoding(false));
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // nothing more we can do; the caller already has the error text
        }
    }

    public static void Delete(string stickRoot, string toolId)
    {
        var p = PathFor(stickRoot, toolId);
        if (File.Exists(p)) File.Delete(p);
    }

    private static string Quote(string s) =>
        "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

public sealed class NewToolSpec
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = "prebuilt";
    public string Dir { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> PathDirs { get; } = new();
    public List<VerifyRow> Verifies { get; } = new();
}

public sealed class VerifyRow
{
    public string Run { get; set; } = string.Empty;
    public string Expect { get; set; } = string.Empty;
}
