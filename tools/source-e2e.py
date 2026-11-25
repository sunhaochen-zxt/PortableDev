#!/usr/bin/env python3
"""Source & index e2e: sources.json, dir/http sources, version resolution, yanked, offline, search."""
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
        print(f"  FAIL {name} {str(detail)[:300]}")


def pd(root, *args, timeout=60):
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
    if os.name != "nt":
        return
    marker = os.path.abspath(root).replace("'", "''")
    script = (
        "$marker='" + marker + "'; "
        "Get-CimInstance Win32_Process | Where-Object { "
        "($_.Name -eq 'node.exe' -or $_.Name -eq 'dotnet.exe' -or $_.Name -eq 'pd-kernel.exe') -and $_.CommandLine -and "
        "($_.CommandLine -like ('*' + $marker + '*')) "
        "} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


def make_pkg(path, pid, version, body):
    manifest = {"schema": 1, "id": pid, "version": version, "platform": "win-x64"}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("package.json", json.dumps(manifest))
        archive.writestr("files/bin/" + pid + ".txt", body)


def prepare(root):
    builtins = ["storage", "rollback", "download", "unpack", "pkgmgr"]
    for name in builtins:
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
    for name in ("sources.json",):
        try:
            os.remove(os.path.join(root, "config", name))
        except OSError:
            pass


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


def record(root, pkg):
    path = os.path.join(root, "state", "records", pkg + ".json")
    if not os.path.isfile(path):
        return None
    return json.load(open(path, encoding="utf-8"))


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)

    work = os.path.join(root, "srcpkgs")
    shutil.rmtree(work, ignore_errors=True)
    dir_a = os.path.join(work, "dir-a")
    dir_b = os.path.join(work, "dir-b")
    http_dir = os.path.join(work, "http")
    bad_dir = os.path.join(work, "bad")
    for directory in (dir_a, dir_b, http_dir, bad_dir):
        os.makedirs(directory, exist_ok=True)

    make_pkg(os.path.join(dir_a, "demo-1.0.0.pdpkg"), "demo", "1.0.0", "demo 1")
    make_pkg(os.path.join(dir_a, "demo-2.0.0.pdpkg"), "demo", "2.0.0", "demo 2")
    make_pkg(os.path.join(dir_a, "onlydir-1.0.0.pdpkg"), "onlydir", "1.0.0", "only dir")
    make_pkg(os.path.join(dir_b, "demo-3.0.0.pdpkg"), "demo", "3.0.0", "demo 3")
    make_pkg(os.path.join(http_dir, "webonly-1.0.0.pdpkg"), "webonly", "1.0.0", "web 1")
    make_pkg(os.path.join(http_dir, "webonly2-1.0.0.pdpkg"), "webonly2", "1.0.0", "web 2")

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=http_dir, **kwargs)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    kernel, log = start_kernel(root)
    try:
        print("index build / inspect")
        result = pd(root, "--json", "index", "build", dir_a, "--name", "dira", "--out", os.path.join(dir_a, "index.json"))
        done = done_of(result.stdout)
        check("index build exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("index build counted packages", (done or {}).get("metrics", {}).get("packages") == 2, done)
        check("index file written", os.path.isfile(os.path.join(dir_a, "index.json")), None)
        pd(root, "--json", "index", "build", dir_b, "--name", "dirb", "--out", os.path.join(dir_b, "index.json"))
        pd(root, "--json", "index", "build", http_dir, "--name", "web", "--out", os.path.join(http_dir, "index.json"), "--priority", "200")

        result = pd(root, "--json", "index", "inspect", os.path.join(dir_a, "index.json"))
        done = done_of(result.stdout)
        check("index inspect exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("index inspect lists demo", (done or {}).get("metrics", {}).get("packages") == 2, done)

        # Make demo@2.0.0 yanked and keep it as latest, so the default path must skip it.
        index_a = json.load(open(os.path.join(dir_a, "index.json"), encoding="utf-8"))
        index_a["packages"]["demo"]["latest"] = "2.0.0"
        index_a["packages"]["demo"]["versions"]["2.0.0"]["yanked"] = True
        index_a["packages"]["demo"]["versions"]["2.0.0"]["yankedReason"] = "broken"
        json.dump(index_a, open(os.path.join(dir_a, "index.json"), "w", encoding="utf-8"))

        print("sources")
        result = pd(root, "--json", "source", "list")
        check("source list exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "source", "add", "dira", dir_a, "--priority", "100")
        check("source add dirA", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "source", "add", "dirb", dir_b, "--priority", "50")
        check("source add dirB", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "source", "add", "web", f"http://127.0.0.1:{port}/index.json", "--kind", "http", "--priority", "200")
        check("source add http", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "source", "refresh", "web")
        check("source refresh web", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("source index cached", os.path.isfile(os.path.join(root, "state", "sources", "web", "index.json")), None)

        print("search")
        result = pd(root, "--json", "search", "demo")
        done = done_of(result.stdout)
        check("search exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("search finds demo", (done or {}).get("metrics", {}).get("matches", 0) >= 1, done)
        check("search prefers higher priority source", "dirb" in result.stdout, result.stdout[-400:])

        print("install by name")
        result = pd(root, "--json", "install", "demo")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("install demo exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("install demo from dirB", metrics.get("source") == "dirb" and record(root, "demo").get("version") == "3.0.0", metrics)

        result = pd(root, "--json", "install", "demo@1.0.0", "--source", "dira")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("install pinned version", result.returncode == 0 and record(root, "demo").get("version") == "1.0.0", (metrics, record(root, "demo")))
        check("pinned install source", metrics.get("source") == "dira", metrics)

        result = pd(root, "--json", "install", "demo@^1", "--source", "dira")
        check("install range ^1", result.returncode == 0 and record(root, "demo").get("version") == "1.0.0", result.stdout[-300:])

        print("yanked")
        result = pd(root, "--json", "install", "demo@2.0.0", "--source", "dira")
        done = done_of(result.stdout)
        check("yanked rejected", result.returncode == 22 and (done or {}).get("error", {}).get("code") == "ERR_PKG_YANKED", done)
        result = pd(root, "--json", "install", "demo@2.0.0", "--source", "dira", "--force")
        check("yanked with --force", result.returncode == 0 and record(root, "demo").get("version") == "2.0.0", result.stdout[-300:])

        print("http + offline")
        result = pd(root, "--json", "fetch", "webonly@1.0.0")
        done = done_of(result.stdout)
        check("fetch http package", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        metrics = (done or {}).get("metrics") or {}
        check("fetch placed blob", os.path.isfile(metrics.get("path", "")), metrics)
        result = pd(root, "--json", "install", "webonly@1.0.0", "--offline")
        done = done_of(result.stdout)
        check("offline install from cached blob", result.returncode == 0 and (done or {}).get("metrics", {}).get("source") == "web", done)
        result = pd(root, "--json", "install", "webonly2@1.0.0", "--offline")
        done = done_of(result.stdout)
        check("offline miss", result.returncode == 23 and (done or {}).get("error", {}).get("code") == "ERR_OFFLINE_MISS", done)

        print("source priority / disable")
        result = pd(root, "--json", "source", "disable", "dirb")
        check("source disable", result.returncode == 0, result.stdout[-300:])
        result = pd(root, "--json", "install", "demo")
        check("install skips disabled source and yanked latest",
              result.returncode == 0 and record(root, "demo").get("version") == "1.0.0", result.stdout[-400:])

        print("invalid index")
        open(os.path.join(bad_dir, "index.json"), "w", encoding="utf-8").write("{ not json")
        result = pd(root, "--json", "source", "add", "bad", bad_dir)
        check("source add bad dir", result.returncode == 0, result.stdout[-300:])
        result = pd(root, "--json", "source", "refresh", "bad")
        done = done_of(result.stdout)
        check("invalid index rejected", result.returncode == 3 and (done or {}).get("error", {}).get("code") == "ERR_INDEX_INVALID", done)

        print("unknown package / remove")
        result = pd(root, "--json", "install", "nosuchpkg")
        done = done_of(result.stdout)
        check("unknown package 20", result.returncode == 20 and (done or {}).get("error", {}).get("code") == "ERR_PKG_NOT_FOUND", done)
        result = pd(root, "--json", "source", "remove", "dirb")
        check("source remove", result.returncode == 0, result.stdout[-300:])
        result = pd(root, "--json", "source", "list")
        check("removed source gone", "dirb" not in result.stdout, result.stdout[-400:])

        print("dry-run")
        before = record(root, "demo")
        result = pd(root, "--json", "install", "demo", "--dry-run")
        done = done_of(result.stdout)
        check("dry-run by name exit 0", result.returncode == 0 and (done or {}).get("metrics", {}).get("dryRun") is True, done)
        check("dry-run changed nothing", record(root, "demo") == before, record(root, "demo"))
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