#!/usr/bin/env python3
"""Job-object e2e: package scripts run inside pd-job (limits + kill-on-close) while the request protocol still works."""
import ctypes
import json
import os
import shutil
import subprocess
import sys
import time

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
DOTNET_DIR = os.path.dirname(DOTNET)
KERNEL_DLL = os.path.abspath(os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64", "pd-kernel.dll"))
PD_DLL = os.path.abspath(os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll"))
JOB_DIR = os.path.abspath(os.path.join("archive", "pd-job", "bin", "Debug", "net8.0", "win-x64"))

PASS = 0
FAIL = 0


def check(name, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        print(f"  ok   {name}")
    else:
        FAIL += 1
        print(f"  FAIL {name} {str(detail)[:600]}")


def pd(root, *args, timeout=120):
    environment = dict(os.environ)
    environment.pop("PD_KERNEL", None)
    return subprocess.run(
        [DOTNET, PD_DLL, "--root", root, *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=environment, timeout=timeout,
    )


def done_of(stdout):
    result = None
    for line in stdout.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        if event.get("type") == "done":
            result = event
    return result


def pid_alive(pid):
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    STILL_ACTIVE = 259
    handle = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return False
    try:
        code = ctypes.c_ulong()
        if not ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return False
        return code.value == STILL_ACTIVE
    finally:
        ctypes.windll.kernel32.CloseHandle(handle)


def kill_leftovers(root):
    if os.name != "nt":
        return
    marker = os.path.abspath(root).replace("'", "''")
    root_arg = str(root).replace("'", "''")
    script = (
        "$marker='" + marker + "'; $rootArg='" + root_arg + "'; "
        "Get-CimInstance Win32_Process | Where-Object { "
        "($_.Name -eq 'node.exe' -or $_.Name -eq 'dotnet.exe' -or $_.Name -eq 'pd-kernel.exe' -or $_.Name -eq 'pd-job.exe') -and $_.CommandLine -and "
        "(($_.CommandLine -like ('*' + $marker + '*')) -or ($_.CommandLine -like ('*--root*' + $rootArg + '*'))) "
        "} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


def make_script_pkg(root, name, pid, version, script, script_meta=None):
    directory = os.path.join(root, "jobpkgs", name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files", "bin"), exist_ok=True)
    manifest = {"schema": 1, "id": pid, "version": version, "platform": "win-x64",
                "script": script_meta or {"entry": "install.py", "interpreter": "python", "on": ["install"]}}
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    with open(os.path.join(directory, "install.py"), "w", encoding="utf-8") as handle:
        handle.write(script)
    with open(os.path.join(directory, "files", "bin", "base.txt"), "w", encoding="utf-8") as handle:
        handle.write("base " + pid)
    return directory


WRITE_SCRIPT = """import json, sys
print(json.dumps({"type": "request", "id": "1", "cmd": "write", "args": {"path": "bin/created.txt", "content": "job ok"}}), flush=True)
assert json.loads(sys.stdin.readline()).get("ok")
"""

SPAWN_SCRIPT = """import json, os, subprocess, sys
child = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(60)"])
open(os.path.join(os.environ["PD_ROOT"], "state", "tmp", "job-child.pid"), "w").write(str(child.pid))
print(json.dumps({"type": "request", "id": "1", "cmd": "write", "args": {"path": "bin/spawn.txt", "content": "ok"}}), flush=True)
assert json.loads(sys.stdin.readline()).get("ok")
"""

TIMEOUT_SCRIPT = """import os, subprocess, sys, time
child = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(60)"])
open(os.path.join(os.environ["PD_ROOT"], "state", "tmp", "job-timeout-child.pid"), "w").write(str(child.pid))
time.sleep(60)
"""


def prepare(root):
    for name in ("storage", "rollback", "download", "unpack", "pkgmgr"):
        target = os.path.join(root, "extensions", name)
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(os.path.join("app", "extensions", name), target)
    log_target = os.path.join(root, "extensions", "log")
    shutil.rmtree(log_target, ignore_errors=True)
    shutil.copytree(os.path.join("tests", "fixtures", "log-ext"), log_target)
    for directory in ("store", "current", "staging", "packages", "home"):
        shutil.rmtree(os.path.join(root, directory), ignore_errors=True)
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent", "wire", "sources", "tmp", "logs", "generations", "trash"):
        shutil.rmtree(os.path.join(root, "state", directory), ignore_errors=True)
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)
    os.makedirs(os.path.join(root, "config"), exist_ok=True)
    try:
        os.remove(os.path.join(root, "config", "sources.json"))
    except OSError:
        pass
    wire_dir = os.path.join(root, "state", "wire")
    os.makedirs(wire_dir, exist_ok=True)
    with open(os.path.join(wire_dir, "fake-mode"), "w", encoding="utf-8") as handle:
        handle.write("1\n")
    with open(os.path.join(root, "state", "stick.json"), "w", encoding="utf-8") as handle:
        json.dump({"schema": 1, "stickId": "pd-" + os.urandom(6).hex(), "source": "manual",
                   "volumeSerial": "", "label": ""}, handle)
    tools = os.path.join(root, "app", "tools")
    os.makedirs(tools, exist_ok=True)
    for name in ("pd-job.dll", "pd-job.deps.json", "pd-job.runtimeconfig.json"):
        source = os.path.join(JOB_DIR, name)
        if not os.path.isfile(source):
            raise SystemExit("missing pd-job build output: " + source)
        shutil.copyfile(source, os.path.join(tools, name))


def start_kernel(root):
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    environment = dict(os.environ)
    environment["PATH"] = DOTNET_DIR + ";" + environment.get("PATH", "")
    process = subprocess.Popen([DOTNET, KERNEL_DLL, "--root", root], stdout=log, stderr=subprocess.STDOUT, cwd=os.getcwd(), env=environment)
    deadline = time.time() + 20
    while time.time() < deadline:
        probe = pd(root, "version", timeout=15)
        if probe.returncode == 0:
            time.sleep(0.4)
            if process.poll() is not None:
                raise SystemExit("kernel exited right after a successful probe")
            return process, log
        if process.poll() is not None:
            raise SystemExit("kernel exited during startup")
        time.sleep(0.3)
    raise SystemExit("kernel never became reachable")


def stop_kernel(root):
    pd(root, "kernel", "stop", timeout=30)
    time.sleep(0.8)


def read_pid(path):
    try:
        return int(open(path, encoding="utf-8").read().strip())
    except (OSError, ValueError):
        return None


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run-job"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)

    normal = make_script_pkg(root, "jobwrite", "jobwrite", "1.0.0", WRITE_SCRIPT)
    spawner = make_script_pkg(root, "jobspawn", "jobspawn", "1.0.0", SPAWN_SCRIPT)
    timeout_pkg = make_script_pkg(root, "jobtimeout", "jobtimeout", "1.0.0", TIMEOUT_SCRIPT,
                                  {"entry": "install.py", "interpreter": "python", "on": ["install"], "timeout": "2s"})

    kernel, log = start_kernel(root)
    try:
        print("normal script through pd-job")
        result = pd(root, "--json", "install", normal)
        done = done_of(result.stdout)
        store = ((done or {}).get("metrics") or {}).get("storePath", "")
        check("install normal script exit 0", result.returncode == 0, (result.returncode, result.stdout[-500:]))
        check("script write reached the store", os.path.isfile(os.path.join(store, "bin", "created.txt")), store)

        print("child processes die with the job")
        result = pd(root, "--json", "install", spawner)
        check("install spawn script exit 0", result.returncode == 0, (result.returncode, result.stdout[-500:]))
        child_pid = read_pid(os.path.join(root, "state", "tmp", "job-child.pid"))
        check("spawned child pid recorded", child_pid is not None, child_pid)
        if child_pid:
            deadline = time.time() + 10
            while time.time() < deadline and pid_alive(child_pid):
                time.sleep(0.3)
            check("child killed when the script exits", not pid_alive(child_pid), child_pid)

        print("timeout kills the whole tree")
        started = time.time()
        result = pd(root, "--json", "install", timeout_pkg, timeout=90)
        done = done_of(result.stdout)
        elapsed = time.time() - started
        check("timeout install exits 25", result.returncode == 25, (result.returncode, result.stdout[-500:]))
        check("timeout error code", (done or {}).get("error", {}).get("code") == "ERR_TIMEOUT", done)
        check("timeout did not take 60s", elapsed < 20, elapsed)
        timeout_pid = read_pid(os.path.join(root, "state", "tmp", "job-timeout-child.pid"))
        check("timeout child pid recorded", timeout_pid is not None, timeout_pid)
        if timeout_pid:
            deadline = time.time() + 10
            while time.time() < deadline and pid_alive(timeout_pid):
                time.sleep(0.3)
            check("timeout killed the spawned child", not pid_alive(timeout_pid), timeout_pid)
        check("failed timeout left no record", not os.path.isfile(os.path.join(root, "state", "records", "jobtimeout.json")))
        result = pd(root, "--json", "list")
        done = done_of(result.stdout)
        names = [item.get("package") for item in ((done or {}).get("metrics") or {}).get("packages", [])]
        check("list only has the successful packages", result.returncode == 0 and "jobtimeout" not in names, names)
    finally:
        stop_kernel(root)
        log.close()
        kill_leftovers(root)

    print(f"\npassed {PASS}, failed {FAIL}")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
