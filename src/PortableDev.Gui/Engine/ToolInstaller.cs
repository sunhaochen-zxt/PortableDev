using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PortableDev.Gui.Engine;

public sealed class InstallResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public string TargetDir { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public List<string> Log { get; } = new();
}

/// <summary>
/// Unpacks a tool archive into tools\&lt;id&gt;\&lt;version&gt;.
///
/// zip and tar.gz are handled by the .NET base library on purpose. The 7z.exe that
/// ships next to this app is the *reduced* 7zr build (identical size, 7z format
/// only), so delegating zip to it fails with "Cannot open the file as archive".
/// Only genuine 7z archives (and self-extracting ones) fall back to the external
/// binary.
///
/// This only ever writes inside the stick, and only into a directory that does not
/// exist yet, so a failed install can be retried by deleting the partial folder.
/// It never touches PATH or the registry: those stay the exclusive business of
/// apply, so an install cannot half-modify the machine.
/// </summary>
public static class ToolInstaller
{
    public static InstallResult Install(string stickRoot, string toolId, string version,
        string archivePath, bool autoStrip)
    {
        var target = Path.Combine(stickRoot, "tools", toolId, version);
        var result = new InstallResult { Ok = false, TargetDir = target };

        if (!File.Exists(archivePath))
        {
            result.Log.Add("找不到压缩包：" + archivePath);
            return result;
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            result.Log.Add("目标已存在且非空：" + target);
            result.Log.Add("如果要重装，请先手动删除该目录。");
            return result;
        }

        var tempRoot = Path.Combine(stickRoot, "state", "tmp", "install-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            result.Log.Add("解压到临时目录…");

            if (!Extract(stickRoot, archivePath, tempRoot, result.Log, out var err))
            {
                result.Log.Add("解压失败：" + err);
                return result;
            }

            var source = tempRoot;
            if (autoStrip)
            {
                var single = SingleRootDir(tempRoot);
                if (single is not null)
                {
                    source = single;
                    result.Log.Add("剥离顶层目录：" + Path.GetFileName(single));
                }
                else
                {
                    result.Log.Add("顶层不止一个目录，未剥离。");
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target)) Directory.Delete(target, true);

            if (string.Equals(source, tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(target);
                foreach (var entry in Directory.EnumerateFileSystemEntries(tempRoot))
                    MoveEntry(entry, Path.Combine(target, Path.GetFileName(entry)));
            }
            else
            {
                Directory.Move(source, target);
            }

            var count = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count();
            result.Log.Add("已安装 " + count + " 个文件到 " + target);
            result.Log.Add("PATH 尚未改变：回到工具页按“应用”或运行 apply 才会生效。");

            var done = new InstallResult
            {
                Ok = true,
                TargetDir = target,
                FileCount = count,
                Message = "安装完成，共 " + count + " 个文件",
            };
            done.Log.AddRange(result.Log);
            return done;
        }
        catch (Exception ex)
        {
            result.Log.Add("出错：" + ex.Message);
            return result;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>True when the temp tree holds exactly one directory and no files.</summary>
    private static string? SingleRootDir(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length != 1) return null;
        return Directory.Exists(entries[0]) ? entries[0] : null;
    }

    /// <summary>
    /// What the file actually is. The name is not evidence: an update URL such as
    /// <c>https://update.code.visualstudio.com/1.144.0/win32-x64-archive/stable</c> has no
    /// file name at all, so the download lands as "stable" with no extension. Dispatching on
    /// the extension then sent a zip to 7zr (7z format only), which answered "Cannot open
    /// the file as archive". Read the first bytes instead.
    /// </summary>
    private enum ArchiveKind { Unknown, Zip, Gzip, SevenZip, Cab, Xz }

    private static ArchiveKind Detect(string archive)
    {
        try
        {
            using var fs = File.OpenRead(archive);
            Span<byte> head = stackalloc byte[8];
            var read = fs.Read(head);
            if (read >= 4 && head[0] == 0x50 && head[1] == 0x4B
                && ((head[2] == 0x03 && head[3] == 0x04)   // normal entry
                 || (head[2] == 0x05 && head[3] == 0x06)   // empty archive
                 || (head[2] == 0x07 && head[3] == 0x08))) // spanned
                return ArchiveKind.Zip;
            if (read >= 2 && head[0] == 0x1F && head[1] == 0x8B)
                return ArchiveKind.Gzip;
            if (read >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC
                && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
                return ArchiveKind.SevenZip;
            if (read >= 4 && head[0] == 0x4D && head[1] == 0x53 && head[2] == 0x43 && head[3] == 0x46)
                return ArchiveKind.Cab;
            if (read >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A
                && head[3] == 0x58 && head[4] == 0x5A && head[5] == 0x00)
                return ArchiveKind.Xz;
        }
        catch
        {
            // Unreadable file: fall through, Extract reports the real error.
        }

        return ArchiveKind.Unknown;
    }

    private static bool Extract(string stickRoot, string archive, string dest, List<string> log, out string error)
    {
        error = string.Empty;
        var lower = archive.ToLowerInvariant();
        var kind = Detect(archive);

        try
        {
            if (kind == ArchiveKind.Zip
                || (kind == ArchiveKind.Unknown && lower.EndsWith(".zip", StringComparison.Ordinal)))
            {
                log.Add("使用内置 zip 解压");
                ZipFile.ExtractToDirectory(archive, dest, overwriteFiles: true);
                return true;
            }

            if (kind == ArchiveKind.Gzip
                || (kind == ArchiveKind.Unknown
                    && (lower.EndsWith(".tar.gz", StringComparison.Ordinal)
                        || lower.EndsWith(".tgz", StringComparison.Ordinal))))
            {
                log.Add("使用内置 tar.gz 解压");
                using var file = File.OpenRead(archive);
                using var gz = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, dest, overwriteFiles: true);
                return true;
            }

            if (kind == ArchiveKind.Cab)
            {
                error = "不支持的格式：cab（Windows Installer 包）。请用 msiexec /a 或换成官方 zip 包。";
                return false;
            }

            if (kind == ArchiveKind.Xz)
            {
                error = "不支持的格式：xz。目前只处理 zip、tar.gz 和 7z。";
                return false;
            }

            var sevenZip = FindSevenZip(stickRoot);
            if (sevenZip is null)
            {
                error = "无法识别这个压缩包的格式，且盘上没有 tools\\7zip\\<版本>\\7z.exe 可以尝试："
                    + Path.GetFileName(archive);
                return false;
            }

            log.Add("使用 " + Path.GetFileName(sevenZip) + " 解压");
            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add(archive);
            psi.ArgumentList.Add("-o" + dest);
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-bso0");
            psi.ArgumentList.Add("-bsp0");

            using var p = Process.Start(psi);
            if (p is null) { error = "无法启动 7z"; return false; }

            // Start draining both pipes before waiting. Reading one synchronously to the
            // end and only then the other deadlocks as soon as the other pipe fills up.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(600_000))
            {
                try { p.Kill(true); } catch { }
                error = "解压超时";
                return false;
            }

            var stderrText = stderr.GetAwaiter().GetResult();
            _ = stdout.GetAwaiter().GetResult();

            if (p.ExitCode != 0)
            {
                error = "7z 退出码 " + p.ExitCode + " " + stderrText.Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Locate the 7-Zip binary on the stick, best first. 7zr is the reduced build that only
    /// understands the 7z format, so it must never be the first choice when a multi-format
    /// 7za/7z happens to be installed too.
    /// </summary>
    private static string? FindSevenZip(string stickRoot)
    {
        var baseDir = Path.Combine(stickRoot, "tools", "7zip");
        if (!Directory.Exists(baseDir)) return null;

        foreach (var dir in Directory.GetDirectories(baseDir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var n in new[] { "7z.exe", "7za.exe", "7zr.exe" })
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
