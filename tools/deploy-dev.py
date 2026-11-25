#!/usr/bin/env python3
"""Build a double-clickable PortableDev app folder for local testing.

Copies the built kernel/shell/window/elevate binaries and the product extensions into
<dest>/app, initializes the stick root and writes launchers:
  <dest>/启动 PortableDev.cmd   -> opens the window shell
  <dest>/pd.cmd                 -> runs the CLI against this root

Usage: python tools/deploy-dev.py [dest]   (default: .run-app)
"""
import os
import shutil
import subprocess
import sys

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
        if os.path.isfile(full) and not name.endswith((".pdb", ".xml")):
            shutil.copy2(full, os.path.join(target, name))


def main():
    dest = sys.argv[1] if len(sys.argv) > 1 else os.path.join(".run-app")
    dest = os.path.abspath(dest)
    app = os.path.join(dest, "app")
    print("deploying to " + dest)
    shutil.rmtree(dest, ignore_errors=True)
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

    launcher = os.path.join(dest, "启动 PortableDev.cmd")
    window_exe = os.path.join("app", "pd-window.exe")
    lines = [
        "@echo off",
        "setlocal",
        "set \"ROOT=%~dp0\"",
        "set \"DOTNET_ROOT=" + DOTNET_ROOT.replace("/", "\\") + "\"",
        "start \"\" \"%ROOT%" + window_exe + "\" --root \"%ROOT%\"",
    ]
    with open(launcher, "w", encoding="utf-8", newline="\r\n") as handle:
        handle.write("\r\n".join(lines) + "\r\n")

    cli = os.path.join(dest, "pd.cmd")
    cli_lines = [
        "@echo off",
        "setlocal",
        "set \"ROOT=%~dp0\"",
        "set \"DOTNET_ROOT=" + DOTNET_ROOT.replace("/", "\\") + "\"",
        "\"" + DOTNET.replace("/", "\\") + "\" \"%ROOT%app\\pd.dll\" --root \"%ROOT%\" %*",
    ]
    with open(cli, "w", encoding="utf-8", newline="\r\n") as handle:
        handle.write("\r\n".join(cli_lines) + "\r\n")

    readme = os.path.join(dest, "README.txt")
    with open(readme, "w", encoding="utf-8") as handle:
        handle.write("PortableDev 本地测试盘\r\n\r\n")
        handle.write("双击 启动 PortableDev.cmd 打开窗口壳（会自动拉起内核）。\r\n")
        handle.write("命令行用 pd.cmd，例如：pd.cmd list / pd.cmd install <目标>\r\n")

    print("done:")
    print("  window: " + launcher)
    print("  cli:    " + cli)
    return 0


if __name__ == "__main__":
    sys.exit(main())
