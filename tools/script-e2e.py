#!/usr/bin/env python3
"""Package script e2e: restricted request protocol, policy, timeout, wire primitives from scripts."""
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import zipfile
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
KERNEL_DLL = os.path.abspath(os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64", "pd-kernel.dll"))
PD_DLL = os.path.abspath(os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll"))

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


def pd(root, *args, timeout=90):
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


def kill_leftovers(root):
    """Stop only this test root's kernel/extensions. Match both the absolute marker and the exact
    --root argument, otherwise kernels started with a relative root survive and poison later runs."""
    if os.name != "nt":
        return
    marker = os.path.abspath(root).replace("'", "''")
    root_arg = str(root).replace("'", "''")
    script = (
        "$marker='" + marker + "'; $rootArg='" + root_arg + "'; "
        "Get-CimInstance Win32_Process | Where-Object { "
        "($_.Name -eq 'node.exe' -or $_.Name -eq 'dotnet.exe' -or $_.Name -eq 'pd-kernel.exe') -and $_.CommandLine -and "
        "(($_.CommandLine -like ('*' + $marker + '*')) -or ($_.CommandLine -like ('*--root*' + $rootArg + '*'))) "
        "} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


def make_dir_pkg(root, name, pid, version, script=None, script_meta=None, files=None):
    directory = os.path.join(root, "scriptpkgs", name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files", "bin"), exist_ok=True)
    manifest = {"schema": 1, "id": pid, "version": version, "platform": "win-x64"}
    if script is not None:
        effective = script_meta or {"entry": "install.py", "interpreter": "python", "on": ["install"]}
        manifest["script"] = effective
        with open(os.path.join(directory, effective.get("entry", "install.py")), "w", encoding="utf-8") as handle:
            handle.write(script)
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    with open(os.path.join(directory, "files", "bin", "base.txt"), "w", encoding="utf-8") as handle:
        handle.write("base " + pid + " " + version)
    for rel, content in (files or {}).items():
        full = os.path.join(directory, "files", rel)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8") as handle:
            handle.write(content)
    return directory


def make_zip_pkg(root, name, pid, version, script=None, script_meta=None, files=None):
    path = os.path.join(root, "scriptpkgs", name + ".pdpkg")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    manifest = {"schema": 1, "id": pid, "version": version, "platform": "win-x64"}
    if script is not None:
        manifest["script"] = script_meta or {"entry": "install.py", "interpreter": "python", "on": ["install"]}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("package.json", json.dumps(manifest))
        archive.writestr("files/bin/base.txt", "base " + pid)
        if script is not None:
            archive.writestr("install.py", script)
        for rel, content in (files or {}).items():
            archive.writestr("files/" + rel, content)
    return path


def prepare(root):
    for name in ["storage", "rollback", "download", "unpack", "pkgmgr", "proc"]:
        target = os.path.join(root, "extensions", name)
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(os.path.join("app", "extensions", name), target)
    log_target = os.path.join(root, "extensions", "log")
    shutil.rmtree(log_target, ignore_errors=True)
    shutil.copytree(os.path.join("tests", "fixtures", "log-ext"), log_target)
    for directory in ("store", "current", "staging", "packages"):
        shutil.rmtree(os.path.join(root, directory), ignore_errors=True)
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent", "wire", "sources", "tmp", "logs"):
        shutil.rmtree(os.path.join(root, "state", directory), ignore_errors=True)
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)
    os.makedirs(os.path.join(root, "config"), exist_ok=True)
    wire_dir = os.path.join(root, "state", "wire")
    os.makedirs(wire_dir, exist_ok=True)
    with open(os.path.join(wire_dir, "fake-mode"), "w", encoding="utf-8") as handle:
        handle.write("1\n")
    # Fake elevation: the helper runs with --fake (no UAC) but still executes process.run, so the
    # elevated-script path is exercised without an approval prompt.
    with open(os.path.join(wire_dir, "elevate-fake.json"), "w", encoding="utf-8") as handle:
        handle.write("{}\n")
    helper_source = os.path.join("src", "PortableDev.Elevate", "bin", "Debug", "net8.0", "win-x64")
    helper_target = os.path.join(root, "app", "tools")
    shutil.rmtree(helper_target, ignore_errors=True)
    os.makedirs(helper_target, exist_ok=True)
    for name in ("pd-elevate.exe", "pd-elevate.dll", "pd-elevate.deps.json", "pd-elevate.runtimeconfig.json"):
        shutil.copy2(os.path.join(helper_source, name), os.path.join(helper_target, name))


def start_kernel(root):
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    process = subprocess.Popen([DOTNET, KERNEL_DLL, "--root", root], stdout=log, stderr=subprocess.STDOUT, cwd=os.getcwd())
    deadline = time.time() + 20
    while time.time() < deadline:
        probe = pd(root, "version", timeout=15)
        if probe.returncode == 0:
            return process, log
        if process.poll() is not None:
            raise SystemExit("kernel exited during startup")
        time.sleep(0.3)
    raise SystemExit("kernel never became reachable")


def stop_kernel(root):
    pd(root, "kernel", "stop", timeout=30)
    time.sleep(0.8)


def fake_state(root):
    path = os.path.join(root, "state", "wire", "fake.json")
    if not os.path.isfile(path):
        return {"path": None, "env": {}, "registry": {}, "launchers": {}, "links": {}}
    return json.load(open(path, encoding="utf-8"))


SCRIPT_WRITE = """import json, sys
def ask(cmd, args):
    print(json.dumps({"type": "request", "id": str(id(args)), "cmd": cmd, "args": args}), flush=True)
    return json.loads(sys.stdin.readline())
r = ask("write", {"path": "bin/created.txt", "content": "made by script"})
assert r.get("ok"), r
r = ask("assert", {"kind": "exists", "path": "bin/created.txt"})
assert r.get("ok"), r
"""

SCRIPT_WIRE = """import json, sys
def ask(cmd, args):
    print(json.dumps({"type": "request", "id": "w" + str(len(args)), "cmd": cmd, "args": args}), flush=True)
    return json.loads(sys.stdin.readline())
assert ask("env.set", {"name": "SCRIPT_MARK", "value": "on"}).get("ok")
assert ask("reg.set", {"hive": "HKCU", "key": "Software\\\\PortableDevScript", "name": "Mark", "type": "REG_SZ", "value": "1"}).get("ok")
assert ask("link", {"source": "bin", "target": "{{ROOT}}\\\\scriptlink"}).get("ok")
"""

SCRIPT_BAD = """import json, sys
print(json.dumps({"type": "request", "id": "1", "cmd": "reboot", "args": {}}), flush=True)
sys.stdin.readline()
"""

SCRIPT_SLEEP = """import time
time.sleep(30)
"""

SCRIPT_ASSERT_FAIL = """import json, sys
print(json.dumps({"type": "request", "id": "1", "cmd": "assert", "args": {"kind": "exists", "path": "bin/nope.txt"}}), flush=True)
sys.stdin.readline()
"""

SCRIPT_KERNEL_CHECK = """import json, os, sys
assert "PD_PIPE" not in os.environ, "script must not see the kernel pipe"
print(json.dumps({"type": "request", "id": "1", "cmd": "write", "args": {"path": "bin/isolated.txt", "content": "ok"}}), flush=True)
assert json.loads(sys.stdin.readline()).get("ok")
"""

SCRIPT_FETCH_BLOCKED = """import json, sys
print(json.dumps({"type": "request", "id": "1", "cmd": "fetch", "args": {"url": "http://127.0.0.1:1/x", "path": "bin/x.bin"}}), flush=True)
sys.stdin.readline()
"""

SCRIPT_FETCH_OK = """import json, sys
print(json.dumps({"type": "request", "id": "1", "cmd": "fetch", "args": {"url": "%s", "path": "bin/fetched.txt"}}), flush=True)
assert json.loads(sys.stdin.readline()).get("ok")
"""


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)

    work = os.path.join(root, "scriptpkgs")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work, exist_ok=True)
    served = os.path.join(work, "served")
    os.makedirs(served, exist_ok=True)
    with open(os.path.join(served, "payload.txt"), "w", encoding="utf-8") as handle:
        handle.write("payload")

    local_scripted = make_dir_pkg(root, "scripted", "scripted", "1.0.0", SCRIPT_WRITE)
    make_dir_pkg(root, "noscripted", "noscripted", "1.0.0", SCRIPT_WRITE)
    wire_scripted = make_dir_pkg(root, "wiresscripted", "wiresscripted", "1.0.0", SCRIPT_WIRE)
    bad_scripted = make_dir_pkg(root, "badscripted", "badscripted", "1.0.0", SCRIPT_BAD)
    sleep_scripted = make_dir_pkg(root, "sleepscripted", "sleepscripted", "1.0.0", SCRIPT_SLEEP,
                                  {"entry": "install.py", "interpreter": "python", "on": ["install"], "timeout": "1s"})
    assert_fail = make_dir_pkg(root, "assertfail", "assertfail", "1.0.0", SCRIPT_ASSERT_FAIL)
    isolated = make_dir_pkg(root, "isolated", "isolated", "1.0.0", SCRIPT_KERNEL_CHECK)
    fetch_blocked = make_dir_pkg(root, "fetchblocked", "fetchblocked", "1.0.0", SCRIPT_FETCH_BLOCKED)
    fetch_ok = make_dir_pkg(root, "fetchok", "fetchok", "1.0.0", SCRIPT_FETCH_OK % ("http://127.0.0.1:PORT/payload.txt"),
                            {"entry": "install.py", "interpreter": "python", "on": ["install"], "network": True})
    # Fill the actual port later by rewriting the file (port is random).
    net_archive = make_zip_pkg(root, "netscripted", "netscripted", "1.0.0", SCRIPT_WRITE)

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=served, **kwargs)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    # Rewrite the network fetch script with the live port.
    with open(os.path.join(fetch_ok, "install.py"), "w", encoding="utf-8") as handle:
        handle.write(SCRIPT_FETCH_OK % ("http://127.0.0.1:%d/payload.txt" % port))

    kernel, log = start_kernel(root)
    try:
        print("local script runs")
        result = pd(root, "--json", "install", local_scripted)
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        store = metrics.get("storePath", "")
        check("scripted install exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("script write landed in the store", os.path.isfile(os.path.join(store, "bin", "created.txt")), store)

        print("--no-scripts")
        result = pd(root, "--json", "install", os.path.join(root, "scriptpkgs", "noscripted"), "--no-scripts")
        done = done_of(result.stdout)
        store = ((done or {}).get("metrics") or {}).get("storePath", "")
        check("no-scripts install exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("no-scripts skipped the script", not os.path.isfile(os.path.join(store, "bin", "created.txt")), store)

        print("wire requests from a script (fake machine)")
        result = pd(root, "--json", "install", wire_scripted)
        check("wire script install exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        state = fake_state(root)
        check("script env.set recorded", state.get("env", {}).get("SCRIPT_MARK") == "on", state.get("env"))
        check("script reg.set recorded",
              any(key.endswith("PortableDevScript\\Mark") and value.get("value") == "1"
                  for key, value in state.get("registry", {}).items()), state.get("registry"))
        links = state.get("links", {})
        check("script link recorded",
              any(os.path.normcase(key).endswith(os.path.normcase("scriptlink")) for key in links), links)

        print("kernel isolation")
        result = pd(root, "--json", "install", isolated)
        done = done_of(result.stdout)
        store = ((done or {}).get("metrics") or {}).get("storePath", "")
        check("script cannot see PD_PIPE", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("isolated script wrote its file", os.path.isfile(os.path.join(store, "bin", "isolated.txt")), store)

        print("network policy")
        net_hash = __import__("hashlib").sha256(open(net_archive, "rb").read()).hexdigest()
        url = f"http://127.0.0.1:{port}/netscripted.pdpkg"
        shutil.copyfile(net_archive, os.path.join(served, "netscripted.pdpkg"))
        result = pd(root, "--json", "install", url, "--sha256", net_hash)
        done = done_of(result.stdout)
        store = ((done or {}).get("metrics") or {}).get("storePath", "")
        check("network install without --allow-scripts", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("network script skipped by default", not os.path.isfile(os.path.join(store, "bin", "created.txt")), store)
        result = pd(root, "--json", "install", url, "--sha256", net_hash, "--allow-scripts")
        done = done_of(result.stdout)
        store = ((done or {}).get("metrics") or {}).get("storePath", "")
        check("network install with --allow-scripts", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("network script ran", os.path.isfile(os.path.join(store, "bin", "created.txt")), store)

        print("script failures")
        result = pd(root, "--json", "install", bad_scripted)
        done = done_of(result.stdout)
        check("unknown script command fails install",
              result.returncode != 0 and (done or {}).get("error", {}).get("code") == "ERR_SCRIPT", done)
        started = time.time()
        result = pd(root, "--json", "install", sleep_scripted, timeout=120)
        done = done_of(result.stdout)
        check("script timeout fails install",
              result.returncode == 25 and (done or {}).get("error", {}).get("code") == "ERR_TIMEOUT", done)
        check("script timeout killed promptly", time.time() - started < 30, time.time() - started)
        result = pd(root, "--json", "install", assert_fail)
        done = done_of(result.stdout)
        check("failed assert request fails install",
              result.returncode != 0 and (done or {}).get("error", {}).get("code") == "ERR_SCRIPT", done)

        print("fetch requires network:true")
        result = pd(root, "--json", "install", fetch_blocked)
        done = done_of(result.stdout)
        check("fetch without network:true fails", result.returncode != 0 and (done or {}).get("error", {}).get("code") == "ERR_SCRIPT", done)
        result = pd(root, "--json", "install", fetch_ok)
        done = done_of(result.stdout)
        store = ((done or {}).get("metrics") or {}).get("storePath", "")
        check("fetch with network:true works", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("fetched file landed", os.path.isfile(os.path.join(store, "bin", "fetched.txt")), store)
        print("script cpu quota")
        cpu_pkg = make_dir_pkg(root, "cpu", "cpu-script", "1.0.0",
            script="print('cpu limit ok')\n",
            script_meta={"entry": "install.py", "interpreter": "python", "on": ["install"], "cpu": 40})
        result = pd(root, "--json", "install", cpu_pkg)
        done = done_of(result.stdout)
        check("script with cpu limit installs", result.returncode == 0 and (done or {}).get("exitCode") == 0, (result.returncode, result.stdout[-300:]))
        bad_cpu = make_dir_pkg(root, "cpu-bad", "cpu-bad", "1.0.0",
            script="print('never')\n",
            script_meta={"entry": "install.py", "interpreter": "python", "on": ["install"], "cpu": 150})
        result = pd(root, "--json", "install", bad_cpu)
        done = done_of(result.stdout)
        check("cpu out of range rejected", result.returncode == 3
              and (done or {}).get("error", {}).get("code") == "ERR_PKG_INVALID", (result.returncode, result.stdout[-300:]))

        print("elevated script via the elevation helper")
        elevated = make_dir_pkg(root, "elevated", "elevated-script", "1.0.0",
            script="echo elevated > elevated-marker.txt\r\n",
            script_meta={"entry": "install.cmd", "interpreter": "cmd", "on": ["install"], "elevated": True})
        result = pd(root, "--json", "install", elevated)
        done = done_of(result.stdout)
        store = ((done or {}).get("metrics") or {}).get("storePath", "")
        check("elevated script installs", result.returncode == 0, (result.returncode, result.stdout[-500:]))
        check("elevated script ran through the helper", bool(store) and os.path.isfile(os.path.join(store, "elevated-marker.txt")), store)

    finally:
        server.shutdown()
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
