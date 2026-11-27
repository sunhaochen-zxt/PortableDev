#!/usr/bin/env python3
"""End-to-end tests for the wiring layer: apply, clean, and real user-environment round trip."""
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


def pd(root, *argv, timeout=120):
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


def stable_root(root):
    try:
        settings = json.load(open(os.path.join(root, "config", "settings.json"), encoding="utf-8"))
        return settings.get("root", {}).get("stableRoot") or "C:\\Dev"
    except OSError:
        return "C:\\Dev"


def fake_state(root):
    path = os.path.join(root, "state", "wire", "fake.json")
    if not os.path.isfile(path):
        return {"path": None, "env": {}, "registry": {}, "launchers": {}}
    return json.load(open(path, encoding="utf-8"))


def make_zip(path, entries):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)


def build_packages(packages):
    os.makedirs(packages, exist_ok=True)
    wired = {
        "schema": 1, "id": "wired", "version": "1.0.0", "platform": "win-x64",
        "path": ["bin"],
        "env": {"WIRED_HOME": "{{ROOT}}\\current\\wired"},
        "registry": [{"hive": "HKCU", "key": "Software\\PortableDevTest", "name": "Mark",
                      "type": "REG_SZ", "value": "1"},
                     {"hive": "HKLM", "key": "Software\\PortableDevTest", "name": "MachineMark",
                      "type": "REG_SZ", "value": "2"}],
        "launch": [{"name": "wired", "exe": "wired.txt"}],
        "asserts": [{"kind": "exists", "path": "bin\\wired.txt"}],
    }
    make_zip(os.path.join(packages, "wired-1.0.0.pdpkg"), {
        "package.json": json.dumps(wired).encode(),
        "files/bin/wired.txt": b"wired content\n",
        "files/bin/wired.cmd": b"@echo off\necho wired\n",
        "files/gui/wired.cmd": b"@echo off\necho wired\n",
    })

    homed = {
        "schema": 1, "id": "homed", "version": "1.0.0", "platform": "win-x64",
        "path": ["bin"],
        "env": {"HOMED_PATH": "{{HOME}}"},
        "data": [
            {"kind": "link", "source": "{{STICK}}\\\\home\\\\homed", "target": "{{HOME}}\\\\cache"},
            {"kind": "copy", "source": "{{STICK}}\\\\home\\\\homed\\\\seed", "target": "{{HOME}}\\\\copy"},
        ],
        "asserts": [{"kind": "exists", "path": "bin\\homed.txt"}],
    }
    make_zip(os.path.join(packages, "homed-1.0.0.pdpkg"), {
        "package.json": json.dumps(homed).encode(),
        "files/bin/homed.txt": b"homed content\n",
    })

    realwired = {
        "schema": 1, "id": "realwired", "version": "1.0.0", "platform": "win-x64",
        "env": {"PD_TEST_WIRE_MARKER": "set-by-test"},
    }
    make_zip(os.path.join(packages, "realwired-1.0.0.pdpkg"), {
        "package.json": json.dumps(realwired).encode(),
        "files/bin/real.txt": b"real\n",
    })


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
    for directory in ("records", "intent", "wire"):
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


def user_env(name):
    result = subprocess.run(
        ["powershell", "-NoProfile", "-Command", f"[Environment]::GetEnvironmentVariable('{name}','User')"],
        capture_output=True, text=True)
    value = result.stdout.strip()
    return value or None


def clear_user_env(name):
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"[Environment]::SetEnvironmentVariable('{name}',$null,'User')"],
                   capture_output=True, text=True)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)
    fake_mode = os.path.join(root, "state", "wire", "fake-mode")
    open(fake_mode, "w", encoding="utf-8").write("1\n")
    packages = os.path.join(root, "wpkgs")
    build_packages(packages)
    os.makedirs(os.path.join(root, "home", "homed", "seed"), exist_ok=True)
    with open(os.path.join(root, "home", "homed", "seed", "note.txt"), "w", encoding="utf-8") as handle:
        handle.write("seed")

    # Pre-seed a user-owned env var / registry value so clean must restore them, not delete them.
    seeded = fake_state(root)
    seeded.setdefault("env", {})["WIRED_HOME"] = "user-orig"
    seeded.setdefault("registry", {})["HKCU\\Software\\PortableDevTest\\Mark"] = {"type": "REG_SZ", "value": "user-orig"}
    with open(os.path.join(root, "state", "wire", "fake.json"), "w", encoding="utf-8") as handle:
        json.dump(seeded, handle)

    kernel, log = start_kernel(root)
    stable = stable_root(root)
    try:
        print("install with wiring (fake machine)")
        result = pd(root, "--json", "install", os.path.join(packages, "wired-1.0.0.pdpkg"))
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("install exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("wiring changes reported", metrics.get("wiringChanges", 0) >= 4, metrics)
        store_path = metrics.get("storePath", "")
        check("package adopted", os.path.isdir(store_path), store_path)

        state = fake_state(root)
        path_entries = [entry for entry in (state.get("path") or "").split(";") if entry]
        expected_dir = os.path.normcase(os.path.join(stable, "current", "wired", "bin"))
        check("path entry added", any(os.path.normcase(entry) == expected_dir for entry in path_entries), path_entries)
        check("env var expanded", state.get("env", {}).get("WIRED_HOME") == os.path.join(stable, "current", "wired"),
              state.get("env"))
        registry = state.get("registry", {})
        check("registry value written",
              any(key.endswith("PortableDevTest\\Mark") and value.get("value") == "1" for key, value in registry.items()),
              registry)
        check("HKLM registry value written (fake)",
              any(key.startswith("HKLM\\") and key.endswith("PortableDevTest\\MachineMark") and value.get("value") == "2"
                  for key, value in registry.items()), registry)
        launcher = os.path.normcase(os.path.join(stable, "launchers", "wired.lnk"))
        check("launcher generated", any(os.path.normcase(key) == launcher for key in state.get("launchers", {})),
              list(state.get("launchers", {}).keys()))
        link = os.path.join(root, "current", "wired")
        check("current junction present", os.path.exists(link) and os.path.realpath(link) == os.path.realpath(store_path),
              (os.path.realpath(link), store_path))

        print("apply is idempotent")
        result = pd(root, "--json", "apply")
        done = done_of(result.stdout)
        check("apply exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("apply made no changes", (done or {}).get("metrics", {}).get("changed") == 0, done)

        print("clean --dry-run reports a plan without touching the machine")
        before_state = json.dumps(fake_state(root), sort_keys=True)
        result = pd(root, "--json", "clean", "--dry-run")
        done = done_of(result.stdout)
        check("clean dry-run exit 0", result.returncode == 0, result.returncode)
        check("clean dry-run reports changes", (done or {}).get("metrics", {}).get("changed", 0) >= 1, done)
        check("clean dry-run changed nothing", json.dumps(fake_state(root), sort_keys=True) == before_state, fake_state(root))

        print("clean restores the wiring")
        result = pd(root, "--json", "clean")
        done = done_of(result.stdout)
        check("clean exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("clean reported changes", (done or {}).get("metrics", {}).get("changed", 0) >= 4, done)
        state = fake_state(root)
        path_entries = [entry for entry in (state.get("path") or "").split(";") if entry]
        check("path entry removed", not any(os.path.normcase(entry) == expected_dir for entry in path_entries), path_entries)
        check("env var restored to pre-install value", state.get("env", {}).get("WIRED_HOME") == "user-orig", state.get("env"))
        check("registry value restored to pre-install value",
              any(key.endswith("PortableDevTest\\Mark") and value.get("value") == "user-orig"
                  for key, value in state.get("registry", {}).items()),
              state.get("registry"))
        check("HKLM registry value removed by clean",
              not any(key.startswith("HKLM\\") and key.endswith("PortableDevTest\\MachineMark")
                      for key in state.get("registry", {})), state.get("registry"))
        check("launcher removed", not any(os.path.normcase(key) == launcher for key in state.get("launchers", {})),
              state.get("launchers"))
        check("junction removed", not os.path.exists(link), link)

        print("apply restores after clean")
        result = pd(root, "--json", "apply")
        done = done_of(result.stdout)
        check("re-apply exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("re-apply restored changes", (done or {}).get("metrics", {}).get("changed", 0) >= 4, done)
        state = fake_state(root)
        check("path restored", any(os.path.normcase(entry) == expected_dir
                                    for entry in (state.get("path") or "").split(";") if entry), state.get("path"))
        check("env reapplied after clean", state.get("env", {}).get("WIRED_HOME") == os.path.join(stable, "current", "wired"),
              state.get("env"))

        print("home directory and data wiring (fake machine)")
        result = pd(root, "--json", "install", os.path.join(packages, "homed-1.0.0.pdpkg"))
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("homed install exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("homed wiring changes", metrics.get("wiringChanges", 0) >= 4, metrics)
        state = fake_state(root)
        home_machine = os.path.join(stable, "home", "homed")
        links = state.get("links", {})
        check("home junction created",
              any(os.path.normcase(key) == os.path.normcase(home_machine) for key in links), list(links.keys()))
        data_link_target = os.path.join(home_machine, "cache")
        check("data link created",
              any(os.path.normcase(key) == os.path.normcase(data_link_target) for key in links), list(links.keys()))
        copies = state.get("copies", {})
        check("data copy recorded",
              any(os.path.normcase(key) == os.path.normcase(os.path.join(home_machine, "copy")) for key in copies), copies)
        check("{{HOME}} expanded in env", state.get("env", {}).get("HOMED_PATH") == home_machine, state.get("env"))

        result = pd(root, "--json", "clean")
        check("clean after homed exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        state = fake_state(root)
        links = state.get("links", {})
        check("home junction kept by clean",
              any(os.path.normcase(key) == os.path.normcase(home_machine) for key in links), list(links.keys()))
        check("data link removed by clean",
              not any(os.path.normcase(key) == os.path.normcase(data_link_target) for key in links), list(links.keys()))
        check("data copy removed by clean",
              not any(os.path.normcase(key) == os.path.normcase(os.path.join(home_machine, "copy"))
                      for key in state.get("copies", {})), state.get("copies"))

        print("elevation helper (fake mode)")
        helper_dll = os.path.abspath(os.path.join("src", "PortableDev.Elevate", "bin", "Debug", "net8.0", "win-x64", "pd-elevate.dll"))
        if os.path.isfile(helper_dll):
            tmp = os.path.join(root, "state", "tmp")
            os.makedirs(tmp, exist_ok=True)
            helper_state = os.path.join(tmp, "elevate-fake.json")
            helper_request = os.path.join(tmp, "elevate-request.json")
            helper_result = os.path.join(tmp, "elevate-result.json")
            # The helper writes its result exclusively; leftovers from an earlier run must go.
            for leftover in (helper_result, helper_state, helper_request):
                try:
                    os.remove(leftover)
                except OSError:
                    pass
            json.dump({"schema": 1, "registry": {}, "links": {}, "copies": {}}, open(helper_state, "w", encoding="utf-8"))
            json.dump({
                "schema": 1, "result": helper_result, "ops": [
                    {"op": "registry.set", "hive": "HKLM", "key": "Software\\PortableDevTest",
                     "name": "HelperMark", "type": "REG_SZ", "value": "1"},
                    {"op": "link.create", "source": os.path.join(root, "home", "homed"),
                     "target": os.path.join(tmp, "helper-link")},
                    {"op": "copy.create", "source": os.path.join(root, "home", "homed", "seed"),
                     "target": os.path.join(tmp, "helper-copy")},
                ],
            }, open(helper_request, "w", encoding="utf-8"))
            # Mirror the storage flow: the stick root tells the helper where state\tmp is.
            helper_env = dict(os.environ)
            helper_env["PD_ROOT"] = os.path.abspath(root)
            helper_proc = subprocess.run([DOTNET, helper_dll, helper_request, "--fake", helper_state],
                                         capture_output=True, text=True, timeout=120, env=helper_env)
            helper_result_data = json.load(open(helper_result, encoding="utf-8")) if os.path.isfile(helper_result) else None
            check("elevation helper fake mode", helper_proc.returncode == 0 and (helper_result_data or {}).get("ok") is True,
                  (helper_proc.returncode, (helper_proc.stderr or "")[:200]))
            helper_state_data = json.load(open(helper_state, encoding="utf-8"))
            check("elevation helper fake registry",
                  any(key.startswith("HKLM\\") and key.endswith("HelperMark")
                      for key in helper_state_data.get("registry", {})),
                  list(helper_state_data.get("registry", {}).keys()))
            check("elevation helper fake link/copy",
                  bool(helper_state_data.get("links")) and bool(helper_state_data.get("copies")), helper_state_data)
        else:
            check("elevation helper fake mode", False, helper_dll + " is missing (build PortableDev.Elevate)")

        print("real machine environment round trip")
        stop_kernel(root)
        os.remove(fake_mode)
        kernel, log = start_kernel(root)
        clear_user_env("PD_TEST_WIRE_MARKER")
        result = pd(root, "--json", "install", os.path.join(packages, "realwired-1.0.0.pdpkg"))
        done = done_of(result.stdout)
        check("real install exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:], result.stderr[-300:]))
        check("real user env var set", user_env("PD_TEST_WIRE_MARKER") == "set-by-test", user_env("PD_TEST_WIRE_MARKER"))
        result = pd(root, "--json", "clean")
        check("real clean exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("real user env var removed", user_env("PD_TEST_WIRE_MARKER") is None, user_env("PD_TEST_WIRE_MARKER"))
    finally:
        clear_user_env("PD_TEST_WIRE_MARKER")
        if os.path.exists(fake_mode):
            os.remove(fake_mode)
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
