#!/usr/bin/env python3
"""Build a double-clickable PortableDev app folder for local testing.

Copies the built kernel/shell/window/elevate binaries and the product extensions into
<dest>/app, initializes the stick root and writes launchers:
  <dest>/启动 PortableDev.cmd   -> opens the window shell (kernel starts on demand)
  <dest>/pd.cmd                 -> runs the CLI against this root

Usage: python tools/deploy-dev.py [dest]   (default: .run-app)
"""
import os
import shutil
import subprocess
import sys
import time

DOTNET = r"Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"
DOTNET_ROOT = os.path.dirname(DOTNET)
KERNEL_DIR = os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64")
SHELL_DIR = os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64")
ELEVATE_DIR = os.path.join("src", "PortableDev.Elevate", "bin", "Debug", "net8.0", "win-x64")
WINDOW_DIR = os.path.join("src", "PortableDev.Window", "bin", "Debug", "net8.0-windows", "win-x64")
EXTENSIONS_SRC = os.path.join("app", "extensions")


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
    print("deploying to " + dest)
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

    copy_binaries(KERNEL_DIR, app)
    copy_binaries(SHELL_DIR, app)
    copy_binaries(ELEVATE_DIR, app)
    copy_binaries(WINDOW_DIR, app)

    for name in sorted(os.listdir(EXTENSIONS_SRC)):
        source = os.path.join(EXTENSIONS_SRC, name)
        if os.path.isdir(source):
            shutil.copytree(source, os.path.join(extensions, name))

    subprocess.run([DOTNET, os.path.join(app, "pd-kernel.dll"), "--root", dest, "--init", "--once"],
                   check=True, capture_output=True, text=True)

    root_value = DOTNET_ROOT.replace("/", os.sep)
    launcher = os.path.join(dest, "启动 PortableDev.cmd")
    write_lines(launcher, [
        "@echo off",
        "setlocal",
        "for %%i in (\"%~dp0.\") do set \"ROOT=%%~fi\"",
        "set \"DOTNET_ROOT=" + root_value + "\"",
        "start \"\" \"%ROOT%" + os.sep + "app" + os.sep + "pd-window.exe\" --root \"%ROOT%\"",
    ])

    cli = os.path.join(dest, "pd.cmd")
    write_lines(cli, [
        "@echo off",
        "setlocal",
        "for %%i in (\"%~dp0.\") do set \"ROOT=%%~fi\"",
        "set \"DOTNET_ROOT=" + root_value + "\"",
        "\"" + DOTNET.replace("/", os.sep) + "\" \"%ROOT%" + os.sep + "app" + os.sep + "pd.dll\" --root \"%ROOT%\" %*",
    ])

    readme = os.path.join(dest, "README.txt")
    with open(readme, "w", encoding="utf-8") as handle:
        handle.write("PortableDev 本地测试盘\r\n\r\n")
        handle.write("双击 启动 PortableDev.cmd 打开窗口壳（会自动拉起内核）。\r\n")
        handle.write("命令行用 pd.cmd，例如：pd.cmd list / pd.cmd install <目标>\r\n")
        handle.write("\r\n为什么用启动器而不是直接双击 exe：随盘 .NET 不在 PATH 上，\r\n")
        handle.write("exe 的 host 解析发生在托管代码之前，只能靠 DOTNET_ROOT 环境变量。\r\n")

    print("done:")
    print("  window: " + launcher)
    print("  cli:    " + cli)
    return 0


if __name__ == "__main__":
    sys.exit(main())
