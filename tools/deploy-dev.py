#!/usr/bin/env python3
"""Build a double-clickable, machine-independent PortableDev app folder.

Copies the built kernel/shell/window/elevate binaries and the product extensions into
<dest>/app, copies the .NET runtime the stick should carry into <dest>/app/runtime,
initializes the stick root and writes launchers:

  <dest>/\u542f\u52a8 PortableDev.cmd   -> opens the window shell (kernel starts on demand)
  <dest>/pd.cmd                 -> runs the CLI against this root

The launchers run the bundled runtime explicitly, so the folder works on a machine with
no .NET installed and without DOTNET_ROOT set.

Usage: python tools/deploy-dev.py [dest]   (default: .run-app)
Environment:
  PD_DOTNET   path to the dotnet.exe used for deployment (and copied as the runtime)
"""
import glob
import hashlib
import os
import shutil
import subprocess
import sys
import time

KERNEL_DIR = os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64")
SHELL_DIR = os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64")
ELEVATE_DIR = os.path.join("src", "PortableDev.Elevate", "bin", "Debug", "net8.0", "win-x64")
WINDOW_DIR = os.path.join("src", "PortableDev.Window", "bin", "Debug", "net8.0-windows", "win-x64")
EXTENSIONS_SRC = os.path.join("app", "extensions")
RUNTIME_FILES = ("dotnet.exe", "hostfxr.dll", "hostpolicy.dll", "ijwhost.dll",
                 "LICENSE.txt", "ThirdPartyNotices.txt")


def resolve_dotnet():
    """PD_DOTNET, then the newest dev SDK, then PATH."""
    override = os.environ.get("PD_DOTNET")
    if override:
        if not os.path.isfile(override):
            raise SystemExit("PD_DOTNET points at a missing file: " + override)
        return os.path.abspath(override)

    dev_sdks = []
    for base in (r"Z:/PortableDev/tools/dotnet-sdk", os.path.join(os.path.expanduser("~"), ".dotnet")):
        if os.path.isdir(base):
            for name in os.listdir(base):
                candidate = os.path.join(base, name, "dotnet.exe")
                if os.path.isfile(candidate):
                    dev_sdks.append(candidate)
    if dev_sdks:
        return os.path.abspath(sorted(dev_sdks)[-1])

    found = shutil.which("dotnet")
    if found:
        return os.path.abspath(found)
    raise SystemExit("cannot find dotnet.exe for deployment; set PD_DOTNET")


def resolve_node():
    """PD_NODE, the dev toolchain, or PATH; None when no Node is available (built-ins need it)."""
    override = os.environ.get("PD_NODE")
    if override:
        if not os.path.isfile(override):
            raise SystemExit("PD_NODE points at a missing file: " + override)
        return os.path.abspath(override)

    for base in (r"Z:/PortableDev/tools/nodejs", os.path.join(os.path.expanduser("~"), "nodejs")):
        if os.path.isdir(base):
            candidates = []
            for name in os.listdir(base):
                candidate = os.path.join(base, name, "node.exe")
                if os.path.isfile(candidate):
                    candidates.append(candidate)
            if candidates:
                return os.path.abspath(sorted(candidates)[-1])

    found = shutil.which("node")
    return os.path.abspath(found) if found else None


def copy_node(node, runtime_dir):
    """Copy node.exe next to the bundled .NET runtime; built-in extensions are Node programs."""
    if not node:
        print("warning: node.exe not found (set PD_NODE); built-in extensions will need a system Node")
        return None
    target = os.path.join(runtime_dir, "node")
    os.makedirs(target, exist_ok=True)
    shutil.copy2(node, os.path.join(target, "node.exe"))
    return node


def newest_runtime_version(shared_root):
    if not os.path.isdir(shared_root):
        return None
    versions = [name for name in os.listdir(shared_root)
                if os.path.isdir(os.path.join(shared_root, name))]
    if not versions:
        return None
    return sorted(versions, key=lambda value: [int(part) if part.isdigit() else -1
                                               for part in value.replace("-", ".").split(".")])[-1]


def copy_runtime(dotnet_root, runtime_dir):
    """Copy the host, hostfxr and one shared runtime so the stick runs without a system .NET.

    Handles the SDK layout (dotnet.exe + host/fxr/<version>/hostfxr.dll + shared/...) and the
    flat runtime-pack layout (dotnet.exe + hostfxr.dll + shared/...).
    """
    os.makedirs(runtime_dir, exist_ok=True)
    host = os.path.join(dotnet_root, "dotnet.exe")
    if not os.path.isfile(host):
        raise SystemExit("the selected dotnet root has no dotnet.exe: " + dotnet_root)
    shutil.copy2(host, os.path.join(runtime_dir, "dotnet.exe"))
    for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
        source = os.path.join(dotnet_root, name)
        if os.path.isfile(source):
            shutil.copy2(source, os.path.join(runtime_dir, name))

    # hostfxr: SDKs keep it under host/fxr/<version>/; runtime packs put it next to dotnet.exe.
    hostfxr_version = newest_runtime_version(os.path.join(dotnet_root, "host", "fxr"))
    if hostfxr_version is not None:
        fxr_target = os.path.join(runtime_dir, "host", "fxr", hostfxr_version)
        os.makedirs(fxr_target, exist_ok=True)
        shutil.copytree(os.path.join(dotnet_root, "host", "fxr", hostfxr_version), fxr_target,
                        dirs_exist_ok=True)
    elif os.path.isfile(os.path.join(dotnet_root, "hostfxr.dll")):
        shutil.copy2(os.path.join(dotnet_root, "hostfxr.dll"), os.path.join(runtime_dir, "hostfxr.dll"))
    else:
        raise SystemExit("the selected dotnet root has no hostfxr (neither host/fxr nor hostfxr.dll): " + dotnet_root)

    # The window shell needs Microsoft.WindowsDesktop.App; console apps only need NETCore.App.
    # Copy every framework present, so the same stick runs both.
    copied_version = None
    for framework in ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"):
        shared_source = os.path.join(dotnet_root, "shared", framework)
        version = newest_runtime_version(shared_source)
        if version is None:
            if framework == "Microsoft.NETCore.App":
                raise SystemExit("no shared Microsoft.NETCore.App runtime under " + dotnet_root)
            print("warning: no " + framework + " under " + dotnet_root + " (window shell will not start)")
            continue
        shared_target = os.path.join(runtime_dir, "shared", framework, version)
        os.makedirs(shared_target, exist_ok=True)
        shutil.copytree(os.path.join(shared_source, version), shared_target, dirs_exist_ok=True)
        if framework == "Microsoft.NETCore.App":
            copied_version = version
    return copied_version


def copy_elevate_helper(app):
    """The storage extension runs pd-elevate from app\tools; package it there."""
    if not os.path.isdir(ELEVATE_DIR):
        raise SystemExit("missing build output: " + ELEVATE_DIR)
    target = os.path.join(app, "tools")
    os.makedirs(target, exist_ok=True)
    copied = 0
    for name in os.listdir(ELEVATE_DIR):
        if name.endswith(".dll") or name.endswith(".exe") or name.endswith(".json"):
            shutil.copy2(os.path.join(ELEVATE_DIR, name), os.path.join(target, name))
            copied += 1
    if not (os.path.isfile(os.path.join(target, "pd-elevate.exe")) or
            os.path.isfile(os.path.join(target, "pd-elevate.dll"))):
        raise SystemExit("pd-elevate helper was not copied into " + target)
    if not os.path.isfile(os.path.join(target, "pd-elevate.runtimeconfig.json")):
        raise SystemExit("pd-elevate.runtimeconfig.json is missing in " + target)
    return copied


def copy_proc_extension(extensions):
    """proc is the only compiled extension: its dll is gitignored, so a fresh checkout has to get
    it from the ProcExt build output instead of relying on a hand-copied file."""
    source = os.path.join("src", "PortableDev.ProcExt", "bin", "Debug", "net8.0", "win-x64")
    target = os.path.join(extensions, "proc")
    if not os.path.isdir(source):
        raise SystemExit("missing " + source + "; build src/PortableDev.ProcExt first")
    os.makedirs(target, exist_ok=True)
    copied = 0
    for name in os.listdir(source):
        if name.endswith(".dll") or name.endswith(".json"):
            shutil.copy2(os.path.join(source, name), os.path.join(target, name))
            copied += 1
    return copied


def check_shared_assemblies(dirs):
    """The app dirs each carry their own copy of PortableDev.Core.dll; copying them in order can
    silently overwrite a fresh kernel with a stale window copy. Refuse to deploy such a mix."""
    digests = {}
    for name, directory in dirs.items():
        path = os.path.join(directory, "PortableDev.Core.dll")
        if os.path.isfile(path):
            digests.setdefault(hashlib.sha256(open(path, "rb").read()).hexdigest(), []).append(name)
    if len(digests) > 1:
        groups = "；".join(" + ".join(names) for names in digests.values())
        raise SystemExit("mixed PortableDev.Core.dll builds (" + groups +
                         ")；run the full rebuild (kernel/shell/elevate/window) before deploying")


def copy_binaries(source, target):
    if not os.path.isdir(source):
        raise SystemExit("missing build output: " + source)
    for name in os.listdir(source):
        full = os.path.join(source, name)
        if os.path.isfile(full):
            if not name.endswith((".pdb", ".xml")):
                shutil.copy2(full, os.path.join(target, name))
            continue
        # Native assets live in subfolders such as runtimes/win-x64/native; a flat copy
        # silently dropped them (e.g. WebView2Loader.dll resolution paths).
        if os.path.isdir(full):
            shutil.copytree(full, os.path.join(target, name), dirs_exist_ok=True)


def write_lines(path, lines):
    with open(path, "w", encoding="utf-8", newline="\r\n") as handle:
        handle.write("\r\n".join(lines) + "\r\n")


def main():
    dest = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".run-app")
    app = os.path.join(dest, "app")
    dotnet = resolve_dotnet()
    dotnet_root = os.path.dirname(dotnet)
    node = resolve_node()
    runtime_dir = os.path.join(app, "runtime")
    # Reuse an unchanged runtime: renaming it aside and back is instant on the same volume,
    # while re-copying 170 MB on every deploy is not.
    stamp = "dotnet=" + dotnet + "|" + str(os.path.getmtime(dotnet)) + "|node=" + str(node)
    cache_dir = dest + ".runtime-cache"
    cached_runtime = False
    if os.path.isdir(dest):
        stamp_file = os.path.join(runtime_dir, ".stamp")
        try:
            with open(stamp_file, "r", encoding="utf-8") as handle:
                cached_runtime = handle.read().strip() == stamp
        except OSError:
            cached_runtime = False
        if cached_runtime:
            shutil.rmtree(cache_dir, ignore_errors=True)
            try:
                os.replace(runtime_dir, cache_dir)
            except OSError:
                cached_runtime = False
    print("deploying to " + dest + " (dotnet: " + dotnet + ", runtime cached: " + str(cached_runtime) + ")")
    if os.path.isdir(dest):
        try:
            shutil.rmtree(dest)
        except OSError:
            aside = dest + ".old-" + str(int(time.time()))
            os.replace(dest, aside)
            print("note: the previous folder is in use; moved it aside to " + aside)

    os.makedirs(app, exist_ok=True)
    extensions = os.path.join(app, "extensions")
    os.makedirs(extensions, exist_ok=True)

    check_shared_assemblies({
        "kernel": KERNEL_DIR,
        "shell": SHELL_DIR,
        "elevate": ELEVATE_DIR,
        "window": WINDOW_DIR,
        # proc is a compiled extension and also loads PortableDev.Core.dll.
        "procext": os.path.join("src", "PortableDev.ProcExt", "bin", "Debug", "net8.0", "win-x64"),
    })
    copy_binaries(KERNEL_DIR, app)
    copy_binaries(SHELL_DIR, app)
    copy_elevate_helper(app)
    copy_binaries(WINDOW_DIR, app)

    for name in sorted(os.listdir(EXTENSIONS_SRC)):
        source = os.path.join(EXTENSIONS_SRC, name)
        if os.path.isdir(source):
            shutil.copytree(source, os.path.join(extensions, name),
                            ignore=shutil.ignore_patterns("_preview*.html", "*.log", ".git", "node_modules"))

    # proc is the only compiled extension and its dll is gitignored: take it from the build output.
    copy_proc_extension(extensions)

    # Pristine copy of the built-in extensions: `pd doctor --fix` restores from here.
    baseline = os.path.join(app, "baseline", "extensions")
    for name in sorted(os.listdir(extensions)):
        source = os.path.join(extensions, name)
        if os.path.isdir(source):
            shutil.copytree(source, os.path.join(baseline, name),
                            ignore=shutil.ignore_patterns("_preview*.html", "*.log", ".git", "node_modules"))

    if cached_runtime:
        os.replace(cache_dir, runtime_dir)
        runtime_version = newest_runtime_version(os.path.join(runtime_dir, "shared", "Microsoft.NETCore.App"))
    else:
        runtime_version = copy_runtime(dotnet_root, runtime_dir)
        copy_node(node, runtime_dir)
        with open(os.path.join(runtime_dir, ".stamp"), "w", encoding="utf-8") as handle:
            handle.write(stamp + "\n")

    init = subprocess.run([dotnet, os.path.join(app, "pd-kernel.dll"), "--root", dest, "--init", "--once"],
                          capture_output=True, text=True)
    if init.returncode != 0:
        raise SystemExit("kernel init failed (" + str(init.returncode) + "): " +
                         (init.stderr or init.stdout or "").strip()[-600:])

    sep = os.sep
    launcher = os.path.join(dest, "\u542f\u52a8 PortableDev.cmd")
    write_lines(launcher, [
        "@echo off",
        "setlocal",
        "for %%i in (\"%~dp0.\") do set \"ROOT=%%~fi\"",
        "set \"DOTNET_ROOT=%ROOT%" + sep + "app" + sep + "runtime\"",
        "start \"\" \"%ROOT%" + sep + "app" + sep + "runtime" + sep + "dotnet.exe\" "
        "\"%ROOT%" + sep + "app" + sep + "pd-window.dll\" --root \"%ROOT%\"",
    ])

    cli = os.path.join(dest, "pd.cmd")
    write_lines(cli, [
        "@echo off",
        "setlocal",
        "for %%i in (\"%~dp0.\") do set \"ROOT=%%~fi\"",
        "set \"DOTNET_ROOT=%ROOT%" + sep + "app" + sep + "runtime\"",
        "\"%ROOT%" + sep + "app" + sep + "runtime" + sep + "dotnet.exe\" "
        "\"%ROOT%" + sep + "app" + sep + "pd.dll\" --root \"%ROOT%\" %*",
    ])

    readme = os.path.join(dest, "README.txt")
    with open(readme, "w", encoding="utf-8") as handle:
        handle.write("PortableDev \u672c\u5730\u6d4b\u8bd5\u76d8\r\n\r\n")
        handle.write("\u53cc\u51fb \u542f\u52a8 PortableDev.cmd \u6253\u5f00\u7a97\u53e3\u58f3\uff08\u4f1a\u81ea\u52a8\u62c9\u8d77\u5185\u6838\uff09\u3002\r\n")
        handle.write("\u547d\u4ee4\u884c\u7528 pd.cmd\uff0c\u4f8b\u5982\uff1apd.cmd list / pd.cmd install <\u76ee\u6807>\r\n")
        handle.write("\r\n\u968f\u76d8 .NET \u8fd0\u884c\u65f6\u5728 app" + sep + "runtime\uff08.NET " + runtime_version +
                     "\uff09\uff0c\u672c\u673a\u4e0d\u9700\u8981\u5b89\u88c5 .NET\u3002\r\n")
        handle.write("\u542f\u52a8\u5668\u5df2\u628a DOTNET_ROOT \u6307\u5411\u968f\u76d8\u8fd0\u884c\u65f6\u3002\r\n")

    print("done:")
    print("  window: " + launcher)
    print("  cli:    " + cli)
    print("  runtime: app" + sep + "runtime (NET " + runtime_version + ", from " + dotnet_root + ")")
    return 0


if __name__ == "__main__":
    sys.exit(main())
