using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PortableDev.Core.Model;
using PortableDev.Core.Packages;
using PortableDev.Core.Update;
using PortableDev.Gui.ViewModels;

namespace PortableDev.Gui.Engine;

/// <summary>
/// Download one update, put it in place next to the old version, and repoint the manifest.
///
/// Order matters: the payload lands in a NEW version directory, so a failed download
/// or a corrupt archive leaves the working toolchain untouched. Only afterwards is the
/// manifest rewritten (version, archive, sha256 and version-bearing expectations in
/// one pass), and that write is validated by a full manifest reload with rollback.
///
/// Two shapes are supported, and the manifest decides which:
///   kind = 'raw'  -> the download IS the program (jq.exe, 7zr.exe): copy + rename
///   anything else -> the download is an archive: extract, honouring [tool].strip
/// </summary>
public static class Upgrader
{
    public static async Task<InstallResult> RunAsync(
        string stickRoot,
        ToolManifest tool,
        UpdateInfo info,
        Action<string> log,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var toolId = tool.Tool.Id;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.Add("User-Agent", "PortableDev/1.0");

        var archiveName = FileNameOf(info.DownloadUrl);
        if (archiveName.Length == 0)
            return Fail(log, "下载地址里没有文件名：" + info.DownloadUrl);

        // Some update endpoints carry no file name in the path — the VS Code one is
        // ".../<version>/win32-x64-archive/stable" — so the last segment is a word like
        // "stable" and the download ends up with no extension. The real name is only in the
        // redirect target / Content-Disposition. Ask for it up front: one HEAD next to a
        // 350 MB GET, and every later step that looks at the name ("is this a zip?") and
        // every human looking at downloads\ sees the actual package.
        if (Path.GetExtension(archiveName).Length == 0)
        {
            var remoteName = await RemoteNameAsync(http, info.DownloadUrl, ct);
            if (remoteName.Length > 0 && !string.Equals(remoteName, archiveName, StringComparison.Ordinal))
            {
                log("下载文件名取自重定向：" + remoteName);
                archiveName = remoteName;
            }
        }

        var downloadDir = Path.Combine(stickRoot, "downloads", toolId, info.AvailableVersion);
        var archivePath = Path.Combine(downloadDir, archiveName);
        Directory.CreateDirectory(downloadDir);

        // The published checksum is resolved here, i.e. only when a download is really
        // going to be used. Without this the value was always null and the "corrupt
        // download cannot damage a working tool" promise never held above the unpack step.
        var expectedSha = string.IsNullOrWhiteSpace(info.Sha256)
            ? await new UpdateChecker(http).ResolveSha256Async(tool, info, ct)
            : info.Sha256;

        // ---- 1. download ----
        if (File.Exists(archivePath) && new FileInfo(archivePath).Length > 0)
        {
            var existing = await Downloader.VerifyAsync(archivePath, expectedSha, ct);
            var sizeOk = info.SizeBytes is not > 0 || new FileInfo(archivePath).Length == info.SizeBytes.Value;
            if (existing.Ok && sizeOk)
            {
                log("压缩包已存在且校验通过，跳过下载：" + archivePath);
            }
            else
            {
                log("已存在的压缩包不可信（" + (existing.Error ?? "大小不符") + "），重新下载");
                try { File.Delete(archivePath); } catch { }
            }
        }

        if (!File.Exists(archivePath))
        {
            log("下载 " + info.DownloadUrl + (info.SizeBytes is > 0
                ? "  (" + (info.SizeBytes.Value / 1024.0 / 1024).ToString("N1") + " MB)"
                : string.Empty));

            var dl = await Downloader.DownloadAsync(http, info.DownloadUrl, archivePath, 16, progress, ct);
            if (!dl.Ok) return Fail(log, "下载失败：" + dl.Error);
        }

        var bytes = new FileInfo(archivePath).Length;
        log("已下载 " + (bytes / 1024.0 / 1024).ToString("N1") + " MB");

        // ---- 2. verify what we got ----
        var verified = await Downloader.VerifyAsync(archivePath, expectedSha, ct);
        if (!verified.Ok)
        {
            try { File.Delete(archivePath); } catch { }
            return Fail(log, "校验失败，已删除压缩包：" + verified.Error);
        }

        var actualSha = verified.Actual;
        if (string.IsNullOrWhiteSpace(expectedSha))
            log("该源没有提供校验值；已记录下载文件的 SHA256 供以后核对：" + actualSha);
        else
            log("SHA256 校验通过");

        // ---- 3. install ----
        int fileCount;
        string targetDir;

        if (string.Equals(tool.Tool.Kind, "raw", StringComparison.OrdinalIgnoreCase))
        {
            targetDir = Path.Combine(stickRoot, "tools", toolId, info.AvailableVersion);
            Directory.CreateDirectory(targetDir);

            var fileName = string.IsNullOrWhiteSpace(tool.Tool.Rename) ? archiveName : tool.Tool.Rename!;
            var destFile = Path.Combine(targetDir, fileName);

            log("该工具是单文件（kind=raw），放置为 " + fileName);
            if (File.Exists(destFile)) File.Delete(destFile);
            File.Copy(archivePath, destFile, overwrite: true);
            log("已放入 " + destFile);
            fileCount = 1;
        }
        else
        {
            log("正在解压到 tools\\" + toolId + "\\" + info.AvailableVersion + " …（大包需要几十秒）");

            // Extraction is blocking file I/O over tens of thousands of small files and
            // is slow on exFAT. Running it on the UI thread freezes the window, which
            // Windows then paints as "not responding".
            var strip = tool.Tool.Strip != 0;
            var install = await Task.Run(
                () => ToolInstaller.Install(stickRoot, toolId, info.AvailableVersion, archivePath, strip), ct);

            foreach (var line in install.Log) log(line);
            if (!install.Ok) return install;

            fileCount = install.FileCount;
            targetDir = install.TargetDir;
        }

        // ---- 4. repoint the manifest ----
        var dirRel = Path.GetRelativePath(stickRoot, targetDir).Replace('\\', '/');
        var oldVersion = string.IsNullOrWhiteSpace(tool.Tool.Version)
            ? info.CurrentVersion
            : tool.Tool.Version;
        var oldDirRel = string.IsNullOrWhiteSpace(tool.Tool.Dir)
            ? "tools/" + toolId + "/" + oldVersion
            : tool.Tool.Dir!.Replace('\\', '/');

        log("更新清单 -> " + info.AvailableVersion + "（version、dir、校验期望，以及清单里已有的 archive/sha256 行）");

        var text = ManifestEditor.Read(stickRoot, toolId);
        var archiveRel = Path.GetRelativePath(stickRoot, archivePath).Replace('\\', '/');
        var updated = ManifestEditor.Retarget(
            text, oldVersion, info.AvailableVersion, archiveRel, actualSha, dirRel);

        if (updated is null)
        {
            // Writing PATH from a manifest that still says "old version, old dir" would put
            // the replaced copy back on PATH. Stop here and let a human edit the file.
            log("清单里没有 version 行，无法自动改写。新版本已下载到 " + dirRel + "，请手动修改 " + toolId + ".toml。");
            return new InstallResult { Ok = false, TargetDir = targetDir, FileCount = fileCount, Message = "清单格式不认识，升级中止" };
        }

        var write = ManifestEditor.TryWrite(stickRoot, toolId, updated);
        if (!write.Ok)
        {
            log("写清单失败：" + string.Join("; ", write.Errors));
            return new InstallResult { Ok = false, Message = "写清单失败" };
        }

        // ---- 5. self-check the new copy ----
        // verify prepends the tool dir to PATH itself, so it works before "apply" runs.
        log("自检（verify）…");
        var check = await CliBridge.RunAsync(stickRoot, new[] { "verify", "--json" }, null, ct);
        var mine = VerifyJson.Parse(check.Output)
            .Where(i => string.Equals(i.Tool, toolId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var bad = mine.FirstOrDefault(i => !i.Ok);

        if (mine.Count == 0)
        {
            log("这个工具没有声明校验命令，跳过自检。");
        }
        else if (bad is null)
        {
            log("自检通过：" + string.Join("、", mine.Select(i => i.Command)));
        }
        else
        {
            log("自检失败：" + bad.Command);
            log((bad.Output ?? "(无输出)").Replace("\n", "\n    "));
        }

        // ---- 6. put the new version on PATH ----
        // An upgrade that leaves PATH pointing at an old directory is not an upgrade;
        // making the user remember to press "应用" is how "更新完就有问题" happens.
        log("同步 PATH（apply --yes）…");
        var apply = await CliBridge.RunAsync(stickRoot, new[] { "apply", "--yes" }, line => log(line), ct);
        var pathSynced = apply.Ok;
        if (pathSynced)
            log("PATH 已指向 " + info.AvailableVersion);
        else
            log("PATH 同步失败（" + apply.Describe() + "），请到概览页手动点“应用”。");

        // ---- 7. retire the replaced copy ----
        // Deleting the old dir is the last step on purpose: versioned dirs with no
        // "current" pointer leak one full toolchain per upgrade (244 MB for PowerShell),
        // but if PATH was not repointed the old dir is the only thing still making the
        // tool work. Keep it and let the user press 应用.
        var deleted = false;
        if (pathSynced
            && !string.Equals(oldDirRel.TrimEnd('/'), dirRel.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            var oldAbs = Path.Combine(stickRoot, oldDirRel.Replace('/', '\\'));
            if (Directory.Exists(oldAbs))
            {
                // Portable user data lives INSIDE the version directory: vscode keeps
                // settings and extensions in tools\vscode\<version>\data (635 MB here), and
                // it must stay next to Code.exe. Deleting the old version directory would
                // delete it, so move it first and keep the old directory if that fails.
                var dataReady = true;
                if (!string.IsNullOrWhiteSpace(tool.Tool.Data))
                {
                    var rel = tool.Tool.Data!.Replace('/', '\\');
                    var oldData = Path.Combine(oldAbs, rel);
                    var newData = Path.Combine(targetDir, rel);

                    if (Directory.Exists(oldData))
                    {
                        if (Directory.Exists(newData))
                        {
                            dataReady = false;
                            log("新版本目录里已经有 " + tool.Tool.Data + "，旧版本目录连同旧数据保留在 " + oldDirRel);
                        }
                        else
                        {
                            try
                            {
                                Directory.Move(oldData, newData);
                                log("已把便携数据目录 " + tool.Tool.Data + " 搬到新版本（" + dirRel + "）");
                            }
                            catch (Exception ex)
                            {
                                dataReady = false;
                                log("便携数据目录搬不动（" + ex.Message + "），旧版本目录保留，请手动把 "
                                    + oldDirRel + "\\" + rel + " 移到 " + dirRel + "\\" + rel);
                            }
                        }
                    }
                }

                if (!dataReady)
                {
                    log("为保住用户数据，旧版本目录不删除：" + oldDirRel);
                }
                else
                {
                    try
                    {
                        Directory.Delete(oldAbs, recursive: true);
                        deleted = true;
                        log("已删除旧版本目录 " + oldDirRel);
                    }
                    catch (Exception ex)
                    {
                        log("旧版本目录删不掉（可能还被占用），可以稍后手动删除：" + oldDirRel + " — " + ex.Message);
                    }
                }
            }
        }
        else if (!pathSynced
            && !string.Equals(oldDirRel.TrimEnd('/'), dirRel.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            log("旧版本目录保留在 " + oldDirRel + "，PATH 同步成功后会删除它。");
        }

        log("");
        var ok = pathSynced && bad is null;
        log(ok
            ? (deleted ? "完成。" : "完成（旧版本目录未删除）。")
            : "升级包已就位，但还没完全成功，见上面的日志。");

        return new InstallResult
        {
            Ok = ok,
            TargetDir = targetDir,
            FileCount = fileCount,
            Message = bad is not null
                ? "已升级到 " + info.AvailableVersion + "，但自检未通过"
                : pathSynced
                    ? "已升级到 " + info.AvailableVersion + "，PATH 已同步，自检通过"
                    : "已升级到 " + info.AvailableVersion + "，PATH 未同步",
        };
    }

    private static InstallResult Fail(Action<string> log, string message)
    {
        log(message);
        var r = new InstallResult { Ok = false, Message = message };
        r.Log.Add(message);
        return r;
    }

    private static string FileNameOf(string url)
    {
        try
        {
            var u = new Uri(url);
            return Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
        }
        catch
        {
            var i = url.LastIndexOf('/');
            return i < 0 ? string.Empty : url[(i + 1)..];
        }
    }

    /// <summary>
    /// The file name the server actually serves, from Content-Disposition or from the last
    /// segment of the redirect target. Empty when the server refuses to say — the caller then
    /// keeps the name it had, and the extractor falls back to sniffing the content.
    /// </summary>
    private static async Task<string> RemoteNameAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return string.Empty;

            var disposition = res.Content.Headers.ContentDisposition;
            var name = disposition?.FileNameStar ?? disposition?.FileName;
            name = name?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(name))
            {
                name = LastSegment(name!);
                if (name.Length > 0) return name;
            }

            var finalUri = res.RequestMessage?.RequestUri;
            if (finalUri is not null)
            {
                var candidate = Uri.UnescapeDataString(Path.GetFileName(finalUri.AbsolutePath));
                // Require an extension: a bare word is exactly what we are trying to replace.
                if (candidate.Length > 0 && Path.GetExtension(candidate).Length > 1) return candidate;
            }
        }
        catch
        {
            // Offline or blocked HEAD: not fatal, the download itself will report trouble.
        }

        return string.Empty;
    }

    private static string LastSegment(string name)
    {
        var i = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        return i < 0 ? name : name[(i + 1)..];
    }
}
