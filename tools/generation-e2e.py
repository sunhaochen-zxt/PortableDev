#!/usr/bin/env python3
"""End-to-end tests for generations: snapshot on apply, list, and rollback --to."""
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile

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


def pd(root, *argv, timeout=180):
    command = [DOTNET, PD_DLL, "--root", root] + list(argv)
    environment = dict(os.environ)
    environment.pop("PD_KERNEL", None)
    return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace",
                          env=environment, timeout=timeout)


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


def fake_state(root):
    path = os.path.join(root, "state", "wire", "fake.json")
    if not os.path.isfile(path):
        return {"path": None, "env": {}, "registry": {}, "launchers": {}}
    return json.load(open(path, encoding="utf-8"))


def record(root, pkg):
    path = os.path.join(root, "state", "records", pkg + ".json")
    if not os.path.isfile(path):
        return None
    return json.load(open(path, encoding="utf-8"))


def make_zip(path, entries):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)


def build_packages(packages):
    os.makedirs(packages, exist_ok=True)

    def package(pkg_id, version, env_name, env_value, marker_file):
        manifest = {
            "schema": 1, "id": pkg_id, "version": version, "platform": "win-x64",
            "path": ["bin"],
            "env": {env_name: env_value},
            "asserts": [{"kind": "exists", "path": "bin/" + marker_file}],
        }
        make_zip(os.path.join(packages, f"{pkg_id}-{version}.pdpkg"), {
            "package.json": json.dumps(manifest).encode(),
            f"files/bin/{marker_file}": f"{pkg_id} {version}\n".encode(),
        })

    package("pkga", "1.0.0", "A_MARK", "A", "a.txt")
    package("pkgb", "1.0.0", "B_MARK", "B1", "b.txt")
    package("pkgb", "2.0.0", "B_MARK", "B2", "b.txt")


def prepare(root):
    extensions = {
        "storage": os.path.join("app", "extensions", "storage"),
        "rollback": os.path.join("app", "extensions", "rollback"),
        "log": os.path.join("tests", "fixtures", "log-ext"),
        "download": os.path.join("app", "extensions", "download"),
        "unpack": os.path.join("app", "extensions", "unpack"),
        "pkgmgr": os.path.join("app", "extensions", "pkgmgr"),
    }
    for name, source in extensions.items():
        target = os.path.join(root, "extensions", name)
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(source, target)
    for directory in ("store", "current", "staging"):
        shutil.rmtree(os.path.join(root, directory), ignore_errors=True)
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent", "wire", "generations"):
        shutil.rmtree(os.path.join(root, "state", directory), ignore_errors=True)
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)
    shutil.rmtree(os.path.join(root, "state", "logs"), ignore_errors=True)
    os.makedirs(os.path.join(root, "state", "logs"), exist_ok=True)


def start_kernel(root):
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    process = subprocess.Popen([DOTNET, KERNEL_DLL, "--root", root], stdout=log, stderr=subprocess.STDOUT, cwd=os.getcwd())
    deadline = time.time() + 20
    while time.time() < deadline:
        if pd(root, "version", timeout=15).returncode == 0:
            return process, log
        if process.poll() is not None:
            raise SystemExit("kernel exited during startup")
        time.sleep(0.3)
    raise SystemExit("kernel never became reachable")


def stop_kernel(root):
    pd(root, "kernel", "stop", timeout=30)
    time.sleep(0.8)


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
        "(($_.CommandLine -like ('*' + $marker + '*')) -or ($_.CommandLine -like ('*--root*' + $rootArg + '*'))) "
        "} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


def install(root, packages, name):
    result = pd(root, "--json", "install", os.path.join(packages, name))
    return result, done_of(result.stdout)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)
    open(os.path.join(root, "state", "wire", "fake-mode"), "w", encoding="utf-8").write("1\n")
    packages = os.path.join(root, "gpkgs")
    build_packages(packages)

    kernel, log = start_kernel(root)
    try:
        print("install three generations")
        result, done = install(root, packages, "pkga-1.0.0.pdpkg")
        check("install A exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        gen_a = ((done or {}).get("metrics") or {}).get("storePath")
        result, done = install(root, packages, "pkgb-1.0.0.pdpkg")
        b1_store = ((done or {}).get("metrics") or {}).get("storePath")
        check("install B1 exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        result, done = install(root, packages, "pkgb-2.0.0.pdpkg")
        b2_store = ((done or {}).get("metrics") or {}).get("storePath")
        check("install B2 exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("record B is 2.0.0", (record(root, "pkgb") or {}).get("version") == "2.0.0", record(root, "pkgb"))
        check("env B_MARK is B2", fake_state(root).get("env", {}).get("B_MARK") == "B2", fake_state(root).get("env"))

        print("list generations")
        result = pd(root, "--json", "rollback", "--list")
        done = done_of(result.stdout)
        check("list exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        count = ((done or {}).get("metrics") or {}).get("generations", 0)
        check("three generations", count >= 3, count)

        print("rollback to the second generation")
        result = pd(root, "--json", "rollback", "--to", "2", "--dry-run")
        done = done_of(result.stdout)
        check("dry-run exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("dry-run did not change", (record(root, "pkgb") or {}).get("version") == "2.0.0", record(root, "pkgb"))

        result = pd(root, "--json", "rollback", "--to", "2")
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("restore exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:], result.stderr[-200:]))
        check("restore reports seq 2", metrics.get("seq") == 2 and metrics.get("packages") == 2, metrics)
        check("record B back to 1.0.0", (record(root, "pkgb") or {}).get("version") == "1.0.0", record(root, "pkgb"))
        check("env B_MARK back to B1", fake_state(root).get("env", {}).get("B_MARK") == "B1", fake_state(root).get("env"))
        check("current B points at B1", os.path.realpath(os.path.join(root, "current", "pkgb")) == os.path.realpath(b1_store),
              (os.path.realpath(os.path.join(root, "current", "pkgb")), b1_store))
        check("new generation appended", metrics.get("newSeq") is not None and metrics.get("newSeq") > 3, metrics)

        print("rollback to the first generation")
        result = pd(root, "--json", "rollback", "--to", "1")
        done = done_of(result.stdout)
        check("restore 1 exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("B record removed", record(root, "pkgb") is None, record(root, "pkgb"))
        check("B env removed", "B_MARK" not in fake_state(root).get("env", {}), fake_state(root).get("env"))
        check("B junction removed", not os.path.exists(os.path.join(root, "current", "pkgb")), None)
        check("A env kept", fake_state(root).get("env", {}).get("A_MARK") == "A", fake_state(root).get("env"))
        check("A store kept", os.path.isdir(gen_a), gen_a)
        check("B store kept (gc may clean later)", os.path.isdir(b1_store) and os.path.isdir(b2_store), (b1_store, b2_store))

        print("missing generation")
        result = pd(root, "--json", "rollback", "--to", "99")
        done = done_of(result.stdout)
        check("missing generation exit 20", result.returncode == 20, (result.returncode, result.stdout[-300:]))
        check("missing generation code", (done or {}).get("error", {}).get("code") == "ERR_PKG_NOT_FOUND", done)

        print("store removed by gc")
        shutil.rmtree(b1_store, ignore_errors=True)
        result = pd(root, "--json", "rollback", "--to", "2")
        done = done_of(result.stdout)
        check("missing store exit 20", result.returncode == 20, (result.returncode, result.stdout[-300:]))
        check("missing store code", (done or {}).get("error", {}).get("code") == "ERR_PKG_NOT_FOUND", done)

        print("corrupt generation")
        bogus_path = os.path.join(root, "state", "generations", "0090-bogus.json")
        with open(bogus_path, "w", encoding="utf-8") as handle:
            json.dump({
                "schema": 1, "seq": 90, "at": "2026-11-25T00:00:00+08:00",
                "packages": {
                    "../evil": {
                        "schema": 1, "id": "../evil", "version": "1.0.0",
                        "storePath": b2_store, "wiring": {},
                    },
                },
            }, handle)
        before_record = record(root, "pkga")
        result = pd(root, "--json", "rollback", "--to", "90")
        done = done_of(result.stdout)
        check("corrupt generation rejected", result.returncode == 3, (result.returncode, result.stdout[-300:]))
        check("corrupt generation code", (done or {}).get("error", {}).get("code") == "ERR_PKG_INVALID", done)
        check("corrupt generation changed nothing", record(root, "pkga") == before_record, record(root, "pkga"))
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
