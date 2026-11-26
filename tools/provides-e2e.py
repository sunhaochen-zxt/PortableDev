#!/usr/bin/env python3
"""Virtual packages (provides) + replaces e2e.

- requires on a virtual id resolves to a providing package (installed first,
  else lowest-priority source; same-priority tie is an error);
- install of a package with replaces uninstalls the replaced package first.

Usage: python -u tools/provides-e2e.py [stick-root]
"""
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import time
import zipfile

DOTNET = r"Z:\PortableDev\tools\dotnet-sdk\8.0.426\dotnet.exe"
KERNEL = os.path.join("src", "PortableDev.Kernel", "bin", "Debug", "net8.0", "win-x64", "pd-kernel.dll")
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
    return os.path.join(os.path.dirname(os.path.dirname(here)), ".run-provides")


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


def make_pkg(directory, pkg_id, version, extra=None):
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files"))
    manifest = {"schema": 1, "id": pkg_id, "version": version, "platform": "win-x64"}
    if extra:
        manifest.update(extra)
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    with open(os.path.join(directory, "files", "marker.txt"), "w", encoding="utf-8") as handle:
        handle.write(pkg_id + "@" + version)
    return directory


def make_pdpkg(srcdir, destdir, pkg_id, version, extra=None):
    build = os.path.join(srcdir, pkg_id + "-build")
    make_pkg(build, pkg_id, version, extra)
    dest = os.path.join(destdir, pkg_id + "-" + version + ".pdpkg")
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as handle:
        for base, _dirs, files in os.walk(build):
            for name in files:
                full = os.path.join(base, name)
                handle.write(full, os.path.relpath(full, build).replace(os.sep, "/"))
    digest = hashlib.sha256()
    with open(dest, "rb") as handle:
        digest.update(handle.read())
    return dest, digest.hexdigest()


def add_source_with_index(root, stream, name, priority, entries):
    directory = os.path.join(root, "pkgs-prov", name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(directory)
    index = {"schema": 1, "name": name, "packages": {}}
    for pkg_id, version, provides in entries:
        dest, sha = make_pdpkg(root, directory, pkg_id, version)
        index["packages"][pkg_id] = {
            "latest": version,
            "provides": provides,
            "versions": {version: {"url": os.path.basename(dest), "sha256": sha}},
        }
    with open(os.path.join(directory, "index.json"), "w", encoding="utf-8") as handle:
        json.dump(index, handle)
    return call(stream, ["source", "add", name, directory, "--kind", "dir", "--priority", str(priority)])


def installed_ids(root):
    return sorted(os.path.splitext(name)[0] for name in os.listdir(os.path.join(root, "state", "records")) if name.endswith(".json"))


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
            "protocol": 1, "client": "provides-e2e", "clientVersion": "0.0.1", "features": ["json"]}})
        hello = recv(stream)
        check("hello.ok", hello and hello.get("kind") == "hello.ok", hello)

        print("installed provider satisfies virtual dep")
        ed_real = make_pkg(os.path.join(root, "pkgs-dep", "ed-real"), "ed-real", "1.0.0", {"provides": ["editor"]})
        done = call(stream, ["install", ed_real])
        check("install ed-real exit 0", done and done.get("exitCode") == 0, done)
        needy = make_pkg(os.path.join(root, "pkgs-dep", "needy-editor"), "needy-editor", "1.0.0", {"requires": [{"id": "editor"}]})
        done = call(stream, ["install", needy])
        check("virtual dep satisfied by installed package", done and done.get("exitCode") == 0, done)
        check("no extra provider installed", installed_ids(root) == ["ed-real", "needy-editor"], installed_ids(root))

        print("priority picks provider")
        done = call(stream, ["uninstall", "ed-real"])
        check("uninstall ed-real exit 0", done and done.get("exitCode") == 0, done)
        done = add_source_with_index(root, stream, "provsrc", 100, [("ed-h", "1.0.0", ["editor"])])
        check("add provsrc exit 0", done and done.get("exitCode") == 0, done)
        done = add_source_with_index(root, stream, "provsrc2", 200, [("ed-l", "1.0.0", ["editor"])])
        check("add provsrc2 exit 0", done and done.get("exitCode") == 0, done)
        needy2 = make_pkg(os.path.join(root, "pkgs-dep", "needy2"), "needy2", "1.0.0", {"requires": [{"id": "editor"}]})
        done = call(stream, ["install", needy2])
        check("install with virtual dep exit 0", done and done.get("exitCode") == 0, done)
        check("higher priority provider wins", "ed-h" in installed_ids(root) and "ed-l" not in installed_ids(root), installed_ids(root))

        print("same-priority tie is an error")
        done = add_source_with_index(root, stream, "amb1", 300, [("ed-x", "1.0.0", ["ambvirt"])])
        check("add amb1 exit 0", done and done.get("exitCode") == 0, done)
        done = add_source_with_index(root, stream, "amb2", 300, [("ed-y", "1.0.0", ["ambvirt"])])
        check("add amb2 exit 0", done and done.get("exitCode") == 0, done)
        needy_amb = make_pkg(os.path.join(root, "pkgs-dep", "needy-amb"), "needy-amb", "1.0.0", {"requires": [{"id": "ambvirt"}]})
        done = call(stream, ["install", needy_amb])
        check("ambiguous providers fail 21", done and done.get("exitCode") == 21, done)
        msg = json.dumps(done) if done else ""
        check("ambiguity names candidates", "ed-x" in msg and "ed-y" in msg, msg[:300])

        print("version constraint on virtual dep")
        needy_new = make_pkg(os.path.join(root, "pkgs-dep", "needy-new"), "needy-new", "1.0.0", {"requires": [{"id": "editor", "range": ">=2.0.0"}]})
        done = call(stream, ["install", needy_new])
        check("unsatisfiable virtual range fails", done and done.get("exitCode") in (20, 24), done)

        print("replaces")
        oldpkg = make_pkg(os.path.join(root, "pkgs-dep", "oldpkg"), "oldpkg", "1.0.0")
        done = call(stream, ["install", oldpkg])
        check("install oldpkg exit 0", done and done.get("exitCode") == 0, done)
        newpkg = make_pkg(os.path.join(root, "pkgs-dep", "newpkg"), "newpkg", "2.0.0", {"replaces": ["oldpkg"]})
        done = call(stream, ["install", newpkg])
        check("install replacer exit 0", done and done.get("exitCode") == 0, done)
        ids = installed_ids(root)
        check("replaced package removed", "oldpkg" not in ids and "newpkg" in ids, ids)

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
