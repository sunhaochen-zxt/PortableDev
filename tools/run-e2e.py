#!/usr/bin/env python3
"""pd run e2e: pkgmgr resolves the record, the kernel relays an exec request, the shell spawns it.

Usage: python -u tools/run-e2e.py [stick-root]
"""
import json
import os
import shutil
import struct
import subprocess
import sys
import time

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
KERNEL = os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64", "pd-kernel.dll")
PD_DLL = os.path.join("src", "PortableDev.Shell", "bin", "Debug", "net8.0", "win-x64", "pd.dll")
PRODUCT_EXTENSIONS = ["storage", "rollback", "download", "unpack", "pkgmgr"]

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


def read_exact(stream, n):
    buf = b""
    while len(buf) < n:
        chunk = stream.read(n - len(buf))
        if not chunk:
            return buf
        buf += chunk
    return buf


def send(stream, obj):
    data = json.dumps(obj, ensure_ascii=False).encode()
    stream.write(struct.pack("<I", len(data)) + data)
    stream.flush()


def recv(stream):
    head = read_exact(stream, 4)
    if len(head) < 4:
        return None
    size = struct.unpack("<I", head)[0]
    return json.loads(read_exact(stream, size))


def stick_id(root):
    with open(os.path.join(root, "state", "stick.json"), "r", encoding="utf-8") as handle:
        return json.load(handle)["stickId"]


def connect(root, timeout=20):
    bs = chr(92)
    pipe = bs + bs + "." + bs + "pipe" + bs + "portabledev-" + stick_id(root)
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            return open(pipe, "r+b", buffering=0)
        except OSError:
            time.sleep(0.1)
    raise SystemExit("pipe never appeared")


COUNTER = [0]


def call(stream, argv, timeout=180):
    COUNTER[0] += 1
    task = f"r{COUNTER[0]}"
    send(stream, {"v": 1, "kind": "req", "id": task, "data": {"argv": argv, "opts": {}}})
    deadline = time.time() + timeout
    while time.time() < deadline:
        env = recv(stream)
        if env is None:
            return None
        if env.get("kind") != "evt":
            continue
        if env["data"].get("type") == "done":
            return env["data"]
    return None


def default_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(os.path.dirname(os.path.dirname(here)), ".run-run")


def deploy(root):
    proc_dir = os.path.join(root, "extensions", "proc")
    shutil.rmtree(proc_dir, ignore_errors=True)
    shutil.copytree(os.path.join("app", "extensions", "proc"), proc_dir)
    for name in PRODUCT_EXTENSIONS:
        target = os.path.join(root, "extensions", name)
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(os.path.join("app", "extensions", name), target)
    shutil.rmtree(os.path.join(root, "extensions", "log"), ignore_errors=True)
    shutil.copytree(os.path.join("tests", "fixtures", "log-ext"), os.path.join(root, "extensions", "log"))
    os.makedirs(os.path.join(root, "state", "wire"), exist_ok=True)
    with open(os.path.join(root, "state", "wire", "fake-mode"), "w", encoding="utf-8") as handle:
        handle.write("1\n")
    for directory in ("store", "current", "staging", "packages"):
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent", "wire", "logs", "tmp", "sources"):
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)


def make_pkg(root):
    directory = os.path.join(root, "pkgs", "demo")
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files", "bin"), exist_ok=True)
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump({"schema": 1, "id": "demo", "version": "1.0.0", "platform": "win-x64"}, handle)
    with open(os.path.join(directory, "files", "bin", "base.txt"), "w", encoding="utf-8") as handle:
        handle.write("base")
    with open(os.path.join(directory, "files", "bin", "hello.cmd"), "w", encoding="utf-8") as handle:
        handle.write("@echo off\r\necho hello %~1\r\nexit /b 0\r\n")
    with open(os.path.join(directory, "files", "bin", "fail.cmd"), "w", encoding="utf-8") as handle:
        handle.write("@echo off\r\nexit /b 3\r\n")
    return directory


def make_launch_pkg(root):
    directory = os.path.join(root, "pkgs", "launchdemo")
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files", "bin"), exist_ok=True)
    manifest = {
        "schema": 1, "id": "launchdemo", "version": "1.0.0", "platform": "win-x64",
        "launch": [{"name": "hello", "exe": "bin/hello.cmd"}],
    }
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    with open(os.path.join(directory, "files", "bin", "base.txt"), "w", encoding="utf-8") as handle:
        handle.write("base")
    with open(os.path.join(directory, "files", "bin", "hello.cmd"), "w", encoding="utf-8") as handle:
        handle.write("@echo off\r\necho hello %~1\r\nexit /b 0\r\n")
    return directory


def pd(root, *args, timeout=120):
    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.dirname(DOTNET)
    env.pop("PD_KERNEL", None)
    return subprocess.run([DOTNET, PD_DLL, "--root", root, *args],
                          capture_output=True, text=True, encoding="utf-8", errors="replace",
                          env=env, timeout=timeout)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else default_root()
    print(f"stick root: {root}")
    shutil.rmtree(root, ignore_errors=True)
    subprocess.run([DOTNET, KERNEL, "--root", root, "--init", "--once"],
                   check=True, capture_output=True, text=True)
    deploy(root)

    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.dirname(DOTNET)
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    child = subprocess.Popen([DOTNET, KERNEL, "--root", root], stdout=log, stderr=subprocess.STDOUT, env=env)
    stream = None
    try:
        stream = connect(root)
        send(stream, {"v": 1, "kind": "hello", "data": {
            "protocol": 1, "client": "run-e2e", "clientVersion": "0.0.1", "features": ["json"]}})
        hello = recv(stream)
        check("hello.ok", hello and hello.get("kind") == "hello.ok", hello)

        package = make_pkg(root)
        done = call(stream, ["install", package])
        check("install exit 0", done and done.get("exitCode") == 0, done)
        launch_package = make_launch_pkg(root)
        done = call(stream, ["install", launch_package])
        check("install launch package exit 0", done and done.get("exitCode") == 0, done)

        print("pd run: inherited stdio and exit codes")
        result = pd(root, "run", "demo", "--exe", "bin/hello.cmd", "world")
        check("run exit 0", result.returncode == 0, result.stderr[-300:])
        check("child stdout inherited", "hello world" in result.stdout, result.stdout[-300:])

        result = pd(root, "run", "demo", "--exe", "bin/fail.cmd")
        check("child failure -> pd exit 1", result.returncode == 1, result.stderr[-300:])

        result = pd(root, "--propagate", "run", "demo", "--exe", "bin/fail.cmd")
        check("--propagate -> child exit 3", result.returncode == 3, result.stderr[-300:])

        result = pd(root, "run", "nosuch", "--exe", "bin/hello.cmd")
        check("unknown package 20", result.returncode == 20, result.stderr[-300:])

        result = pd(root, "run", "demo")
        check("missing --exe 2", result.returncode == 2, result.stderr[-300:])

        print("launch declaration")
        result = pd(root, "run", "launchdemo", "world")
        check("launch declaration auto-resolved", result.returncode == 0, result.stderr[-300:])
        check("launch target output", "hello world" in result.stdout, result.stdout[-300:])

        result = pd(root, "run", "launchdemo", "--name", "nope")
        check("unknown launcher 2", result.returncode == 2, result.stderr[-300:])

        print("--json")
        result = pd(root, "--json", "run", "launchdemo", "world")
        done_event = None
        for line in result.stdout.splitlines():
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue
            if event.get("type") == "done":
                done_event = event
        check("json run exit 0", result.returncode == 0, result.stderr[-300:])
        check("json done event", done_event is not None and done_event.get("exitCode") == 0, done_event)
        check("json metrics child exit", (done_event or {}).get("metrics", {}).get("childExitCode") == 0, done_event)
        check("json child output on stderr", "hello world" in result.stderr, result.stderr[-300:])

        print("pd shell")
        environment = dict(os.environ)
        environment["DOTNET_ROOT"] = os.path.dirname(DOTNET)
        environment.pop("PD_KERNEL", None)
        shell = subprocess.run([DOTNET, PD_DLL, "--root", root, "shell"],
                               input="echo shell-ok\r\nexit\r\n", capture_output=True, text=True,
                               encoding="utf-8", errors="replace", env=environment, timeout=120)
        check("shell exit 0", shell.returncode == 0, shell.stderr[-300:])
        check("shell ran the command", "shell-ok" in shell.stdout, shell.stdout[-300:])

        done = call(stream, ["kernel", "stop"])
        check("kernel stop exit 0", done and done.get("exitCode") == 0, done)
    finally:
        if stream is not None:
            stream.close()
        try:
            child.wait(timeout=20)
        except subprocess.TimeoutExpired:
            child.kill()
        log.close()

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
