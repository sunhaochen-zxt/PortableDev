#!/usr/bin/env python3
r"""Real-machine HKCU registry e2e: storage must use pd-elevate (--no-elevate) instead of reg.exe.

Deploys the built helper into <root>/app/tools and runs the kernel in real (non-fake) wire mode.
Safe: only touches HKCU\Software\PortableDevRegistryTest and restores it afterwards.
"""
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
DOTNET_DIR = os.path.dirname(DOTNET)
KERNEL_DLL = os.path.abspath(os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64", "pd-kernel.dll"))
PD_DLL = os.path.abspath(os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll"))
ELEVATE_DIR = os.path.abspath(os.path.join("src", "PortableDev.Elevate", "bin", "Debug", "net8.0", "win-x64"))
KEY = r"Software\PortableDevRegistryTest"
KEY_FULL = r"Software\PortableDevRegistryTest"

PASS = 0
FAIL = 0


def check(name, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        print(f"  ok   {name}")
    else:
        FAIL += 1
        print(f"  FAIL {name} {str(detail)[:2000]}")


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


def read_value(name):
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, KEY) as key:
            return winreg.QueryValueEx(key, name)[0]
    except FileNotFoundError:
        return None


def write_value(name, value):
    import winreg
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, KEY) as key:
        winreg.SetValueEx(key, name, 0, winreg.REG_SZ, value)


def delete_value(name):
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, KEY, 0, winreg.KEY_SET_VALUE) as key:
            winreg.DeleteValue(key, name)
    except FileNotFoundError:
        pass


def delete_key():
    import winreg
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, KEY)
    except FileNotFoundError:
        pass


def kill_leftovers(root):
    if os.name != "nt":
        return
    marker = os.path.abspath(root).replace("'", "''")
    root_arg = str(root).replace("'", "''")
    script = (
        "$marker='" + marker + "'; $rootArg='" + root_arg + "'; "
        "Get-CimInstance Win32_Process | Where-Object { "
        "($_.Name -eq 'node.exe' -or $_.Name -eq 'dotnet.exe' -or $_.Name -eq 'pd-kernel.exe' -or $_.Name -eq 'pd-elevate.exe') -and $_.CommandLine -and "
        "(($_.CommandLine -like ('*' + $marker + '*')) -or ($_.CommandLine -like ('*--root*' + $rootArg + '*'))) "
        "} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)


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
    with open(os.path.join(root, "state", "stick.json"), "w", encoding="utf-8") as handle:
        json.dump({"schema": 1, "stickId": "pd-" + os.urandom(6).hex(), "source": "manual",
                   "volumeSerial": "", "label": ""}, handle)
    tools = os.path.join(root, "app", "tools")
    os.makedirs(tools, exist_ok=True)
    for name in ("pd-elevate.dll", "pd-elevate.deps.json", "pd-elevate.runtimeconfig.json"):
        source = os.path.join(ELEVATE_DIR, name)
        if not os.path.isfile(source):
            raise SystemExit("missing helper build output: " + source)
        shutil.copyfile(source, os.path.join(tools, name))


def make_pkg(path):
    manifest = {
        "schema": 1, "id": "regpkg", "version": "1.0.0", "platform": "win-x64",
        "registry": [
            {"hive": "HKCU", "key": KEY_FULL, "name": "Mark", "type": "REG_SZ", "value": "1"},
            {"hive": "HKCU", "key": KEY_FULL, "name": "Other", "type": "REG_SZ", "value": "2"},
        ],
    }
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("package.json", json.dumps(manifest))
        archive.writestr("files/bin/regpkg.txt", "registry package")


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


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run-registry"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)

    original_mark = read_value("Mark")
    original_other = read_value("Other")
    delete_value("Other")
    write_value("Mark", "user-orig")

    package = os.path.join(root, "regpkg-1.0.0.pdpkg")
    make_pkg(package)

    kernel, log = start_kernel(root)
    try:
        result = pd(root, "--json", "install", package)
        check("install registry package exit 0", result.returncode == 0, (result.returncode, result.stdout[-500:]))
        check("HKCU value written through the helper", read_value("Mark") == "1", read_value("Mark"))
        check("HKCU second value written", read_value("Other") == "2", read_value("Other"))

        result = pd(root, "--json", "clean")
        check("clean exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("clean restored the user's original value", read_value("Mark") == "user-orig", read_value("Mark"))
        check("clean removed the value that did not exist before", read_value("Other") is None, read_value("Other"))

        result = pd(root, "--json", "install", package)
        check("reinstall registry package", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        result = pd(root, "--json", "remove", "regpkg")
        check("remove registry package exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        check("remove restored the original value", read_value("Mark") == "user-orig", read_value("Mark"))
        check("remove deleted the new value", read_value("Other") is None, read_value("Other"))
        check("record removed", not os.path.isfile(os.path.join(root, "state", "records", "regpkg.json")))
        result = pd(root, "--json", "list")
        done = done_of(result.stdout)
        check("list is empty", result.returncode == 0 and (done or {}).get("metrics", {}).get("packages") == [], done)
    finally:
        stop_kernel(root)
        log.close()
        kill_leftovers(root)
        if original_mark is None:
            delete_value("Mark")
        else:
            write_value("Mark", original_mark)
        if original_other is None:
            delete_value("Other")
        else:
            write_value("Other", original_other)
        delete_key()

    print(f"\npassed {PASS}, failed {FAIL}")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
