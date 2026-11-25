#!/usr/bin/env python3
"""End-to-end driver: start pd-kernel, speak the pipe protocol, assert, stop.

Usage: python tools/e2e.py <stick-root> [--dotnet <dotnet.exe>] [--dll <pd-kernel.dll>]
"""
import json
import os
import shutil
import struct
import subprocess
import sys
import time

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
DLL = os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64", "pd-kernel.dll")

PASS = 0
FAIL = 0


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


def check(name, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        print(f"  ok   {name}")
    else:
        FAIL += 1
        print(f"  FAIL {name} {str(detail)[:300]}")


def stick_id(root):
    with open(os.path.join(root, "state", "stick.json"), "r", encoding="utf-8") as f:
        return json.load(f)["stickId"]


COUNTER = [0]


def call(stream, argv, timeout=20):
    COUNTER[0] += 1
    task = f"r{COUNTER[0]}"
    send(stream, {"v": 1, "kind": "req", "id": task, "data": {"argv": argv, "opts": {}}})
    events = []
    deadline = time.time() + timeout
    while time.time() < deadline:
        env = recv(stream)
        if env is None:
            return None, events
        if env.get("kind") != "evt":
            continue
        events.append(env["data"])
        if env["data"].get("type") == "done":
            return env["data"], events
    return None, events


def barrier(stream, timeout=5):
    """Round-trip a ping so any events queued behind the last done are drained first."""
    send(stream, {"v": 1, "kind": "ctl", "data": {"action": "ping", "ts": 0}})
    events = []
    deadline = time.time() + timeout
    while time.time() < deadline:
        env = recv(stream)
        if env is None:
            return None
        if env.get("kind") == "ctl" and env.get("data", {}).get("action") == "pong":
            return events
        if env.get("kind") == "evt":
            events.append(env)
    return None


def connect(root, timeout=15):
    bs = chr(92)
    pipe = bs + bs + "." + bs + "pipe" + bs + "portabledev-" + stick_id(root)
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            return open(pipe, "r+b", buffering=0)
        except OSError:
            time.sleep(0.1)
    raise SystemExit(f"pipe {pipe} never appeared")


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    dotnet = DOTNET
    dll = DLL
    for i, arg in enumerate(sys.argv):
        if arg == "--dotnet":
            dotnet = sys.argv[i + 1]
        if arg == "--dll":
            dll = sys.argv[i + 1]

    os.makedirs(os.path.join(root, "state", "logs"), exist_ok=True)
    shutil.rmtree(os.path.join(root, "state", "logs", "providers"), ignore_errors=True)
    fixture = os.path.join("tests", "fixtures", "echo-ext")
    if os.path.isdir(fixture):
        shutil.copytree(fixture, os.path.join(root, "extensions", "echo"), dirs_exist_ok=True)

    provider = os.environ.get("PD_LOG_FIXTURE", "")
    log_dir = os.path.join(root, "extensions", "log")
    if provider:
        shutil.rmtree(log_dir, ignore_errors=True)
        shutil.copytree(os.path.join("tests", "fixtures", provider), log_dir, dirs_exist_ok=True)
    else:
        shutil.rmtree(log_dir, ignore_errors=True)
    log_path = os.path.join(root, "kernel.out")
    log = open(log_path, "w", encoding="utf-8")
    child_env = dict(os.environ)
    child_env["HTTP_PROXY"] = "http://secret-proxy:8080"
    proc = subprocess.Popen([dotnet, dll, "--root", root], stdout=log, stderr=subprocess.STDOUT, env=child_env)
    try:
        stream = connect(root)
        print("handshake")
        send(stream, {"v": 1, "kind": "hello", "data": {
            "protocol": 1, "client": "e2e", "clientVersion": "0.0.1", "features": ["json", "ext-events"]}})
        hello = recv(stream)
        check("hello.ok", hello and hello.get("kind") == "hello.ok", hello)
        check("kernelAbi == 1", hello["data"].get("kernelAbi") == 1, hello)
        check("stickId echoed", hello["data"].get("stickId") == stick_id(root), hello)
        check("protocolRange", hello["data"].get("protocolRange") == [1, 1], hello)

        print("version")
        done, _ = call(stream, ["version"])
        check("version exit 0", done and done["exitCode"] == 0, done)

        print("kernel status / ps / ext list")
        done, events = call(stream, ["kernel", "status"])
        kv = [e for e in events if e.get("type") == "kv"]
        check("status running", done and done["exitCode"] == 0 and kv and kv[0]["items"]["state"] == "running", events)
        done, _ = call(stream, ["kernel", "ps"])
        check("kernel ps ok", done and done["exitCode"] == 0, done)
        done, events = call(stream, ["kernel", "ext", "list"])
        check("kernel ext list ok", done and done["exitCode"] == 0, done)

        print("help / complete")
        done, events = call(stream, ["help"])
        names = [r[0] for t in events if t.get("type") == "table" for r in t["rows"]]
        check("help has kernel ext list", "kernel ext list" in names, names[:6])
        done, events = call(stream, ["complete", "ker"])
        check("complete ok", done and done["exitCode"] == 0, done)

        print("config")
        done, _ = call(stream, ["config", "get", "kernel.logLevel"])
        check("config get", done and done["exitCode"] == 0, done)
        done, _ = call(stream, ["config", "set", "install.maxConcurrent", "3"])
        check("config set", done and done["exitCode"] == 0, done)
        done, _ = call(stream, ["config", "set", "install.maxConcurrent", "99"])
        check("config set rejects 99", done and done["exitCode"] == 2, done)
        done, _ = call(stream, ["config", "set", "ui.theme", "purple"])
        check("config set rejects enum", done and done["exitCode"] == 2, done)
        done, _ = call(stream, ["config", "unset", "install.maxConcurrent"])
        check("config unset", done and done["exitCode"] == 0, done)
        done, events = call(stream, ["config", "get", "log.level", "--file"])
        kv = [e for e in events if e.get("type") == "kv"]
        check("config get --file reads the file", done and done["exitCode"] == 0 and kv and kv[0]["items"].get("source") == "file",
              (done, kv[:1]))
        done, _ = call(stream, ["config", "set", "log.level", "debug"])
        check("config set reports restartRequired", done and (done.get("metrics") or {}).get("restartRequired") is True, done)
        call(stream, ["config", "set", "log.level", "info"])
        done, _ = call(stream, ["config", "set", "ui.theme", "light"])
        check("config set live key has no restart", done and (done.get("metrics") or {}).get("restartRequired") is False, done)
        call(stream, ["config", "unset", "ui.theme"])

        print("errors")
        done, _ = call(stream, ["statuz"])
        check("unknown command 2", done and done["exitCode"] == 2, done)
        check("err code", done and done["error"]["code"] == "ERR_UNKNOWN_COMMAND", done)
        check("suggestion", "status" in (done["error"].get("hint") or ""), done)
        done, _ = call(stream, ["kernel", "restart", "nosuch"])
        check("restart missing 20", done and done["exitCode"] == 20, done)

        print("ping/pong and cancel")
        send(stream, {"v": 1, "kind": "ctl", "data": {"action": "ping", "ts": 7}})
        pong = None
        deadline = time.time() + 10
        while time.time() < deadline:
            env = recv(stream)
            if env is None:
                break
            if env.get("kind") == "ctl" and env.get("data", {}).get("action") == "pong":
                pong = env
                break
        check("pong", pong is not None, pong)
        send(stream, {"v": 1, "kind": "ctl", "id": "r999", "data": {"action": "cancel"}})

        print("log tail")
        done, events = call(stream, ["kernel", "log", "--tail", "5"])
        lines = [e for e in events if e.get("type") == "log"]
        check("log tail has lines", done and done["exitCode"] == 0 and len(lines) >= 1, lines[:1])
        check("log tail avoids env names", all("HTTP_PROXY" not in json.dumps(e) for e in lines), lines[:1])

        print("extension: spawn + invoke")
        done, events = call(stream, ["echo", "hello"], timeout=30)
        types = [e.get("type") for e in events]
        check("echo exit 0", done and done["exitCode"] == 0, done)
        check("echo emitted step/progress", "step" in types and "progress" in types, types)
        check("done is last", bool(types) and types[-1] == "done", types)
        queued_after_done = barrier(stream)
        check("barrier reachable after done", queued_after_done is not None, queued_after_done)
        check("no events after done", queued_after_done == [], queued_after_done)
        check("exactly one done", types.count("done") == 1, types)
        check("extension env isolated", "proxy=absent" in (done.get("summary") or ""), done)

        print("extension: ps shows the process")
        done, events = call(stream, ["kernel", "ps"])
        rows = [r for t in events if t.get("type") == "table" for r in t["rows"]]
        check("echo has a pid", any(r[0] == "echo" and r[2] != "-" for r in rows), rows)

        print("handshake: extension impersonation rejected")
        attacker = connect(root)
        send(attacker, {"v": 1, "kind": "hello", "data": {
            "protocol": 1, "role": "extension", "name": "echo", "version": "0.0.1", "features": []}})
        reply = recv(attacker)
        check("impersonation rejected", reply and reply.get("kind") == "hello.err", reply)
        check("impersonation protocol code", reply and reply["data"].get("code") == "ERR_PROTOCOL", reply)
        try:
            attacker.close()
        except OSError:
            pass

        print("kernel: duplicate task id rejected")
        duplicate = {"v": 1, "kind": "req", "id": "rdup", "data": {"argv": ["echo", "hello"], "opts": {}}}
        send(stream, duplicate)
        send(stream, duplicate)
        dones = []
        deadline = time.time() + 20
        while time.time() < deadline and len(dones) < 2:
            env = recv(stream)
            if env is None:
                break
            if env.get("kind") == "evt" and env.get("data", {}).get("type") == "done":
                dones.append(env["data"])
        successes = [d for d in dones if d.get("exitCode") == 0]
        rejected = [d for d in dones if d.get("exitCode") == 2]
        check("duplicate id: one success + one rejection", len(successes) == 1 and len(rejected) == 1, dones)
        check("duplicate id rejection code",
              bool(rejected) and rejected[0].get("error", {}).get("code") == "ERR_BAD_ARGS", rejected[:1])
        done, _ = call(stream, ["echo", "hello"], timeout=10)
        check("extension healthy after duplicate id", done and done["exitCode"] == 0, done)

        print("kernel: concurrent request limit")
        for index in range(9):
            send(stream, {"v": 1, "kind": "req", "id": f"c{index}", "data": {"argv": ["echo", "sleep"], "opts": {}}})
        concurrent = []
        deadline = time.time() + 20
        while time.time() < deadline and len(concurrent) < 9:
            env = recv(stream)
            if env is None:
                break
            if env.get("kind") == "evt" and env.get("data", {}).get("type") == "done":
                concurrent.append(env["data"])
        accepted = [d for d in concurrent if d.get("exitCode") == 0]
        rejected = [d for d in concurrent if d.get("exitCode") == 10]
        check("concurrency limit: 8 accepted + 1 busy", len(accepted) == 8 and len(rejected) == 1,
              (len(accepted), len(rejected)))
        check("concurrency busy code", bool(rejected) and rejected[0].get("error", {}).get("code") == "ERR_BUSY",
              rejected[:1])

        print("extension: crash -> failed task -> restart")
        done, _ = call(stream, ["echo", "crash"], timeout=20)
        check("crash surfaces ERR_EXT_TIMEOUT", done and done["exitCode"] == 25, done)
        time.sleep(2.0)
        done, _ = call(stream, ["echo", "hello"], timeout=30)
        check("restarted after crash", done and done["exitCode"] == 0, done)

        print("extension: hang -> watchdog kill")
        started = time.time()
        done, _ = call(stream, ["echo", "hang"], timeout=25)
        elapsed = time.time() - started
        check("hang killed as timeout", done and done["exitCode"] == 25, done)
        check("watchdog fired near heartbeatTimeout", elapsed < 20, elapsed)

        print("extension: quiet long task is kept alive by heartbeat ping")
        started = time.time()
        done, _ = call(stream, ["echo", "slow"], timeout=25)
        elapsed = time.time() - started
        check("slow task not killed", done and done["exitCode"] == 0, done)
        check("slow task outlived heartbeat window", elapsed > 4.5, elapsed)

        print("extension: secrets never reach the log")
        done, events = call(stream, ["kernel", "log", "--tail", "200"])
        blob = json.dumps(events)
        check("no proxy value in logs", "secret-proxy" not in blob, blob[:200])

        if provider:
            print(f"log provider contract: {provider}")
            done, events = call(stream, ["log", "--tail", "5"], timeout=30)
            records = [e["record"] for e in events if e.get("type") == "log" and "record" in e]
            check("log tail ok", done and done["exitCode"] == 0, done)
            check("records carry contract fields",
                  bool(records) and all("pri" in r and "_bootId" in r and "seq" in r and "_unit" in r for r in records),
                  records[:1])
            check("boot beats match", all(r["_bootId"] for r in records), records[:1])

            done, events = call(stream, ["log", "--level", "error"], timeout=30)
            errs = [e["record"] for e in events if e.get("type") == "log"]
            check("level filter", done and all(r["pri"] <= 3 for r in errs), errs[:1])

            done, events = call(stream, ["log", "--unit", "echo", "--tail", "20"], timeout=30)
            echo_recs = [e["record"] for e in events if e.get("type") == "log"]
            check("unit filter", done and echo_recs and all(r["_unit"] == "echo" for r in echo_recs), echo_recs[:1])

            done, _ = call(stream, ["log", "--grep", "proxy=absent"], timeout=30)
            check("grep ok", done and done["exitCode"] == 0, done)
            done, _ = call(stream, ["log", "--since", "1m"], timeout=30)
            check("since duration ok", done and done["exitCode"] == 0, done)
            done, _ = call(stream, ["log", "--since", "nonsense"], timeout=30)
            check("invalid since rejected", done and done["exitCode"] == 2, done)

            done, events = call(stream, ["log", "--disk-usage"], timeout=30)
            check("disk usage table", done and any(e.get("type") == "table" for e in events), events[:1])

            done, _ = call(stream, ["log", "--vacuum", "--before", "1s"], timeout=30)
            check("vacuum ok", done and done["exitCode"] == 0, done)

            done, events = call(stream, ["log", "--grep", "secret-proxy"], timeout=30)
            secrets = [e["record"] for e in events if e.get("type") == "log"]
            check("provider holds no secrets", not secrets, secrets[:1])

            send(stream, {"v": 1, "kind": "req", "id": "rf1", "data": {"argv": ["log", "--follow"], "opts": {}}})
            time.sleep(0.5)
            send(stream, {"v": 1, "kind": "ctl", "id": "rf1", "data": {"action": "cancel"}})
            done = None
            deadline = time.time() + 10
            while time.time() < deadline:
                env = recv(stream)
                if env is None:
                    break
                if env.get("kind") == "evt" and env.get("data", {}).get("type") == "done" and env.get("id") == "rf1":
                    done = env["data"]
                    break
            check("follow cancelled with 130", done and done["exitCode"] == 130, done)
        else:
            done, _ = call(stream, ["log"], timeout=20)
            check("missing provider -> 20", done and done["exitCode"] == 20, done)
            check("missing provider code", done and done["error"]["code"] == "ERR_LOG_PROVIDER_MISSING", done)

        print("shutdown" + (" (force)" if provider else ""))
        shutdown = ["kernel", "stop", "--force"] if provider else ["kernel", "stop"]
        done, _ = call(stream, shutdown)
        check("stop exit 0", done and done["exitCode"] == 0, done)
        try:
            stream.close()
        except OSError:
            pass

        try:
            proc.wait(timeout=10)
            check("kernel exited cleanly", proc.returncode == 0, proc.returncode)
        except subprocess.TimeoutExpired:
            check("kernel exited cleanly", False, "still running")
    finally:
        if proc.poll() is None:
            proc.kill()
        log.close()

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
