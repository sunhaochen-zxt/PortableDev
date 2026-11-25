#!/usr/bin/env python3
"""Window shell e2e: kernel link, real WebView2, apphost launch, no-ui and bad-page cases.

Usage: python -u tools/window-e2e.py [stick-root]
"""
import json
import os
import shutil
import subprocess
import sys
import time

DOTNET = r"Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"
DOTNET_ROOT = os.path.dirname(DOTNET)
KERNEL = "src/PortableDev.Kernel/bin/Debug/net8.0/win-x64/pd-kernel.dll"
WINDOW_DLL = os.path.join("src", "PortableDev.Window", "bin", "Debug", "net8.0-windows", "win-x64", "pd-window.dll")
PRODUCT_EXTENSIONS = ["storage", "rollback", "download", "unpack", "pkgmgr", "proc", "ui"]

PASS = 0
FAIL = 0
ROOT = None


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


def run(command, timeout=180, env=None):
    return subprocess.run(command, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=timeout, env=env)


def kill_leftovers(root):
    marker = os.path.abspath(root).replace("'", "''")
    script = ("$m='" + marker + "'; names=@('dotnet.exe','pd-window.exe','pd-kernel.exe','pd.exe'); "
              "Get-CimInstance Win32_Process | Where-Object { ($names -contains $_.Name) -and $_.CommandLine "
              "-and ($_.CommandLine -like ('*' + $m + '*')) } | "
              "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


def main():
    global ROOT
    ROOT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else default_root())
    print("stick root: " + ROOT)
    kill_leftovers(ROOT)
    shutil.rmtree(ROOT, ignore_errors=True)
    subprocess.run([DOTNET, KERNEL, "--root", ROOT, "--init", "--once"], check=True, capture_output=True, text=True)
    deploy(ROOT)

    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT

    print("self-test: kernel link and ui pages")
    report_path = os.path.join(ROOT, "self-test.json")
    kernel_dll = os.path.abspath(KERNEL)
    result = run([DOTNET, WINDOW_DLL, "--self-test", report_path, "--root", ROOT,
                  "--extensions", os.path.join(ROOT, "extensions"), "--kernel-dll", kernel_dll], env=env)
    report = read_json(report_path) or {}
    check("self-test exit 0", result.returncode == 0, (result.stdout[-200:], result.stderr[-300:], report))
    check("self-test ok", report.get("ok") is True, report)
    pages = report.get("pages") or []
    check("ui pages discovered", len(pages) >= 3, pages[:3])
    check("version answered", report.get("versionExitCode") == 0, report.get("versionExitCode"))
    check("list answered", report.get("listExitCode") == 0, report.get("listExitCode"))

    print("smoke: WebView2 + bridge (dotnet host)")
    smoke_path = os.path.join(ROOT, "smoke.json")
    smoke = run([DOTNET, WINDOW_DLL, "--root", ROOT, "--extensions", os.path.join(ROOT, "extensions"),
                 "--kernel-dll", kernel_dll, "--smoke", smoke_path], env=env)
    smoke_report = read_json(smoke_path) or {}
    check("smoke reported", bool(smoke_report), (smoke.stdout[-300:], smoke.stderr[-300:]))
    check("bridge injected", "object" in str(smoke_report.get("bridge", "")), smoke_report)
    check("smoke ok", smoke_report.get("ok") is True, smoke_report)

    outside = os.path.join(os.path.dirname(ROOT), ".win-e2e")
    print("apphost: real deployment outside the repository")
    kill_leftovers(outside)
    shutil.rmtree(outside, ignore_errors=True)
    built = run([sys.executable, os.path.join("tools", "deploy-dev.py"), outside])
    check("deployment built", built.returncode == 0 and os.path.isfile(os.path.join(outside, "app", "pd-window.exe")),
          (built.stdout[-300:], built.stderr[-300:]))

    apphost = os.path.join(outside, "app", "pd-window.exe")
    apphost_smoke = os.path.join(outside, "apphost-smoke.json")
    apphost_report = {}
    try:
        result = run([apphost, "--smoke", apphost_smoke, "--root", outside], env=env)
        apphost_report = read_json(apphost_smoke) or {}
        detail = (result.stdout[-300:], result.stderr[-300:], apphost_report)
    except OSError as error:
        detail = str(error)
    check("apphost smoke ok", apphost_report.get("ok") is True, detail)

    print("apphost: two concurrent windows")
    first = subprocess.Popen([apphost, "--smoke", os.path.join(outside, "concurrent-a.json"), "--root", outside],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env)
    second = subprocess.Popen([apphost, "--smoke", os.path.join(outside, "concurrent-b.json"), "--root", outside],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env)
    first.wait(timeout=120)
    second.wait(timeout=120)
    check("concurrent window a", (read_json(os.path.join(outside, "concurrent-a.json")) or {}).get("ok") is True)
    check("concurrent window b", (read_json(os.path.join(outside, "concurrent-b.json")) or {}).get("ok") is True)

    print("no ui extension")
    no_ui = outside + "-no-ui"
    kill_leftovers(no_ui)
    shutil.rmtree(no_ui, ignore_errors=True)
    shutil.copytree(outside, no_ui)
    shutil.rmtree(os.path.join(no_ui, "app", "extensions", "ui"), ignore_errors=True)
    no_ui_smoke = os.path.join(no_ui, "no-ui.json")
    run([os.path.join(no_ui, "app", "pd-window.exe"), "--smoke", no_ui_smoke, "--root", no_ui], env=env)
    no_ui_report = read_json(no_ui_smoke) or {}
    check("no-ui reports empty shell", no_ui_report.get("ok") is False
          and "no pages" in str(no_ui_report.get("error", "")), no_ui_report)

    print("rejects unsafe page entries")
    bad = os.path.join(no_ui, "app", "extensions", "ui")
    shutil.copytree(os.path.join("app", "extensions", "ui"), bad, dirs_exist_ok=True)
    manifest_path = os.path.join(bad, "ext.json")
    with open(manifest_path, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    manifest["pages"][0]["entry"] = "../evil.html"
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    bad_report = os.path.join(no_ui, "bad-pages.json")
    kill_leftovers(no_ui)
    run([DOTNET, os.path.join(no_ui, "app", "pd-window.dll"), "--self-test", bad_report,
         "--root", no_ui, "--extensions", os.path.join(no_ui, "app", "extensions")], env=env)
    bad_result = read_json(bad_report) or {}
    check("unsafe entry rejected", (bad_result.get("pages") or []) == [] or len(bad_result.get("pages") or []) == 2,
          bad_result.get("pages"))

    shell_dll = os.path.abspath(os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll"))
    for root in (ROOT, outside, no_ui):
        run([DOTNET, shell_dll, "--root", root, "kernel", "stop"], env=env, timeout=30)

    kill_leftovers(outside)
    kill_leftovers(no_ui)
    kill_leftovers(ROOT)

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
