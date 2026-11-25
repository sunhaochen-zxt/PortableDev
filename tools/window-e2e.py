#!/usr/bin/env python3
"""Window shell e2e: kernel link + page plumbing (self-test) and WebView2 smoke with the bridge.

Usage: python -u tools/window-e2e.py [stick-root]
"""
import json
import os
import shutil
import subprocess
import sys
import time

DOTNET = r"Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"
KERNEL = "src/PortableDev.Kernel/bin/Debug/net8.0/win-x64/pd-kernel.dll"
WINDOW = "src/PortableDev.Window/bin/Debug/net8.0-windows/win-x64/pd-window.dll"
PRODUCT_EXTENSIONS = ["storage", "rollback", "download", "unpack", "pkgmgr", "proc", "ui"]

PASS = 0
FAIL = 0


def check(name, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        print(f"  ok   {name}")
    else:
        FAIL += 1
        print(f"  FAIL {name} {str(detail)[:400]}")


def default_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(os.path.dirname(os.path.dirname(here)), ".run-window")


def deploy(root):
    for name in PRODUCT_EXTENSIONS:
        target = os.path.join(root, "extensions", name)
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(os.path.join("app", "extensions", name), target)
    shutil.rmtree(os.path.join(root, "extensions", "log"), ignore_errors=True)
    shutil.copytree(os.path.join("tests", "fixtures", "log-ext"), os.path.join(root, "extensions", "log"))
    for directory in ("store", "current", "staging", "packages"):
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent", "wire", "logs", "tmp", "sources"):
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)
    with open(os.path.join(root, "state", "wire", "fake-mode"), "w", encoding="utf-8") as handle:
        handle.write("1\n")


def read_json(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError):
        return None


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else default_root()
    print(f"stick root: {root}")
    shutil.rmtree(root, ignore_errors=True)
    subprocess.run([DOTNET, KERNEL, "--root", root, "--init", "--once"],
                   check=True, capture_output=True, text=True)
    deploy(root)

    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.dirname(DOTNET)
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    kernel = subprocess.Popen([DOTNET, KERNEL, "--root", root], stdout=log, stderr=subprocess.STDOUT, env=env)
    try:
        deadline = time.time() + 20
        while time.time() < deadline and not os.path.isfile(os.path.join(root, "state", "stick.json")):
            time.sleep(0.2)
        time.sleep(1.0)

        print("self-test: kernel link and ui pages")
        report_path = os.path.join(root, "self-test.json")
        result = subprocess.run([DOTNET, WINDOW, "--self-test", report_path,
                                 "--root", root, "--extensions", os.path.join(root, "extensions")],
                                capture_output=True, text=True, encoding="utf-8", errors="replace",
                                env=env, timeout=120)
        report = read_json(report_path) or {}
        check("self-test exit 0", result.returncode == 0, (result.stdout[-200:], result.stderr[-300:], report))
        check("self-test ok", report.get("ok") is True, report)
        pages = report.get("pages") or []
        check("ui pages discovered", len(pages) >= 3, pages[:3])
        check("version answered", report.get("versionExitCode") == 0, report.get("versionExitCode"))
        check("list answered", report.get("listExitCode") == 0, report.get("listExitCode"))

        print("smoke: WebView2 + bridge injection")
        smoke_path = os.path.join(root, "smoke.json")
        smoke = subprocess.run([DOTNET, WINDOW, "--root", root, "--extensions", os.path.join(root, "extensions"),
                                "--smoke", smoke_path],
                               capture_output=True, text=True, encoding="utf-8", errors="replace",
                               env=env, timeout=120)
        smoke_report = read_json(smoke_path) or {}
        check("smoke reported", bool(smoke_report), (smoke.stdout[-300:], smoke.stderr[-300:]))
        check("bridge injected", "object" in str(smoke_report.get("bridge", "")), smoke_report)
        check("smoke ok", smoke_report.get("ok") is True, smoke_report)
    finally:
        try:
            subprocess.run([DOTNET, KERNEL, "--root", root, "--init", "--once"],
                           capture_output=True, text=True, timeout=30)
        except Exception:
            pass
        kernel.terminate()
        try:
            kernel.wait(timeout=10)
        except subprocess.TimeoutExpired:
            kernel.kill()
        log.close()

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
