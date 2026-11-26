#!/usr/bin/env python3
"""proc extension end-to-end: real kernel + real proc, protocol and pkgmgr integration.

Usage: python tools/proc-e2e.py [stick-root]
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
PRODUCT_EXTENSIONS = ["storage", "rollback", "download", "unpack", "pkgmgr"]

SCRIPT_WRITE = """import json, sys
def ask(cmd, args):
    print(json.dumps({"type": "request", "id": "1", "cmd": cmd, "args": args}), flush=True)
    return json.loads(sys.stdin.readline())
r = ask("write", {"path": "bin/created.txt", "content": "made by script"})
assert r.get("ok"), r
"""

PASS = 0
FAIL = 0
TOPIC_EVENTS = []


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
    body = read_exact(stream, size)
    return json.loads(body)


def stick_id(root):
    with open(os.path.join(root, "state", "stick.json"), "r", encoding="utf-8") as f:
        return json.load(f)["stickId"]


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


def call(stream, argv, timeout=120):
    COUNTER[0] += 1
    task = f"r{COUNTER[0]}"
    send(stream, {"v": 1, "kind": "req", "id": task, "data": {"argv": argv, "opts": {}}})
    events = []
    deadline = time.time() + timeout
    while time.time() < deadline:
        env = recv(stream)
        if env is None:
            return None, events
        if env.get("kind") == "event":
            TOPIC_EVENTS.append(env.get("data") or {})
            continue
        if env.get("kind") != "evt":
            continue
        events.append(env["data"])
        if env["data"].get("type") == "done":
            return env["data"], events
    return None, events


def default_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(os.path.dirname(os.path.dirname(here)), ".run-proc")


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


def make_dir_pkg(root, name, pid, version, script):
    directory = os.path.join(root, "pkgs", name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files", "bin"), exist_ok=True)
    manifest = {
        "schema": 1, "id": pid, "version": version, "platform": "win-x64",
        "script": {"entry": "install.py", "interpreter": "python", "on": ["install"]},
    }
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    with open(os.path.join(directory, "install.py"), "w", encoding="utf-8") as handle:
        handle.write(script)
    with open(os.path.join(directory, "files", "bin", "base.txt"), "w", encoding="utf-8") as handle:
        handle.write("base " + pid)
    return directory


def logs_contain(root, needle):
    logs = os.path.join(root, "state", "logs")
    for directory, _, files in os.walk(logs):
        for name in files:
            path = os.path.join(directory, name)
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as handle:
                    if needle in handle.read():
                        return True
            except OSError:
                continue
    return False


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else default_root()
    print(f"stick root: {root}")
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if os.path.abspath(root).startswith(repo_root + os.sep):
        print("warning: stick root is inside the repository; binaries under it may run at low")
        print("         integrity in this environment and cannot write to the kernel pipe.")

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
        print("handshake")
        send(stream, {"v": 1, "kind": "hello", "data": {
            "protocol": 1, "client": "proc-e2e", "clientVersion": "0.0.1", "features": ["json", "topics"]}})
        hello = recv(stream)
        check("hello.ok", hello and hello.get("kind") == "hello.ok", hello)
        check("hello advertises topics", bool(hello) and "topics" in (hello.get("data") or {}).get("features", []), hello)
        send(stream, {"v": 1, "kind": "ctl", "data": {"action": "subscribe", "scope": "proc"}})

        print("extension: spawn + invoke")
        done, events = call(stream, ["proc", "echo", "hello"])
        types = [e.get("type") for e in events]
        check("proc echo exit 0", done and done.get("exitCode") == 0, (done, types))
        check("proc echo logged", "log" in types, types)
        check("done is last", bool(types) and types[-1] == "done", types)

        done, _ = call(stream, ["proc", "snapshot"])
        check("proc snapshot exit 0", done and done.get("exitCode") == 0, done)

        done, _ = call(stream, ["proc", "nosuch"])
        check("unknown subcommand 2", done and done.get("exitCode") == 2, done)
        check("unknown subcommand code", done and (done.get("error") or {}).get("code") == "ERR_BAD_ARGS", done)

        print("commands: list / kill")
        done, _ = call(stream, ["proc", "list"])
        check("proc list exit 0", done and done.get("exitCode") == 0, done)
        done, _ = call(stream, ["proc", "kill", "999999"])
        check("proc kill unknown 20", done and done.get("exitCode") == 20, done)
        check("proc kill unknown code", done and (done.get("error") or {}).get("code") == "ERR_PROC_NOT_FOUND", done)

        print("system enumeration / system kill")
        done, events = call(stream, ["proc", "system", "--filter", "dotnet"])
        tables = [e for e in events if e.get("type") == "table"]
        filtered_rows = tables[0].get("rows", []) if tables else []
        check("proc system exit 0", done and done.get("exitCode") == 0, done)
        check("proc system filter works", any("dotnet" in str(row[2] or "").lower() for row in filtered_rows), filtered_rows[:3])

        done, events = call(stream, ["proc", "system"])
        tables = [e for e in events if e.get("type") == "table"]
        rows = tables[0].get("rows", []) if tables else []
        check("proc system not empty", len(rows) > 10, len(rows))

        critical_pid = next((row[0] for row in rows if str(row[2] or "").lower() in
                             ("csrss.exe", "wininit.exe", "services.exe", "lsass.exe")), None)
        if critical_pid:
            done, _ = call(stream, ["proc", "kill", str(critical_pid)])
            check("critical kill refused", done and done.get("exitCode") == 11, done)

        done, _ = call(stream, ["proc", "kill", "4"])
        check("pid 4 refused", done and done.get("exitCode") == 11, done)

        victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
        try:
            time.sleep(0.6)
            done, _ = call(stream, ["proc", "kill", str(victim.pid)])
            check("system kill exit 0", done and done.get("exitCode") == 0, done)
            try:
                victim.wait(timeout=5)
                removed = True
            except subprocess.TimeoutExpired:
                removed = False
            check("system kill removed it", removed, victim.pid)
        finally:
            if victim.poll() is None:
                victim.kill()

        print("admin mode (one-shot)")
        os.makedirs(os.path.join(root, "state", "tmp"), exist_ok=True)
        request_path = os.path.join(root, "state", "tmp", "admin-request.json")
        result_path = request_path + ".result.json"
        with open(request_path, "w", encoding="utf-8") as handle:
            json.dump({"action": "system", "filter": "dotnet"}, handle)
        proc_dll = os.path.join("app", "extensions", "proc", "proc.dll")
        admin_env = dict(env)
        admin_env["PD_ROOT"] = root
        subprocess.run([DOTNET, proc_dll, "--admin", request_path, result_path], check=False, capture_output=True,
                       timeout=120, env=admin_env)
        admin_result = {}
        if os.path.isfile(result_path):
            with open(result_path, "r", encoding="utf-8") as handle:
                admin_result = json.load(handle)
        check("admin mode enumerated",
              isinstance(admin_result.get("processes"), list) and bool(admin_result.get("processes")),
              admin_result.get("error"))
        for path in (request_path, result_path):
            try:
                os.remove(path)
            except OSError:
                pass

        print("pkgmgr: script runs through proc.spawn")
        pkg = make_dir_pkg(root, "scripted", "scripted", "1.0.0", SCRIPT_WRITE)
        done, events = call(stream, ["install", pkg], timeout=180)
        check("script install exit 0", done and done.get("exitCode") == 0, (done, events[-3:]))
        check("record written", os.path.isfile(os.path.join(root, "state", "records", "scripted.json")))
        check("script wrote into content", done is not None)

        check("proc.started topic", any(e.get("event") == "proc.started" for e in TOPIC_EVENTS), TOPIC_EVENTS[:3])
        check("proc.exited topic", any(e.get("event") == "proc.exited" for e in TOPIC_EVENTS), TOPIC_EVENTS[:3])
        check("topic payload has pid", all("payload" in e and "pid" in e["payload"] for e in TOPIC_EVENTS if e.get("event") == "proc.started"), TOPIC_EVENTS[:3])

        done, _ = call(stream, ["kernel", "stop"])
        check("kernel stop exit 0", done and done.get("exitCode") == 0, done)
    finally:
        if stream is not None:
            stream.close()
        try:
            child.wait(timeout=20)
        except subprocess.TimeoutExpired:
            child.kill()
        log.close()

    check("proc.spawn logged", logs_contain(root, '"evt":"proc.spawn"'))
    check("proc.exit logged", logs_contain(root, '"evt":"proc.exit"'))

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
