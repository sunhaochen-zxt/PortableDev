#!/usr/bin/env python3
"""Lifecycle e2e: pd list, pd upgrade (with --to / --all / failure), pd remove (--purge / --keep-data)."""
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile

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
    for directory in ("records", "intent", "wire", "sources", "tmp", "logs", "generations"):
        shutil.rmtree(os.path.join(root, "state", directory), ignore_errors=True)
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)
    os.makedirs(os.path.join(root, "config"), exist_ok=True)
    try:
        os.remove(os.path.join(root, "config", "sources.json"))
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


def current_target(root, pkg):
    link = os.path.join(root, "current", pkg)
    if not os.path.exists(link):
        return None
    try:
        return os.path.realpath(link)
    except OSError:
        return None


def generation_count(root):
    directory = os.path.join(root, "state", "generations")
    if not os.path.isdir(directory):
        return 0
    return len([name for name in os.listdir(directory) if name.endswith(".json")])


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)

    work = os.path.join(root, "srcpkgs")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work, exist_ok=True)
    make_pkg(os.path.join(work, "demo-1.0.0.pdpkg"), "demo", "1.0.0", "demo 1")
    make_pkg(os.path.join(work, "demo-2.0.0.pdpkg"), "demo", "2.0.0", "demo 2")
    make_pkg(os.path.join(work, "other-1.0.0.pdpkg"), "other", "1.0.0", "other 1")

    kernel, log = start_kernel(root)
    try:
        result = pd(root, "--json", "index", "build", work, "--name", "local", "--out", os.path.join(work, "index.json"))
        check("index build", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "source", "add", "fixtures", work, "--priority", "10")
        check("source add", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("install baseline")
        result = pd(root, "--json", "install", "demo@1.0.0", "--source", "fixtures")
        check("install demo 1.0.0", result.returncode == 0 and record(root, "demo").get("version") == "1.0.0",
              (result.returncode, result.stdout[-400:]))
        baseline = record(root, "demo")
        baseline_target = current_target(root, "demo")
        check("current points at the record store path", baseline_target and os.path.normcase(baseline_target) == os.path.normcase(os.path.realpath(baseline["storePath"])),
              (baseline_target, baseline.get("storePath")))
        generations = generation_count(root)
        check("install wrote a generation", generations >= 1, generations)

        print("list")
        result = pd(root, "--json", "list")
        done = done_of(result.stdout)
        packages = (done or {}).get("metrics", {}).get("packages") or []
        check("list exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("list shows demo 1.0.0", len(packages) == 1 and packages[0].get("package") == "demo" and packages[0].get("version") == "1.0.0", packages)
        check("list marks current", packages and packages[0].get("current") is True, packages)

        result = pd(root, "--json", "list", "--outdated")
        done = done_of(result.stdout)
        packages = (done or {}).get("metrics", {}).get("packages") or []
        check("list --outdated finds 2.0.0", result.returncode == 0 and packages and packages[0].get("latest") == "2.0.0", packages)

        print("upgrade planning")
        result = pd(root, "--json", "upgrade", "demo", "--dry-run")
        done = done_of(result.stdout)
        check("upgrade --dry-run exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("dry-run leaves the record alone", record(root, "demo").get("version") == "1.0.0", record(root, "demo"))
        check("dry-run plans 2.0.0", (done or {}).get("metrics", {}).get("packages", [{}])[0].get("target") == "2.0.0", done)

        result = pd(root, "--json", "upgrade", "demo", "--to", "9.9.9")
        check("upgrade to a missing version fails 20", result.returncode == 20, (result.returncode, result.stdout[-300:]))
        check("failed resolve leaves the record alone", record(root, "demo").get("version") == "1.0.0", record(root, "demo"))

        result = pd(root, "--json", "upgrade", "--all", "--to", "2.0.0")
        check("upgrade --all --to is rejected", result.returncode == 2, (result.returncode, result.stdout[-300:]))

        print("upgrade")
        result = pd(root, "--json", "upgrade", "demo")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("upgrade exit 0", result.returncode == 0, (result.returncode, result.stdout[-500:]))
        check("upgrade moved the record to 2.0.0", record(root, "demo").get("version") == "2.0.0", record(root, "demo"))
        upgraded_target = current_target(root, "demo")
        check("upgrade moved the current pointer", upgraded_target != baseline_target and os.path.normcase(upgraded_target) == os.path.normcase(os.path.realpath(record(root, "demo")["storePath"])),
              (baseline_target, upgraded_target))
        check("upgrade reports one package", metrics.get("upgraded") == 1 and metrics.get("failed") == 0, metrics)
        check("upgrade wrote exactly one generation", generation_count(root) == generations + 1, (generations, generation_count(root)))
        check("old store directory is kept until gc", os.path.isdir(baseline["storePath"]), baseline["storePath"])

        result = pd(root, "--json", "upgrade", "--all")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("upgrade --all is a no-op when current", result.returncode == 0 and metrics.get("upgraded") == 0 and metrics.get("skipped") == 1, (result.returncode, metrics))

        result = pd(root, "--json", "upgrade", "demo", "--to", "1.0.0")
        check("upgrade --to can downgrade", result.returncode == 0 and record(root, "demo").get("version") == "1.0.0",
              (result.returncode, record(root, "demo"), result.stdout[-400:]))

        print("remove")
        result = pd(root, "--json", "install", "other@1.0.0", "--source", "fixtures")
        check("install other", result.returncode == 0 and record(root, "other") is not None, result.stdout[-300:])
        other_home = os.path.join(root, "home", "other")
        os.makedirs(other_home, exist_ok=True)
        open(os.path.join(other_home, "persist.txt"), "w", encoding="utf-8").write("keep me\n")

        result = pd(root, "--json", "remove", "other", "--dry-run")
        check("remove --dry-run exit 0 and keeps everything", result.returncode == 0 and record(root, "other") is not None and os.path.isdir(other_home),
              (result.returncode, result.stdout[-300:]))

        result = pd(root, "--json", "remove", "other", "--purge")
        check("remove --purge exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("remove --purge deleted the record", record(root, "other") is None, record(root, "other"))
        check("remove --purge deleted the home directory", not os.path.exists(other_home), other_home)

        result = pd(root, "--json", "remove", "other")
        check("removing a missing package fails 20", result.returncode == 20, (result.returncode, result.stdout[-300:]))

        result = pd(root, "--json", "remove", "demo", "--keep-data")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("remove --keep-data exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("remove cleared the record", record(root, "demo") is None, record(root, "demo"))
        check("remove cleared the current pointer", current_target(root, "demo") is None, current_target(root, "demo"))
        check("remove reports the package", metrics.get("removed") and metrics["removed"][0].get("package") == "demo", metrics)

        result = pd(root, "--json", "list")
        done = done_of(result.stdout)
        check("list is empty after removals", result.returncode == 0 and (done or {}).get("metrics", {}).get("packages") == [], done)

        result = pd(root, "--json", "remove", "demo", "--purge", "--keep-data")
        check("--purge and --keep-data conflict is 2", result.returncode == 2, (result.returncode, result.stdout[-300:]))
    finally:
        stop_kernel(root)
        log.close()
        kill_leftovers(root)

    print()
    print(f"passed {PASS}, failed {FAIL}")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
