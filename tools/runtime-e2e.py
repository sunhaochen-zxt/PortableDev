#!/usr/bin/env python3
"""Portable-runtime e2e: the deployed stick works on a machine without .NET or Node.

Deploys with tools/deploy-dev.py, then runs the CLI and the window with DOTNET_ROOT,
PD_* and any dotnet/node on PATH removed. Everything must resolve the bundled runtime.

Usage: python -u tools/runtime-e2e.py [dest]
"""
import json
import os
import shutil
import subprocess
import sys

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


def clean_env(temp_dir=None):
    """Keep only Windows system directories; drop every .NET/PortableDev hint."""
    system32 = os.path.join(os.environ.get("SystemRoot", r"C:\\Windows"), "system32")
    env = {
        "SystemRoot": os.environ.get("SystemRoot", r"C:\\Windows"),
        "SystemDrive": os.environ.get("SystemDrive", "C:"),
        "windir": os.environ.get("windir", r"C:\\Windows"),
        "TEMP": temp_dir or os.environ.get("TEMP", r"C:\\Windows\\Temp"),
        "TMP": temp_dir or os.environ.get("TMP", r"C:\\Windows\\Temp"),
        "PATH": system32,
        "USERPROFILE": os.environ.get("USERPROFILE", ""),
    }
    return {key: value for key, value in env.items() if value}


def stop_kernel(dest, env):
    dotnet = os.path.join(dest, "app", "runtime", "dotnet.exe")
    pd = os.path.join(dest, "app", "pd.dll")
    subprocess.run([dotnet, pd, "--root", dest, "kernel", "stop"], env=env,
                   capture_output=True, text=True, timeout=120, cwd=dest)


def run(args, dest, env, timeout=180):
    return subprocess.run(args, env=env, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=timeout, cwd=dest)


def main():
    dest = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".run-runtime")
    print("runtime root: " + dest)
    # A previous run may have left a kernel holding the single-instance lock.
    stale = os.path.join(dest, "app", "runtime", "dotnet.exe")
    if os.path.isfile(stale):
        try:
            stop_kernel(dest, clean_env())
        except Exception:
            pass
    shutil.rmtree(dest, ignore_errors=True)
    deployed = subprocess.run([sys.executable, os.path.join("tools", "deploy-dev.py"), dest],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
    check("deployment built", deployed.returncode == 0, deployed.stdout[-400:] + deployed.stderr[-400:])

    runtime = os.path.join(dest, "app", "runtime")
    dotnet = os.path.join(runtime, "dotnet.exe")
    node = os.path.join(runtime, "node", "node.exe")
    check("bundled dotnet host", os.path.isfile(dotnet), dotnet)
    check("bundled node host", os.path.isfile(node), node)
    shared = os.path.join(runtime, "shared", "Microsoft.NETCore.App")
    versions = [name for name in os.listdir(shared)] if os.path.isdir(shared) else []
    check("bundled shared runtime", len(versions) > 0, versions)
    for launcher in ("启动 PortableDev.cmd", "pd.cmd"):
        path = os.path.join(dest, launcher)
        content = open(path, encoding="utf-8").read() if os.path.isfile(path) else ""
        check("launcher uses bundled runtime: " + launcher,
              "app\\runtime\\dotnet.exe" in content and "DOTNET_ROOT" in content,
              content[:200] if os.path.isfile(path) else "missing")

    temp_dir = os.path.join(dest, "state", "tmp")
    os.makedirs(temp_dir, exist_ok=True)
    env = clean_env(temp_dir)
    print("clean environment: PATH=" + env["PATH"] + ", TEMP=" + temp_dir)
    pd = os.path.join(dest, "app", "pd.dll")
    try:
        version = run([dotnet, pd, "--root", dest, "version"], dest, env)
        check("pd version runs without DOTNET_ROOT", version.returncode == 0, version.stderr[-300:])
        check("version output", "pd 2.0.0" in (version.stdout + version.stderr), version.stdout[-200:])

        listing = run([dotnet, pd, "--root", dest, "--json", "list"], dest, env)
        check("node extension loads without system Node", listing.returncode == 0,
              (listing.stdout + listing.stderr)[-300:])

        smoke = os.path.join(dest, "runtime-smoke.json")
        if os.path.exists(smoke):
            os.remove(smoke)
        window = run([dotnet, os.path.join(dest, "app", "pd-window.dll"), "--smoke", smoke,
                      "--root", dest, "--extensions", os.path.join(dest, "app", "extensions"),
                      "--kernel-dll", os.path.join(dest, "app", "pd-kernel.dll")], dest, env, timeout=240)
        report = {}
        if os.path.isfile(smoke):
            try:
                report = json.load(open(smoke, encoding="utf-8"))
            except (OSError, ValueError):
                report = {}
        check("window smoke runs without DOTNET_ROOT", window.returncode == 0, window.stderr[-300:])
        check("window smoke ok", report.get("ok") is True, report)
    finally:
        stop_kernel(dest, env)

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
