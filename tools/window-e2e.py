#!/usr/bin/env python3
"""Window shell e2e: kernel link, real WebView2, apphost launch, no-ui and bad-page cases.

Kernel rule: every captured step runs against an explicitly pre-started kernel
(stdout goes to a file, never to a captured pipe). The window shell's on-demand
kernel autostart is a fallback for interactive use only: an autostarted kernel
inherits the parent's stdio, so combining autostart with a captured step hangs
the step forever waiting for pipe EOF.

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
SHELL = os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll")
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
              "-and ($_.CommandLine -like ('*' + $m + '*' )) } | "
              "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


def pipe_name(root):
    with open(os.path.join(root, "state", "stick.json"), encoding="utf-8") as handle:
        stick = json.load(handle)["stickId"]
    bs = chr(92)
    return bs + bs + "." + bs + "pipe" + bs + "portabledev-" + stick


def start_kernel(root, kernel_dll, env):
    """Starts the kernel with output to a file and waits for the pipe. Returns the Popen object."""
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    child = subprocess.Popen([DOTNET, os.path.abspath(kernel_dll), "--root", root],
                             stdout=log, stderr=subprocess.STDOUT, env=env)
    deadline = time.time() + 30
    while time.time() < deadline:
        if child.poll() is not None:
            raise SystemExit("kernel for " + root + " exited during startup")
        try:
            handle = open(pipe_name(root), "r+b", buffering=0)
            handle.close()
            return child, log
        except OSError:
            time.sleep(0.3)
    raise SystemExit("kernel for " + root + " never opened its pipe")


def stop_kernel(root, env):
    run([DOTNET, os.path.abspath(SHELL), "--root", root, "kernel", "stop"], env=env, timeout=60)
    time.sleep(1.0)


def main():
    global ROOT
    ROOT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else default_root())
    print("stick root: " + ROOT)
    kill_leftovers(ROOT)
    shutil.rmtree(ROOT, ignore_errors=True)
    subprocess.run([DOTNET, KERNEL, "--root", ROOT, "--init", "--once"], check=True, capture_output=True, text=True)
    deploy(ROOT)
    kernel_dll = os.path.abspath(KERNEL)

    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT

    kernel, kernel_log = start_kernel(ROOT, kernel_dll, env)
    try:
        print("self-test: kernel link and ui pages")
        report_path = os.path.join(ROOT, "self-test.json")
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
        check("real page loaded", "pd.local" in str(smoke_report.get("url", "")), smoke_report)
        check("smoke ok", smoke_report.get("ok") is True, smoke_report)

        print("smoke-check: full page loop renders kernel data")
        loop_path = os.path.join(ROOT, "loop-check.json")
        loop = run([DOTNET, WINDOW_DLL, "--root", ROOT, "--extensions", os.path.join(ROOT, "extensions"),
                    "--kernel-dll", kernel_dll, "--smoke-check", loop_path], env=env)
        loop_report = read_json(loop_path) or {}
        check("loop reported", bool(loop_report), (loop.stdout[-300:], loop.stderr[-300:]))
        check("page rendered kernel version", "pd " in str(loop_report.get("versionRendered", "")), loop_report)
        check("loop ok", loop_report.get("ok") is True, loop_report)

        print("follow: streaming log tail through the bridge path")
        shell_dll = os.path.abspath(os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll"))
        follow_proc = subprocess.Popen([DOTNET, shell_dll, "--root", ROOT, "--json", "log", "--tail", "3", "--follow"],
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                       encoding="utf-8", errors="replace", env=env)
        try:
            follow_out, _ = follow_proc.communicate(timeout=20)
        except subprocess.TimeoutExpired:
            follow_proc.kill()
            follow_out, _ = follow_proc.communicate(timeout=10)
        check("follow streams records", '"type":"log"' in (follow_out or "").replace(" ", ""), (follow_out or "")[:300])
    finally:
        stop_kernel(ROOT, env)
        try:
            kernel.wait(timeout=20)
        except subprocess.TimeoutExpired:
            kernel.kill()
        kernel_log.close()
        kill_leftovers(ROOT)

    outside = os.path.join(os.path.dirname(ROOT), ".win-e2e")
    print("apphost: real deployment outside the repository")
    kill_leftovers(outside)
    shutil.rmtree(outside, ignore_errors=True)
    built = run([sys.executable, os.path.join("tools", "deploy-dev.py"), outside])
    check("deployment built", built.returncode == 0 and os.path.isfile(os.path.join(outside, "app", "pd-window.exe")),
          (built.stdout[-300:], built.stderr[-300:]))
    outside_kernel_dll = os.path.join(outside, "app", "pd-kernel.dll")
    outside_kernel, outside_log = start_kernel(outside, outside_kernel_dll, env)
    try:
        apphost = os.path.join(outside, "app", "pd-window.exe")
        apphost_smoke = os.path.join(outside, "apphost-smoke.json")
        result = run([apphost, "--smoke", apphost_smoke, "--root", outside,
                      "--kernel-dll", outside_kernel_dll], env=env)
        apphost_report = read_json(apphost_smoke) or {}
        check("apphost smoke ok", apphost_report.get("ok") is True,
              (result.stdout[-300:], result.stderr[-300:], apphost_report))

        print("single instance: the second window exits without a report")
        hold_env = dict(env)
        hold_env["PD_WINDOW_SMOKE_HOLD_MS"] = "45000"
        first = subprocess.Popen([apphost, "--smoke", os.path.join(outside, "concurrent-a.json"),
                                  "--root", outside, "--kernel-dll", outside_kernel_dll],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=hold_env)
        # The first window writes its report as soon as the page loads, then holds the mutex
        # for another 45s; only then is the second launch guaranteed to observe a held mutex.
        deadline = time.time() + 90
        while time.time() < deadline and not os.path.isfile(os.path.join(outside, "concurrent-a.json")):
            if first.poll() is not None:
                break
            time.sleep(0.5)
        check("first window reported", os.path.isfile(os.path.join(outside, "concurrent-a.json")))
        second = run([apphost, "--smoke", os.path.join(outside, "concurrent-b.json"),
                      "--root", outside, "--kernel-dll", outside_kernel_dll], env=env, timeout=90)
        first.wait(timeout=120)
        first_report = read_json(os.path.join(outside, "concurrent-a.json")) or {}
        check("first window ok", first_report.get("ok") is True, first_report)
        check("second window exits cleanly", second.returncode == 0, second.returncode)
        check("second window writes no report", not os.path.isfile(os.path.join(outside, "concurrent-b.json")))
    finally:
        stop_kernel(outside, env)
        try:
            outside_kernel.wait(timeout=20)
        except subprocess.TimeoutExpired:
            outside_kernel.kill()
        outside_log.close()
        kill_leftovers(outside)

    print("missing ui extension is restored from the baseline")
    no_ui = outside + "-no-ui"
    kill_leftovers(no_ui)
    shutil.rmtree(no_ui, ignore_errors=True)
    shutil.copytree(outside, no_ui, ignore=shutil.ignore_patterns("state"))
    subprocess.run([DOTNET, KERNEL, "--root", no_ui, "--init", "--once"], check=True, capture_output=True, text=True)
    ui_dir = os.path.join(no_ui, "app", "extensions", "ui")
    shutil.rmtree(ui_dir, ignore_errors=True)
    no_ui_kernel, no_ui_log = start_kernel(no_ui, outside_kernel_dll, env)
    try:
        repaired_smoke = os.path.join(no_ui, "no-ui-repaired.json")
        run([os.path.join(no_ui, "app", "pd-window.exe"), "--smoke", repaired_smoke, "--root", no_ui,
             "--kernel-dll", outside_kernel_dll], env=env)
        repaired_report = read_json(repaired_smoke) or {}
        check("ui extension restored from baseline",
              repaired_report.get("ok") is True and os.path.isdir(ui_dir), repaired_report)
    finally:
        stop_kernel(no_ui, env)
        try:
            no_ui_kernel.wait(timeout=20)
        except subprocess.TimeoutExpired:
            no_ui_kernel.kill()
        no_ui_log.close()
        kill_leftovers(no_ui)

    print("no ui extension and no baseline reports an empty shell")
    bare = outside + "-bare"
    kill_leftovers(bare)
    shutil.rmtree(bare, ignore_errors=True)
    shutil.copytree(outside, bare, ignore=shutil.ignore_patterns("state"))
    subprocess.run([DOTNET, KERNEL, "--root", bare, "--init", "--once"], check=True, capture_output=True, text=True)
    shutil.rmtree(os.path.join(bare, "app", "extensions", "ui"), ignore_errors=True)
    shutil.rmtree(os.path.join(bare, "app", "baseline"), ignore_errors=True)
    bare_kernel, bare_log = start_kernel(bare, outside_kernel_dll, env)
    try:
        bare_smoke = os.path.join(bare, "no-ui.json")
        run([os.path.join(bare, "app", "pd-window.exe"), "--smoke", bare_smoke, "--root", bare,
             "--kernel-dll", outside_kernel_dll], env=env)
        bare_report = read_json(bare_smoke) or {}
        check("no-ui reports empty shell", bare_report.get("ok") is False
              and "no pages" in str(bare_report.get("error", "")), bare_report)
        no_ui = bare
        no_ui_kernel, no_ui_log = bare_kernel, bare_log

        print("rejects unsafe page entries")
        bad = os.path.join(no_ui, "app", "extensions", "ui")
        shutil.copytree(os.path.join("app", "extensions", "ui"), bad, dirs_exist_ok=True)
        manifest_path = os.path.join(bad, "ext.json")
        with open(manifest_path, "r", encoding="utf-8") as handle:
            manifest = json.load(handle)
        manifest["pages"][0]["entry"] = "../evil.html"
        with open(manifest_path, "w", encoding="utf-8") as handle:
            json.dump(manifest, handle)
    finally:
        stop_kernel(no_ui, env)
        try:
            no_ui_kernel.wait(timeout=20)
        except subprocess.TimeoutExpired:
            no_ui_kernel.kill()
        no_ui_log.close()
        kill_leftovers(no_ui)
    no_ui_kernel, no_ui_log = start_kernel(no_ui, outside_kernel_dll, env)
    try:
        bad_report = os.path.join(no_ui, "bad-pages.json")
        run([DOTNET, os.path.join(no_ui, "app", "pd-window.dll"), "--self-test", bad_report,
             "--root", no_ui, "--extensions", os.path.join(no_ui, "app", "extensions"),
             "--kernel-dll", outside_kernel_dll], env=env)
        bad_result = read_json(bad_report) or {}
        # Derived, not hard-coded: the ui extension may gain or lose pages.
        with open(manifest_path, "r", encoding="utf-8") as handle:
            expected_pages = len(json.load(handle).get("pages") or []) - 1
        check("unsafe entry rejected", len(bad_result.get("pages") or []) == expected_pages, bad_result.get("pages"))
    finally:
        stop_kernel(no_ui, env)
        try:
            no_ui_kernel.wait(timeout=20)
        except subprocess.TimeoutExpired:
            no_ui_kernel.kill()
        no_ui_log.close()
        kill_leftovers(no_ui)
        kill_leftovers(ROOT)

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
