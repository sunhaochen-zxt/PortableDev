using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Core.Packages;
using PortableDev.Core.Update;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>
/// `pkg add|list|remove`: install a package described by a Scoop bucket manifest.
///
/// This borrows Scoop's <em>data</em>, not its runtime: version, url, hash, extract_dir,
/// persist and depends come from the bucket, while PATH, junctions and the machine layer
/// stay PortableDev's business. Shims, shortcuts and installer scripts are deliberately
/// not implemented; installer-script packages are refused instead of half-installed.
/// </summary>
public static class PackageCommand
{
    private const string BinaryRepo = "https://raw.githubusercontent.com/ScoopInstaller/Binary/master";

    public static int Run(CommandOptions opts)
    {
        var args = opts.Positional;
        var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToList();

        return sub switch
        {
            "add" or "install" => Add(opts, rest),
            "list" or "ls" => List(opts),
            "remove" or "rm" or "uninstall" => Remove(opts, rest),
            _ => Usage("unknown pkg command: " + sub),
        };
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  PortableDev pkg add <app> [--bucket Main] [--dry-run] [--refresh]");
        Console.Error.WriteLine("                           [--allow-scripts] [--no-apply]");
        Console.Error.WriteLine("  PortableDev pkg list");
        Console.Error.WriteLine("  PortableDev pkg remove <app> [--purge] [--no-apply]");
        return 2;
    }

    // ------------------------------------------------------------------ add ----

    private static int Add(CommandOptions opts, List<string> args)
    {
        if (args.Count == 0) return Usage("pkg add needs a package name");

        var resolved = Resolve(opts);
        if (resolved is not { } target) return 4;
        var (root, manifest, stable) = target;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("User-Agent", "PortableDev/1.0");
        var source = new ScoopSource(http, root);

        var requested = args[0];
        var resource = opts.Bucket is null ? requested : opts.Bucket + "/" + requested;

        var (rootPackage, error) = source.GetAsync(resource, opts.Refresh, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (rootPackage is null)
        {
            Console.Error.WriteLine("取不到清单：" + error);
            return 4;
        }

        var (order, errors) = PackageGraph.ResolveAsync(rootPackage,
            (res, ct) => source.GetAsync(res, opts.Refresh, ct)).GetAwaiter().GetResult();

        if (errors.Count > 0)
        {
            Console.Error.WriteLine("依赖解析失败：");
            foreach (var e in errors) Console.Error.WriteLine("  " + e);
            return 1;
        }

        // Refuse before downloading anything: an installer-script package cannot be made
        // portable by unpacking, and installing its dependencies alone would be worse.
        var blocked = order.Where(p => p.HasInstaller).ToList();
        if (blocked.Count > 0)
        {
            Console.Error.WriteLine("这些包依赖厂商安装器（installer.script），本工具不支持：");
            foreach (var p in blocked) Console.Error.WriteLine("  " + p.Resource + " " + p.Version);
            return 5;
        }

        if (order.Count > 1)
            Console.WriteLine("依赖顺序：" + string.Join(" → ", order.Select(p => p.Id)));

        // A generated manifest may not take an id a hand-written tool already owns: both
        // live in manifest.d and the loader rejects duplicate ids, which would leave the
        // whole stick unreadable.
        foreach (var package in order)
        {
            if (manifest.Tools.TryGetValue(package.Id, out var existing)
                && existing.Tool.Source?.StartsWith("scoop:", StringComparison.OrdinalIgnoreCase) != true)
            {
                Console.Error.WriteLine($"已有同名的自管工具 '{package.Id}'（版本 {existing.Tool.Version}）；"
                    + "包管理器不能安装同一个名字。");
                return 5;
            }
        }

        foreach (var package in order)
        {
            Console.WriteLine($"  {package.Id,-20} {package.Version,-14} {Describe(package)}");
            if (package.Scripts.Count > 0 && !opts.AllowScripts)
                Console.WriteLine($"      ! 含 {package.Scripts.Count} 段 pre/post_install 脚本，默认不执行");
            if (package.HasEnvSet)
                Console.WriteLine("      ! env_set 不支持，已忽略");
        }

        if (opts.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("dry run：什么都没做。");
            return 0;
        }

        var needsStable = order.Any(p => p.Persist.Count > 0);
        if (needsStable && !Directory.Exists(stable))
        {
            Console.Error.WriteLine("这个包需要 persist 联接，但稳定根 " + stable + " 不存在；先运行 PortableDev apply。");
            return 7;
        }

        if (!opts.Yes && !Confirm())
            return 6;

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in order)
        {
            if (installed.Contains(package.Id)) continue;

            if (AlreadyInstalled(manifest, root, package))
            {
                Console.WriteLine("  = " + package.Id + " " + package.Version + " 已安装");
                installed.Add(package.Id);
                continue;
            }

            if (!InstallOne(root, stable, package, http, opts)) return 1;
            installed.Add(package.Id);
        }

        if (opts.NoApply)
        {
            Console.WriteLine();
            Console.WriteLine("清单已更新；运行 PortableDev apply 让 PATH 生效。");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("应用环境…");
        opts.Confirm();
        return Applier.Run(opts);
    }

    private static bool InstallOne(
        string root, string stable, ScoopPackage package, HttpClient http, CommandOptions opts)
    {
        Console.WriteLine("  + " + package.Id + " " + package.Version);

        var archiveName = package.RenameTo ?? NameOf(package.Url);
        var directory = Path.Combine(root, "downloads", package.Id, package.Version);
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, archiveName);

        if (File.Exists(archive))
        {
            var existing = Downloader.VerifyAsync(archive, package.Sha256).GetAwaiter().GetResult();
            if (existing.Ok)
            {
                Console.WriteLine("      已存在且校验通过，跳过下载");
            }
            else
            {
                Console.WriteLine("      已存在的文件不可信（" + (existing.Error ?? "大小不符") + "），重新下载");
                try { File.Delete(archive); } catch { }
            }
        }

        if (!File.Exists(archive))
        {
            var last = DateTime.UtcNow;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if ((DateTime.UtcNow - last).TotalSeconds < 2) return;
                last = DateTime.UtcNow;
                Console.WriteLine("      " + (p.Received / 1024.0 / 1024).ToString("N1") + " MB"
                    + (p.Total > 0 ? " / " + (p.Total / 1024.0 / 1024).ToString("N1") + " MB" : string.Empty));
            });

            var download = Downloader.DownloadAsync(http, package.Url, archive, 16, progress)
                .GetAwaiter().GetResult();
            if (!download.Ok)
            {
                Console.Error.WriteLine("      下载失败：" + download.Error);
                return false;
            }
        }

        var verify = Downloader.VerifyAsync(archive, package.Sha256).GetAwaiter().GetResult();
        if (!verify.Ok)
        {
            try { File.Delete(archive); } catch { }
            Console.Error.WriteLine("      校验失败，已删除：" + verify.Error);
            return false;
        }
        Console.WriteLine("      SHA256 校验通过（来自 bucket 清单）");

        string? innounp = null;
        if (ToolInstaller.LooksLikeInno(archive))
        {
            innounp = EnsureInnounp(root, http).GetAwaiter().GetResult();
            if (innounp is null)
            {
                Console.Error.WriteLine("      这是 Inno Setup 包，但 innounp.exe 取不到（见日志）");
                return false;
            }
        }

        var result = ToolInstaller.Install(
            root, package.Id, package.Version, archive,
            autoStrip: package.ExtractDir is null,
            extractDir: package.ExtractDir,
            innounpExe: innounp);

        foreach (var line in result.Log) Console.WriteLine("      " + line);
        if (!result.Ok)
        {
            Console.Error.WriteLine("      安装失败：" + result.Message);
            return false;
        }

        foreach (var persist in package.Persist) LinkPersist(root, stable, package, persist);

        var pathDirs = package.Bin
            .Select(GeneratedManifest.DirOf)
            .Concat(package.EnvAddPath.Select(NormalizeDir))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();

        var manifestFile = Path.Combine(root, "manifest", "manifest.d", GeneratedManifest.FileName(package.Id));
        File.WriteAllText(manifestFile, GeneratedManifest.Render(package, pathDirs), new UTF8Encoding(false));
        Console.WriteLine("      清单：" + Path.GetFileName(manifestFile));

        if (package.Scripts.Count > 0 && opts.AllowScripts)
            Console.WriteLine("      ! 脚本未执行：--allow-scripts 仅确认了意图，脚本执行器尚未实现");

        return true;
    }

    /// <summary>
    /// Move whatever the package shipped into tools\&lt;id&gt;\persist and point a junction back
    /// at the stable path, so the data survives both upgrades and a drive-letter change.
    /// </summary>
    private static void LinkPersist(string root, string stable, ScoopPackage package, ToolManifest.PersistSpec spec)
    {
        var relativeSource = spec.Source.Replace('/', '\\').Trim('\\');
        var relativeTarget = spec.Target.Replace('/', '\\').Trim('\\');
        var versionDir = Path.Combine(root, "tools", package.Id, package.Version);
        var source = Path.Combine(versionDir, relativeSource);
        var store = Path.Combine(root, "tools", package.Id, "persist", relativeTarget);
        var target = Path.Combine(stable, "tools", package.Id, "persist", relativeTarget);

        // A name with an extension is a file even before the app has created it: Notepad++
        // ships no config.xml, and treating that name as a directory produced a junction to
        // an empty folder, which the app then could not write through.
        var isFile = File.Exists(source)
            || (!Directory.Exists(source) && Path.GetExtension(relativeSource).Length > 0);

        if (isFile)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(store)!);

                if (File.Exists(source))
                {
                    if (!File.Exists(store)) File.Move(source, store);
                    else File.Delete(source);
                }
                else if (!File.Exists(store))
                {
                    File.WriteAllBytes(store, Array.Empty<byte>());
                }

                var (okFile, errorFile) = HardLink.Create(source, store);
                Console.WriteLine(okFile
                    ? "      persist：" + spec.Source + " → " + target
                    : "      ! persist 硬链接失败：" + errorFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine("      ! persist 文件处理失败：" + ex.Message);
            }
            return;
        }

        if (Directory.Exists(source) && Junction.IsReparsePoint(source))
            Junction.Remove(source);

        Directory.CreateDirectory(store);

        if (Directory.Exists(source))
        {
            foreach (var entry in Directory.GetFileSystemEntries(source))
            {
                var destination = Path.Combine(store, Path.GetFileName(entry));
                if (File.Exists(destination) || Directory.Exists(destination)) continue;

                if (Directory.Exists(entry)) Directory.Move(entry, destination);
                else File.Move(entry, destination);
            }

            try { Directory.Delete(source, true); } catch { }
        }

        var (ok, error) = Junction.Create(source, target);
        Console.WriteLine(ok
            ? "      persist：" + spec.Source + " → " + target
            : "      ! persist 联接失败：" + error);
    }

    private static bool AlreadyInstalled(RootManifest manifest, string root, ScoopPackage package)
    {
        if (!manifest.Tools.TryGetValue(package.Id, out var tool)) return false;
        if (!string.Equals(tool.Tool.Version, package.Version, StringComparison.OrdinalIgnoreCase)) return false;

        return Directory.Exists(Path.Combine(root, "tools", package.Id, package.Version));
    }

    // ----------------------------------------------------------------- list ----

    private static int List(CommandOptions opts)
    {
        var resolved = Resolve(opts);
        if (resolved is not { } target) return 4;
        var (root, manifest, _) = target;

        var tools = manifest.Tools.Values
            .Where(t => t.Tool.Source?.StartsWith("scoop:", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(t => t.Tool.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tools.Count == 0)
        {
            Console.WriteLine("没有通过 pkg add 安装的包。");
            return 0;
        }

        Console.WriteLine($"{"tool",-22}{"version",-16}{"source",-28}state");
        foreach (var tool in tools)
        {
            var directory = Path.Combine(root, "tools", tool.Tool.Id, tool.Tool.Version);
            Console.WriteLine($"{tool.Tool.Id,-22}{tool.Tool.Version,-16}{tool.Tool.Source,-28}"
                + (Directory.Exists(directory) ? "installed" : "missing"));
        }
        return 0;
    }

    // --------------------------------------------------------------- remove ----

    private static int Remove(CommandOptions opts, List<string> args)
    {
        if (args.Count == 0) return Usage("pkg remove needs a package name");

        var resolved = Resolve(opts);
        if (resolved is not { } target) return 4;
        var (root, manifest, _) = target;

        var id = args[0];
        if (!manifest.Tools.TryGetValue(id, out var tool)
            || tool.Tool.Source?.StartsWith("scoop:", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.Error.WriteLine("不是通过 pkg add 安装的包：" + id);
            return 2;
        }

        // Delete exactly the version the generated manifest names. Deleting the whole
        // tools\<id> tree would also take out a hand-written tool that shares the id — and
        // the persist store, which is supposed to survive a reinstall.
        var versionDir = Path.Combine(root, "tools", id, tool.Tool.Version);
        if (Directory.Exists(versionDir))
        {
            try
            {
                FileTree.Delete(versionDir);
                Console.WriteLine("  已删除 " + versionDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("  删不掉 " + versionDir + "：" + ex.Message);
            }
        }

        var baseDir = Path.Combine(root, "tools", id);
        if (opts.Purge)
        {
            var persist = Path.Combine(baseDir, "persist");
            if (Directory.Exists(persist))
            {
                try { FileTree.Delete(persist); Console.WriteLine("  已删除 persist 数据"); }
                catch (Exception ex) { Console.Error.WriteLine("  删不掉 persist：" + ex.Message); }
            }
        }

        // Drop the id folder once nothing but (optionally kept) persist is left in it.
        if (Directory.Exists(baseDir) && !Directory.EnumerateFileSystemEntries(baseDir).Any())
        {
            try { Directory.Delete(baseDir); } catch { }
        }

        var manifestFile = Path.Combine(root, "manifest", "manifest.d", GeneratedManifest.FileName(id));
        if (File.Exists(manifestFile))
        {
            File.Delete(manifestFile);
            Console.WriteLine("  已删除 " + Path.GetFileName(manifestFile));
        }

        if (opts.NoApply) return 0;

        Console.WriteLine();
        Console.WriteLine("应用环境…");
        opts.Confirm();
        return Applier.Run(opts);
    }

    // -------------------------------------------------------------- helpers ----

    private static (string Root, RootManifest Manifest, string Stable)? Resolve(CommandOptions opts)
    {
        var (root, count) = VolumeLocator.Resolve(opts.Root);
        if (root is null)
        {
            Console.Error.WriteLine(count == 0 ? "找不到盘（缺少 .portabledev-root）" : "找到多块盘；用 --root 指定");
            return null;
        }

        var manifest = ManifestLoader.Load(root);
        var stable = opts.NoJunction ? root : manifest.Root.Stable;
        return (root, manifest, stable);
    }

    private static bool Confirm()
    {
        Console.Write("继续？[y/N] ");
        var line = Console.ReadLine()?.Trim();
        return line is not null
            && (line.Equals("y", StringComparison.OrdinalIgnoreCase)
                || line.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(ScoopPackage package)
    {
        var name = package.RenameTo ?? NameOf(package.Url);
        var lower = name.ToLowerInvariant();

        if (lower.EndsWith(".msi", StringComparison.Ordinal)) return "msi";
        if (lower.EndsWith(".7z", StringComparison.Ordinal)) return "7z";
        if (lower.EndsWith(".zip", StringComparison.Ordinal)) return "zip";
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal)) return "tar.gz";
        if (lower.EndsWith(".exe", StringComparison.Ordinal)) return "exe";
        return "?";
    }

    private static string NormalizeDir(string dir) => dir.Replace('/', '\\').Trim();

    private static string NameOf(string url)
    {
        try { return Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath)); }
        catch { var i = url.LastIndexOf('/'); return i < 0 ? "download.bin" : url[(i + 1)..]; }
    }

    /// <summary>
    /// innounp, downloaded on first use of an Inno package. It ships as a .rar inside
    /// Scoop's binary repository, so it needs the full 7z.exe (rar support), not 7zr.
    /// </summary>
    private static async Task<string?> EnsureInnounp(string root, HttpClient http)
    {
        var cacheDir = Path.Combine(root, "cache", "tools", "innounp");
        var cached = Path.Combine(cacheDir, "innounp.exe");
        if (File.Exists(cached)) return cached;

        var toolsDir = Path.Combine(root, "tools", "innounp");
        if (Directory.Exists(toolsDir))
        {
            foreach (var directory in Directory.GetDirectories(toolsDir))
            {
                var candidate = Path.Combine(directory, "innounp.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        var sevenZip = ToolInstaller.FindSevenZip(root);
        if (sevenZip is null || !string.Equals(Path.GetFileName(sevenZip), "7z.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("      需要完整的 7z.exe（rar 支持）才能解开 innounp；盘上只有 7zr。");
            return null;
        }

        Directory.CreateDirectory(cacheDir);
        var rar = Path.Combine(cacheDir, "innounp.rar");
        if (!File.Exists(rar))
        {
            var url = BinaryRepo + "/innounp/innounp.rar";
            Console.WriteLine("      下载 innounp…");
            var download = await Downloader.DownloadAsync(http, url, rar, 4, null);
            if (!download.Ok)
            {
                Console.Error.WriteLine("      innounp 下载失败：" + download.Error);
                return null;
            }
        }

        var log = new List<string>();
        if (!ToolInstaller.Extract(root, rar, cacheDir, log, null, out var error))
        {
            Console.Error.WriteLine("      innounp 解包失败：" + error);
            return null;
        }

        return File.Exists(cached) ? cached : null;
    }
}
