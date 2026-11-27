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


def make_pkg(path, pid, version, body, extra=None, files=None):
    manifest = {"schema": 1, "id": pid, "version": version, "platform": "win-x64"}
    if extra:
        manifest.update(extra)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("package.json", json.dumps(manifest))
        archive.writestr("files/bin/" + pid + ".txt", body)
        for rel, content in (files or {}).items():
            archive.writestr("files/" + rel, content)


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
    # Isolate this run from other suites/leftover kernels on the same volume: a random persisted
    # stick id gives this root its own pipe/mutex, and fake wire mode keeps machine state virtual.
    wire_dir = os.path.join(root, "state", "wire")
    os.makedirs(wire_dir, exist_ok=True)
    with open(os.path.join(wire_dir, "fake-mode"), "w", encoding="utf-8") as handle:
        handle.write("1\n")
    os.makedirs(os.path.join(root, "state"), exist_ok=True)
    with open(os.path.join(root, "state", "stick.json"), "w", encoding="utf-8") as handle:
        json.dump({"schema": 1, "stickId": "pd-" + os.urandom(6).hex(), "source": "manual",
                   "volumeSerial": "", "label": ""}, handle)


def start_kernel(root):
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    process = subprocess.Popen([DOTNET, KERNEL_DLL, "--root", root], stdout=log, stderr=subprocess.STDOUT, cwd=os.getcwd())
    deadline = time.time() + 20
    while time.time() < deadline:
        probe = pd(root, "version", timeout=15)
        if probe.returncode == 0:
            # A stale kernel from an earlier run can answer the probe while our process exits busy.
            time.sleep(0.4)
            if process.poll() is not None:
                raise SystemExit("kernel exited right after a successful probe (stale kernel on this root?)")
            return process, log
        if process.poll() is not None:
            raise SystemExit("kernel exited during startup")
        time.sleep(0.3)
    raise SystemExit("kernel never became reachable")


def stop_kernel(root):
    pd(root, "kernel", "stop", timeout=30)
    time.sleep(0.8)


def write_fake_state(root, state):
    path = os.path.join(root, "state", "wire", "fake.json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(state, handle)


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


def fake_state(root):
    path = os.path.join(root, "state", "wire", "fake.json")
    if not os.path.isfile(path):
        return {"path": "", "env": {}, "registry": {}, "launchers": {}, "links": {}, "copies": {}}
    return json.load(open(path, encoding="utf-8"))


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
    # persist: install-dir data directories live on the stick.
    make_pkg(os.path.join(work, "persistdemo-1.0.0.pdpkg"), "persistdemo", "1.0.0", "persist demo",
             {"persist": ["data"]}, files={"data/seed.txt": "seed data\n"})
    make_pkg(os.path.join(work, "badpersist-1.0.0.pdpkg"), "badpersist", "1.0.0", "bad persist",
             {"persist": ["..\\escape"]})

    # Wiring/home fixture (fake machine: apply creates a virtual home junction).
    make_pkg(os.path.join(work, "wiredhome-1.0.0.pdpkg"), "wiredhome", "1.0.0", "wired home", {
        "path": ["bin"],
        "env": {"WIRED_HOME": "{{HOME}}"},
        "registry": [{"hive": "HKCU", "key": "Software\\PortableDevManage", "name": "Mark",
                      "type": "REG_SZ", "value": "1"}],
        "launch": [{"name": "wiredhome", "exe": "bin\\wiredhome.txt"}],
    })
    # flaky 2.0.0 fails its assertions, to exercise upgrade install-failure rollback.
    make_pkg(os.path.join(work, "flaky-1.0.0.pdpkg"), "flaky", "1.0.0", "flaky 1")
    make_pkg(os.path.join(work, "flaky-2.0.0.pdpkg"), "flaky", "2.0.0", "flaky 2", {
        "asserts": [{"kind": "exists", "path": "bin/absent.txt"}],
    })
    make_pkg(os.path.join(work, "copied-1.0.0.pdpkg"), "copied", "1.0.0", "copied", {
        "data": [{"kind": "copy", "source": "{{STICK}}\\\\home\\\\copied\\\\seed",
                  "target": "{{HOME}}\\\\copy"}],
    })

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
        check("upgrade exposes its audit task", metrics.get("packages") and metrics["packages"][0].get("task"), metrics)
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
        check("remove exposes its audit task", metrics.get("removed") and metrics["removed"][0].get("task"), metrics)

        result = pd(root, "--json", "list")
        done = done_of(result.stdout)
        check("list is empty after removals", result.returncode == 0 and (done or {}).get("metrics", {}).get("packages") == [], done)


        print("upgrade install-failure rollback")
        result = pd(root, "--json", "install", "flaky@1.0.0", "--source", "fixtures")
        check("install flaky 1.0.0", result.returncode == 0 and record(root, "flaky").get("version") == "1.0.0",
              (result.returncode, result.stdout[-300:]))
        flaky_target = current_target(root, "flaky")
        flaky_gens = generation_count(root)
        result = pd(root, "--json", "upgrade", "flaky")
        done = done_of(result.stdout)
        check("failing upgrade exits 12", result.returncode == 12, (result.returncode, result.stdout[-400:]))
        check("failed upgrade keeps the old version", record(root, "flaky").get("version") == "1.0.0", record(root, "flaky"))
        check("failed upgrade keeps the current pointer", current_target(root, "flaky") == flaky_target,
              (flaky_target, current_target(root, "flaky")))
        check("failed upgrade writes no generation", generation_count(root) == flaky_gens, (flaky_gens, generation_count(root)))
        check("failed upgrade rollback is not partial", "rollback partial" not in str((done or {}).get("error")), done)
        check("failed upgrade points at pd gc", "pd gc" in result.stdout, result.stdout[-500:])
        result = pd(root, "--json", "remove", "flaky")
        check("remove flaky", result.returncode == 0, (result.returncode, result.stdout[-300:]))


        print("atomic batch upgrade")
        result = pd(root, "--json", "install", "demo@1.0.0", "--source", "fixtures", "--force")
        check("demo forced to 1.0.0 for the atomic case", result.returncode == 0 and record(root, "demo").get("version") == "1.0.0", (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "install", "flaky@1.0.0", "--source", "fixtures", "--force")
        check("flaky forced to 1.0.0 for the atomic case", result.returncode == 0 and record(root, "flaky").get("version") == "1.0.0", (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "upgrade", "--all", "--atomic")
        done = done_of(result.stdout)
        check("atomic batch exits 12 on the broken package", result.returncode == 12, (result.returncode, result.stdout[-500:]))
        check("atomic rolled demo back to 1.0.0", record(root, "demo") and record(root, "demo").get("version") == "1.0.0", record(root, "demo"))
        check("atomic left flaky at 1.0.0", record(root, "flaky") and record(root, "flaky").get("version") == "1.0.0", record(root, "flaky"))
        check("atomic failure mentions the rollback", "atomic" in str((done or {}).get("error")) or "atomic" in result.stdout, (done, result.stdout[-400:]))
        result = pd(root, "--json", "remove", "demo", "flaky")
        check("atomic fixtures removed", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("persist user data")
        result = pd(root, "--json", "install", "persistdemo@1.0.0", "--source", "fixtures")
        check("install persist package", result.returncode == 0 and record(root, "persistdemo") is not None,
              (result.returncode, result.stdout[-400:]))
        persisted = record(root, "persistdemo")
        store_data = os.path.join(persisted["storePath"], "data") if persisted else ""
        stick_data = os.path.join(root, "home", "persistdemo", "persist", "data")
        check("store data is a junction to the stick",
              bool(store_data) and os.path.isdir(store_data) and os.path.realpath(store_data) == os.path.realpath(stick_data),
              (store_data, stick_data))
        check("seed data migrated into the stick dir", os.path.isfile(os.path.join(stick_data, "seed.txt")),
              os.listdir(stick_data) if os.path.isdir(stick_data) else None)
        with open(os.path.join(store_data, "new.txt"), "w", encoding="utf-8") as handle:
            handle.write("keep me")
        check("writes through the store land on the stick", os.path.isfile(os.path.join(stick_data, "new.txt")), stick_data)
        result = pd(root, "--json", "remove", "persistdemo", "--keep-data")
        check("remove --keep-data keeps persisted data", result.returncode == 0 and os.path.isfile(os.path.join(stick_data, "new.txt")),
              (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "install", "persistdemo@1.0.0", "--source", "fixtures")
        check("reinstall succeeds", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        rebound = record(root, "persistdemo")
        check("reinstall rebinds the persisted data",
              rebound is not None and os.path.isfile(os.path.join(rebound["storePath"], "data", "new.txt")),
              rebound)
        result = pd(root, "--json", "install", "badpersist@1.0.0", "--source", "fixtures")
        check("persist path escaping the store is rejected (exit 3)",
              result.returncode == 3 and "persist" in result.stdout, (result.returncode, result.stdout[-400:]))
        check("rejected persist package left no record", record(root, "badpersist") is None, record(root, "badpersist"))
        result = pd(root, "--json", "remove", "persistdemo")
        check("persist fixture removed", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("cross-machine record must not restore another machine's old values")
        result = pd(root, "--json", "install", "wiredhome@1.0.0", "--source", "fixtures")
        check("install wiredhome for cross-machine case", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        record_path = os.path.join(root, "state", "records", "wiredhome.json")
        record_data = json.load(open(record_path, encoding="utf-8"))
        before = record_data.setdefault("wiring", {}).setdefault("before", {})
        before["machine"] = "machine-a"
        before.setdefault("env", {})["WIRED_HOME"] = "from-machine-a"
        with open(record_path, "w", encoding="utf-8") as handle:
            json.dump(record_data, handle)
        state = fake_state(root)
        state.setdefault("env", {})["WIRED_HOME"] = "machine-b-current"
        write_fake_state(root, state)

        result = pd(root, "--json", "clean")
        check("clean cross-machine exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("cross-machine clean removes wiring instead of restoring foreign values",
              "WIRED_HOME" not in fake_state(root).get("env", {}), fake_state(root).get("env"))
        result = pd(root, "--json", "remove", "wiredhome")
        check("remove cross-machine record", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("data.copy remove / rollback")
        seed = os.path.join(root, "home", "copied", "seed")
        os.makedirs(seed, exist_ok=True)
        with open(os.path.join(seed, "data.txt"), "w", encoding="utf-8") as handle:
            handle.write("copy me\n")
        result = pd(root, "--json", "install", "copied@1.0.0", "--source", "fixtures")
        check("install copied", result.returncode == 0 and record(root, "copied") is not None,
              (result.returncode, result.stdout[-300:]))
        copy_keys = [key for key in (fake_state(root).get("copies") or {}) if os.path.normcase(key).endswith(os.path.normcase(os.sep + "copy"))]
        check("data copy recorded", bool(copy_keys), fake_state(root).get("copies"))
        result = pd(root, "--json", "remove", "copied")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("remove copied exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("data copy removed on unwire", not any(os.path.normcase(key).endswith(os.path.normcase(os.sep + "copy"))
                                                      for key in (fake_state(root).get("copies") or {})),
              fake_state(root).get("copies"))
        task = (metrics.get("removed") or [{}])[0].get("task")
        result = pd(root, "--json", "rollback", "--task", task or "missing")
        check("rollback of data.copy removal exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        copy_keys = [key for key in (fake_state(root).get("copies") or {}) if os.path.normcase(key).endswith(os.path.normcase(os.sep + "copy"))]
        check("rollback restored the data copy", bool(copy_keys), fake_state(root).get("copies"))
        result = pd(root, "--json", "remove", "copied", "--purge")
        check("cleanup copied", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("home package: remove keeps data, purge is rollback-able")
        result = pd(root, "--json", "install", "wiredhome@1.0.0", "--source", "fixtures")
        check("install wiredhome", result.returncode == 0 and record(root, "wiredhome") is not None,
              (result.returncode, result.stdout[-300:]))
        stick_home = os.path.join(root, "home", "wiredhome")
        check("stick home created", os.path.isdir(stick_home), stick_home)
        home_suffix = os.path.normcase(os.sep + "home" + os.sep + "wiredhome")
        links = [os.path.normcase(key) for key in (fake_state(root).get("links") or {})]
        check("fake machine home junction recorded", any(key.endswith(home_suffix) for key in links), links)
        with open(os.path.join(stick_home, "user.txt"), "w", encoding="utf-8") as handle:
            handle.write("keep me\n")

        result = pd(root, "--json", "remove", "wiredhome")
        check("remove home package exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("remove default keeps stick home", os.path.isfile(os.path.join(stick_home, "user.txt")), stick_home)
        links = [os.path.normcase(key) for key in (fake_state(root).get("links") or {})]
        check("remove default detaches the home junction", not any(key.endswith(home_suffix) for key in links), links)

        result = pd(root, "--json", "install", "wiredhome@1.0.0", "--source", "fixtures")
        check("reinstall wiredhome", result.returncode == 0 and record(root, "wiredhome") is not None,
              (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "remove", "wiredhome", "--purge")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("remove --purge home package exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("--purge moved the stick home away", not os.path.exists(stick_home), stick_home)
        trash = (metrics.get("removed") or [{}])[0].get("homeTrash")
        check("--purge keeps the data in state/trash", trash and os.path.isdir(trash), trash)
        task = (metrics.get("removed") or [{}])[0].get("task")
        check("remove --purge exposes its audit task", bool(task), metrics)

        result = pd(root, "--json", "rollback", "--task", task or "missing")
        check("rollback of purge exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("rollback restored the record", record(root, "wiredhome") is not None, record(root, "wiredhome"))
        check("rollback restored the home data", os.path.isfile(os.path.join(stick_home, "user.txt")), stick_home)
        check("rollback restored the current pointer", current_target(root, "wiredhome") is not None,
              current_target(root, "wiredhome"))
        links = [os.path.normcase(key) for key in (fake_state(root).get("links") or {})]
        check("rollback restored the home junction", any(key.endswith(home_suffix) for key in links), links)
        result = pd(root, "--json", "remove", "wiredhome", "--purge")
        check("cleanup wiredhome", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("argument edge cases")
        result = pd(root, "--json", "remove", "nosuchpkg", "--dry-run")
        check("remove --dry-run of a missing package exits 20", result.returncode == 20, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "upgrade", "demo")
        check("upgrade of a missing package on an empty set exits 20", result.returncode == 20, (result.returncode, result.stdout[-300:]))


        print("partial batches, pinning and missing argument values")
        result = pd(root, "--json", "install", "demo@1.0.0", "--source", "fixtures")
        check("install demo for partial upgrade", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "upgrade", "demo", "nosuchpkg")
        check("partial upgrade exits 20", result.returncode == 20, (result.returncode, result.stdout[-400:]))
        check("partial upgrade still updates the existing package", record(root, "demo").get("version") == "2.0.0",
              record(root, "demo"))
        result = pd(root, "--json", "install", "demo@2.0.0", "--source", "fixtures", "--pin")
        check("install --pin exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("record is pinned", record(root, "demo").get("pinned") is True, record(root, "demo"))
        result = pd(root, "--json", "upgrade", "--all")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("upgrade --all skips pinned packages", result.returncode == 0 and metrics.get("upgraded") == 0 and metrics.get("skipped", 0) >= 1,
              (result.returncode, metrics))
        result = pd(root, "--json", "upgrade", "demo", "--to", "1.0.0")
        check("explicit upgrade of a pinned package still works",
              result.returncode == 0 and record(root, "demo").get("version") == "1.0.0",
              (result.returncode, record(root, "demo"), result.stdout[-300:]))
        check("explicit upgrade keeps the pin", record(root, "demo").get("pinned") is True, record(root, "demo"))
        result = pd(root, "--json", "list")
        done = done_of(result.stdout)
        packages = (done or {}).get("metrics", {}).get("packages") or []
        check("list reports pinned", packages and packages[0].get("pinned") is True, packages)
        result = pd(root, "--json", "upgrade", "demo", "--to")
        check("missing flag value exits 2", result.returncode == 2, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "install", "other@1.0.0", "--source", "fixtures")
        check("install other for partial remove", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        result = pd(root, "--json", "remove", "other", "nosuchpkg")
        check("partial remove exits 20", result.returncode == 20, (result.returncode, result.stdout[-400:]))
        check("partial remove still removed the existing package", record(root, "other") is None, record(root, "other"))
        result = pd(root, "--json", "remove", "demo", "--purge")
        check("cleanup demo after partial tests", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("partial settings must not collapse generation retention")
        with open(os.path.join(root, "config", "settings.json"), "w", encoding="utf-8") as handle:
            json.dump({"install": {"maxConcurrent": 2}}, handle)
        base_gens = generation_count(root)
        check("install under partial settings", pd(root, "--json", "install", "demo@1.0.0", "--source", "fixtures").returncode == 0)
        check("second install under partial settings", pd(root, "--json", "install", "other@1.0.0", "--source", "fixtures").returncode == 0)
        result = pd(root, "--json", "upgrade", "demo", "--to", "1.0.0")
        check("--to reinstall under partial settings", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("partial settings keeps more than one generation", generation_count(root) >= base_gens + 3,
              (base_gens, generation_count(root)))
        pd(root, "--json", "remove", "demo", "--purge")
        pd(root, "--json", "remove", "other", "--purge")

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
