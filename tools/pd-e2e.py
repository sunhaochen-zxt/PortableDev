#!/usr/bin/env python3
"""End-to-end tests for pd.exe (the shell): connect, render, prompts, cancel, exit codes.

Usage: python tools/pd-e2e.py [stick-root]
"""
import json
import os
import shutil
import signal
import subprocess
import sys
import time

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
PD_DLL = os.path.abspath(os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll"))
KERNEL_DLL = os.path.abspath(os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64", "pd-kernel.dll"))

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


def pd(root, *argv, stdin_text=None, env_extra=None, timeout=60, new_group=False):
    command = [DOTNET, PD_DLL, "--root", root] + list(argv)
    environment = dict(os.environ)
    environment.pop("PD_KERNEL", None)
    if env_extra:
        environment.update(env_extra)
    flags = subprocess.CREATE_NEW_PROCESS_GROUP if new_group else 0
    return subprocess.run(
        command,
        input=stdin_text,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=environment,
        timeout=timeout,
        creationflags=flags,
    )


def prepare(root):
    os.makedirs(os.path.join(root, "state", "logs"), exist_ok=True)
    extensions = os.path.join(root, "extensions")
    shutil.rmtree(extensions, ignore_errors=True)
    os.makedirs(extensions, exist_ok=True)
    for name, fixture in (("echo", "echo-ext"), ("log", "log-ext")):
        shutil.copytree(os.path.join("tests", "fixtures", fixture), os.path.join(extensions, name))


def start_kernel(root):
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    process = subprocess.Popen(
        [DOTNET, KERNEL_DLL, "--root", root],
        stdout=log,
        stderr=subprocess.STDOUT,
        cwd=os.getcwd(),
    )
    deadline = time.time() + 20
    while time.time() < deadline:
        probe = pd(root, "version", timeout=15)
        if probe.returncode == 0:
            return process, log
        if process.poll() is not None:
            raise SystemExit("kernel exited during startup: " + open(os.path.join(root, "kernel.out"), encoding="utf-8", errors="replace").read()[-800:])
        time.sleep(0.3)
    raise SystemExit("kernel never became reachable")


def stop_kernel(root):
    pd(root, "kernel", "stop", timeout=30)
    time.sleep(0.5)


def kill_leftovers(root):
    """Stop only this test root's kernel/extensions. A global node/dotnet kill would hit other
    test runs and the developer's own processes, and would hide whether the kernel really exited."""
    if os.name != "nt":
        return
    marker = os.path.abspath(root).replace("'", "''")
    root_arg = str(root).replace("'", "''")
    script = (
        "$marker='" + marker + "'; $rootArg='" + root_arg + "'; "
        "Get-CimInstance Win32_Process | Where-Object { "
        "($_.Name -eq 'node.exe' -or $_.Name -eq 'dotnet.exe' -or $_.Name -eq 'pd-kernel.exe') -and $_.CommandLine -and "
        "(($_.CommandLine -like ('*' + $marker + '*')) -or ($_.CommandLine -like ('*--root*' + $root_arg + '*'))) "
        "} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)
    kernel, log = start_kernel(root)
    try:
        print("basic invocation")
        result = pd(root, "version")
        check("version exit 0", result.returncode == 0, result)
        check("version renders kv", "kernel" in result.stdout and "2.0.0" in result.stdout, result.stdout)
        check("version summary", "pd 2.0.0" in result.stdout, result.stdout)

        result = pd(root)
        check("no args shows status", result.returncode == 0 and "running" in result.stdout, result.stdout)

        print("json mode")
        result = pd(root, "--json", "version")
        lines = [line for line in result.stdout.splitlines() if line.strip()]
        parsed = []
        ok_json = True
        for line in lines:
            try:
                parsed.append(json.loads(line))
            except json.JSONDecodeError:
                ok_json = False
        check("json is pure NDJSON", ok_json and len(lines) >= 2, result.stdout[:300])
        check("json ends with done", bool(parsed) and parsed[-1].get("type") == "done", parsed[-1] if parsed else None)
        check("json done ok", bool(parsed) and parsed[-1].get("exitCode") == 0, parsed[-1] if parsed else None)

        print("quiet mode")
        result = pd(root, "--quiet", "version")
        check("quiet exit 0", result.returncode == 0, result)
        check("quiet drops kv table", "abi" not in result.stdout, result.stdout)

        print("help")
        result = pd(root, "--help")
        check("usage exit 0", result.returncode == 0 and "usage: pd" in result.stdout, result.stdout[:200])
        result = pd(root, "help")
        check("help from kernel", result.returncode == 0 and "kernel status" in result.stdout, result.stdout[:400])

        print("errors")
        result = pd(root, "statuz")
        check("unknown command exit 2", result.returncode == 2, result.returncode)
        check("unknown command hint", "ERR_UNKNOWN_COMMAND" in result.stdout + result.stderr, result.stdout + result.stderr)
        result = pd(root, "kernel", "restart", "nosuch")
        check("missing extension exit 20", result.returncode == 20, result.returncode)

        print("raw escape hatch")
        raw = json.dumps({"v": 1, "kind": "req", "id": "raw1", "data": {"argv": ["version"], "opts": {}}})
        result = pd(root, "--raw", raw)
        check("raw request works", result.returncode == 0, result.stdout + result.stderr)

        print("extension command")
        result = pd(root, "echo", "hello")
        check("echo exit 0", result.returncode == 0, result.stdout + result.stderr)
        check("echo renders progress and summary", "echo hello" in result.stdout, result.stdout)

        print("prompts")
        result = pd(root, "echo", "ask", stdin_text="")
        check("non-interactive prompt replies null", result.returncode == 0 and "reply=null" in result.stdout, result.stdout)
        result = pd(root, "--yes", "echo", "ask", stdin_text="")
        check("--yes prompt replies true", result.returncode == 0 and "reply=true" in result.stdout, result.stdout)

        print("log provider through the shell")
        result = pd(root, "log", "--tail", "5")
        check("log tail exit 0", result.returncode == 0, result.stdout + result.stderr)
        check("log tail has records", result.stdout.strip() != "", result.stdout[:200])

        print("cancel")
        command = [DOTNET, PD_DLL, "--root", root, "log", "--follow"]
        follow = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
        time.sleep(2.5)
        follow.send_signal(signal.CTRL_BREAK_EVENT)
        try:
            out, _ = follow.communicate(timeout=20)
            code = follow.returncode
        except subprocess.TimeoutExpired:
            follow.kill()
            out, code = "", None
        check("cancel exits 130", code == 130, (code, out[-300:]))

        print("no stick")
        empty = os.path.join(root, "not-a-stick")
        os.makedirs(empty, exist_ok=True)
        result = pd(empty, "version")
        check("not a stick exits 4", result.returncode == 4, (result.returncode, result.stderr[:200]))

        print("auto start")
        stop_kernel(root)
        time.sleep(1.0)
        environment = {"PD_KERNEL": DOTNET + " " + KERNEL_DLL}
        result = pd(root, "version", env_extra=environment, timeout=90)
        check("auto-start exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:], result.stderr[-300:]))
        time.sleep(0.5)
        status = pd(root, "kernel", "status")
        check("kernel alive after auto-start", status.returncode == 0 and "running" in status.stdout, status.stdout[:200])

        print("probes never start the kernel")
        stop_kernel(root)
        time.sleep(0.8)
        probe_env = {"PD_KERNEL": DOTNET + " " + KERNEL_DLL}
        status = pd(root, "kernel", "status", env_extra=probe_env)
        check("status without kernel exits 4", status.returncode == 4, (status.returncode, status.stdout[-200:]))
        stopped = pd(root, "kernel", "stop", env_extra=probe_env)
        check("stop without kernel exits 0", stopped.returncode == 0, (stopped.returncode, stopped.stdout[-200:]))
        status = pd(root, "kernel", "status", env_extra=probe_env)
        check("status/stop did not leave a kernel behind", status.returncode == 4, (status.returncode, status.stdout[-200:]))

        print("json help is machine readable")
        result = pd(root, "--json", "--help")
        try:
            json.loads(result.stdout.strip().splitlines()[0])
            json_ok = True
        except Exception:
            json_ok = False
        check("json help is one JSON object", json_ok, result.stdout[:160])
    finally:
        stop_kernel(root)
        try:
            kernel.wait(timeout=10)
        except subprocess.TimeoutExpired:
            kernel.kill()
        log.close()
        kill_leftovers(root)

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
