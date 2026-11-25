#!/usr/bin/env python3
"""Manual elevation self-test (run from a NORMAL, non-admin terminal).

It exercises the pd-elevate helper the same way the storage extension does:
  fake mode (no UAC), then a real HKLM write + read + delete (2 UAC prompts).

Usage:
  python tools/elevation-selftest.py            # fake + real
  python tools/elevation-selftest.py --fake-only
"""
import json
import os
import subprocess
import sys
import tempfile

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
DLL = os.path.abspath(os.path.join("src", "PortableDev.Elevate", "bin", "Debug", "net8.0", "win-x64", "pd-elevate.dll"))
KEY = "Software\\PortableDevTest"
NAME = "PdElevateSelftest"
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


def run_helper(ops, fake_state=None, timeout=240):
    with tempfile.TemporaryDirectory(prefix="pd-elevate-") as work:
        result_path = os.path.join(work, "result.json")
        request_path = os.path.join(work, "request.json")
        json.dump({"schema": 1, "result": result_path, "ops": ops}, open(request_path, "w", encoding="utf-8"))
        args = [DOTNET, DLL, request_path]
        if fake_state is not None:
            args += ["--fake", fake_state]
        proc = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
        result = json.load(open(result_path, encoding="utf-8")) if os.path.isfile(result_path) else None
        return proc, result


def read_hklm_value():
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, KEY) as key:
            return winreg.QueryValueEx(key, NAME)[0]
    except FileNotFoundError:
        return None


def main():
    if not os.path.isfile(DLL):
        print("helper is not built; run:")
        print(r"  Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe build src\PortableDev.Elevate\PortableDev.Elevate.csproj -c Debug")
        return 2

    print("fake mode (no UAC)")
    with tempfile.TemporaryDirectory(prefix="pd-elevate-fake-") as work:
        state_path = os.path.join(work, "state.json")
        json.dump({"schema": 1, "registry": {}, "links": {}, "copies": {}}, open(state_path, "w", encoding="utf-8"))
        proc, result = run_helper([
            {"op": "registry.set", "hive": "HKLM", "key": KEY, "name": NAME, "type": "REG_SZ", "value": "fake-ok"},
            {"op": "link.create", "source": work, "target": os.path.join(work, "link")},
        ], fake_state=state_path, timeout=60)
        check("fake helper exit 0", proc.returncode == 0, (proc.returncode, proc.stderr[:200]))
        check("fake helper result ok", (result or {}).get("ok") is True, result)
        fake = json.load(open(state_path, encoding="utf-8"))
        check("fake registry written", any(key.endswith(NAME) for key in fake.get("registry", {})), fake.get("registry"))
        check("fake link written", any(key.endswith("link") for key in fake.get("links", {})), fake.get("links"))

    if "--fake-only" in sys.argv:
        print(f"\n{PASS} passed, {FAIL} failed")
        return 0 if FAIL == 0 else 1

    print("real mode (UAC prompts expected: write, then delete)")
    proc, result = run_helper([
        {"op": "registry.set", "hive": "HKLM", "key": KEY, "name": NAME, "type": "REG_SZ", "value": "probe-ok"},
    ])
    check("real helper write exit 0", proc.returncode == 0, (proc.returncode, proc.stderr[:200]))
    check("real helper write result ok", (result or {}).get("ok") is True, result)
    check("HKLM value readable", read_hklm_value() == "probe-ok", read_hklm_value())

    proc, result = run_helper([
        {"op": "registry.delete", "hive": "HKLM", "key": KEY, "name": NAME},
    ])
    check("real helper delete exit 0", proc.returncode == 0, (proc.returncode, proc.stderr[:200]))
    check("HKLM value removed", read_hklm_value() is None, read_hklm_value())

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
