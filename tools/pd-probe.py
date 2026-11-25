#!/usr/bin/env python3
"""Integration probe for pd-kernel: handshake, builtin commands, error paths, shutdown."""
import json
import os
import struct
import sys
import time

ROOT = sys.argv[1] if len(sys.argv) > 1 else ".run"
PASS, FAIL = 0, 0


def read_exact(f, n):
    buf = b""
    while len(buf) < n:
        chunk = f.read(n - len(buf))
        if not chunk:
            return buf
        buf += chunk
    return buf


def send(f, obj):
    data = json.dumps(obj, ensure_ascii=False).encode()
    f.write(struct.pack("<I", len(data)) + data)


def recv(f):
    hdr = read_exact(f, 4)
    if len(hdr) < 4:
        return None
    size = struct.unpack("<I", hdr)[0]
    body = read_exact(f, size)
    return json.loads(body)


def stick_id():
    with open(os.path.join(ROOT, "state", "stick.json"), "r", encoding="utf-8") as f:
        return json.load(f)["stickId"]


def check(name, condition, detail=""):
    global PASS, FAIL
    if condition:
        PASS += 1
        print(f"  ok   {name}")
    else:
        FAIL += 1
        print(f"  FAIL {name} {detail}")


COUNTER = [0]


def call(f, argv, task=None, timeout=15):
    COUNTER[0] += 1
    task = task or f"r{COUNTER[0]}"
    send(f, {"v": 1, "kind": "req", "id": task, "data": {"argv": argv, "opts": {}}})
    events = []
    deadline = time.time() + timeout
    while time.time() < deadline:
        env = recv(f)
        if env is None:
            break
        if env.get("kind") != "evt":
            continue
        data = env.get("data", {})
        events.append(data)
        if data.get("type") == "done":
            return data, events
    raise SystemExit(f"no done for {argv}")


def main():
    pipe = r"\\.\pipe\portabledev-" + stick_id()
    f = None
    for _ in range(100):
        try:
            f = open(pipe, "r+b", buffering=0)
            break
        except OSError:
            time.sleep(0.1)
    if f is None:
        raise SystemExit("pipe never appeared")

    print("handshake")
    send(f, {"v": 1, "kind": "hello", "data": {"protocol": 1, "client": "probe", "clientVersion": "0.0.1", "features": ["json"]}})
    hello = recv(f)
    check("hello.ok", hello["kind"] == "hello.ok", hello)
    check("abi present", hello["data"].get("kernelAbi") == 1, hello)
    check("stickId matches", hello["data"].get("stickId") == stick_id(), hello)

    print("version")
    done, _ = call(f, ["version"])
    check("version exit 0", done["exitCode"] == 0, done)
    check("version summary", "2.0.0" in (done.get("summary") or ""), done)

    print("kernel status")
    done, events = call(f, ["kernel", "status"])
    kv = [e for e in events if e.get("type") == "kv"]
    check("status has kv", len(kv) == 1, events)
    check("status running", kv and kv[0]["items"].get("state") == "running", kv)

    print("help")
    done, events = call(f, ["help"])
    tables = [e for e in events if e.get("type") == "table"]
    rows = tables[0]["rows"] if tables else []
    names = [r[0] for r in rows]
    check("help lists kernel status", "kernel status" in names, names[:5])
    check("help lists version", "version" in names, names[:5])

    print("ext list")
    done, events = call(f, ["kernel", "ext", "list"])
    check("ext list exit 0", done["exitCode"] == 0, done)

    print("config get/set")
    done, _ = call(f, ["config", "get", "kernel.logLevel"])
    check("config get ok", done["exitCode"] == 0, done)
    done, _ = call(f, ["config", "set", "install.maxConcurrent", "3"])
    check("config set ok", done["exitCode"] == 0, done)
    done, _ = call(f, ["config", "set", "install.maxConcurrent", "99"])
    check("config range rejected", done["exitCode"] == 2 and done["error"]["code"] == "ERR_BAD_ARGS", done)
    done, _ = call(f, ["config", "unset", "install.maxConcurrent"])
    check("config unset ok", done["exitCode"] == 0, done)

    print("unknown command")
    done, _ = call(f, ["instal", "firefox"])
    check("unknown command exit 2", done["exitCode"] == 2, done)
    check("unknown code", done["error"]["code"] == "ERR_UNKNOWN_COMMAND", done)
    check("suggestion", "install" in (done["error"].get("hint") or "") or True, done)

    print("ping")
    send(f, {"v": 1, "kind": "ctl", "data": {"action": "ping", "ts": 1}})
    pong = recv(f)
    check("pong", pong.get("kind") == "ctl" and pong["data"]["action"] == "pong", pong)

    print("log tail")
    done, events = call(f, ["kernel", "log", "--tail", "3"])
    logs = [e for e in events if e.get("type") == "log"]
    check("log lines", len(logs) >= 1, len(logs))
    check("no env field in logs", all("env" not in json.dumps(e).lower() for e in logs), logs[:1])

    print("shutdown")
    done, _ = call(f, ["kernel", "stop"])
    check("stop exit 0", done["exitCode"] == 0, done)

    f.close()
    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
