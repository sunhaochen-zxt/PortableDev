#!/usr/bin/env python3
"""Scoop source + dependency recursion e2e.

scoop kind: tarballUrl points at a local bucket zip (offline); manifests convert
to index entries; zip/exe installers convert to real installs at install time.
requires: recursive install, conflict/cycle/no-deps, dry-run plan.

Usage: python -u tools/scoop-e2e.py [stick-root]
"""
import functools
import hashlib
import http.server
import json
import os
import shutil
import socketserver
import struct
import subprocess
import sys
import threading
import time
import zipfile

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
    return os.path.join(os.path.dirname(os.path.dirname(here)), ".run-scoop")


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


def pd_env():
    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.dirname(DOTNET)
    env.pop("PD_KERNEL", None)
    return env


def pd(root, *args, timeout=180):
    return subprocess.run([DOTNET, PD_DLL, "--root", root, *args],
                          capture_output=True, text=True, encoding="utf-8", errors="replace",
                          env=pd_env(), timeout=timeout)


def packages_of(root, *args):
    result = pd(root, "--json", "list", *args, timeout=120)
    if result.returncode != 0:
        return None
    for line in result.stdout.splitlines():
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        if event.get("type") == "done":
            return {p.get("package"): p.get("version") for p in (event.get("metrics", {}) or {}).get("packages", [])}
    return None


def make_bucket(docroot, base_url):
    bucket = os.path.join(docroot, "bucket")
    files = os.path.join(docroot, "files")
    shutil.rmtree(docroot, ignore_errors=True)
    os.makedirs(bucket)
    os.makedirs(files)

    def sha256_file(path):
        digest = hashlib.sha256()
        with open(path, "rb") as handle:
            digest.update(handle.read())
        return digest.hexdigest()

    alpha_zip = os.path.join(files, "alpha-3.2.1.zip")
    with zipfile.ZipFile(alpha_zip, "w", zipfile.ZIP_DEFLATED) as handle:
        handle.writestr("app/alpha.cmd", "@echo off\r\necho SCOOP-ALPHA %*\r\n")
        handle.writestr("app/helper.cmd", "@echo off\r\necho SCOOP-HELPER\r\n")
    with open(os.path.join(files, "beta-1.0.0.exe"), "wb") as handle:
        handle.write(b"MZ-fake-portable-exe\n")
    arch_zip = os.path.join(files, "arch32-9.9.zip")
    with zipfile.ZipFile(arch_zip, "w", zipfile.ZIP_DEFLATED) as handle:
        handle.writestr("test32.cmd", "@echo off\r\necho ARCH32\r\n")
    arch64_zip = os.path.join(files, "arch64-2.0.zip")
    with zipfile.ZipFile(arch64_zip, "w", zipfile.ZIP_DEFLATED) as handle:
        handle.writestr("app/tool.cmd", "@echo off\r\necho ARCH64\r\n")
    frag_zip = os.path.join(files, "frag-1.0.zip")
    with zipfile.ZipFile(frag_zip, "w", zipfile.ZIP_DEFLATED) as handle:
        handle.writestr("frag.cmd", "@echo off\r\necho FRAG\r\n")

    def manifest(name, obj):
        with open(os.path.join(bucket, name + ".json"), "w", encoding="utf-8") as handle:
            json.dump(obj, handle)

    manifest("alpha", {
        "version": "3.2.1", "description": "Alpha app",
        "url": base_url + "/files/alpha-3.2.1.zip", "hash": "sha256:" + sha256_file(alpha_zip),
        "bin": [["app/alpha.cmd", "alpha"], "app/helper.cmd"],
        "env_add_path": "bin", "depends": ["beta"],
    })
    manifest("beta", {
        "version": "1.0.0", "description": "Beta tool",
        "url": base_url + "/files/beta-1.0.0.exe", "hash": "sha256:" + sha256_file(os.path.join(files, "beta-1.0.0.exe")),
        "bin": "beta-1.0.0.exe",
    })
    solo_zip = os.path.join(files, "solo-1.0.zip")
    with zipfile.ZipFile(solo_zip, "w", zipfile.ZIP_DEFLATED) as handle:
        handle.writestr("solo.cmd", "@echo off\r\necho SOLO\r\n")
    manifest("solo", {
        "version": "1.0", "url": base_url + "/files/solo-1.0.zip", "hash": "sha256:" + sha256_file(solo_zip),
        "bin": "solo.cmd",
    })
    manifest("arch32", {
        "version": "9.9",
        "url": base_url + "/files/arch32-9.9.zip", "hash": sha256_file(arch_zip),
        "bin": "test32.cmd",
    })
    manifest("arch64", {
        "version": "2.0",
        "architecture": {"64bit": {
            "url": base_url + "/files/arch64-2.0.zip", "hash": "sha256:" + sha256_file(arch64_zip),
            "extract_dir": "app", "bin": "tool.cmd", "env_add_path": "lib",
        }},
    })
    manifest("only32", {
        "version": "2.0",
        "architecture": {"32bit": {
            "url": base_url + "/files/arch32-9.9.zip", "hash": "sha256:" + sha256_file(arch_zip),
            "bin": "test32.cmd",
        }},
    })
    manifest("frag", {
        "version": "1.0", "url": base_url + "/files/frag-1.0.zip#/dl.zip",
        "hash": "sha256:" + sha256_file(frag_zip), "bin": "frag.cmd",
    })
    manifest("persist-only", {
        "version": "1.0", "url": base_url + "/files/frag-1.0.zip",
        "hash": "sha256:" + sha256_file(frag_zip), "bin": "frag.cmd", "persist": ["data"],
    })
    manifest("skip-installer", {
        "version": "1.0", "url": base_url + "/files/beta-1.0.0.exe",
        "hash": "sha256:" + "0" * 64, "bin": "x.exe",
        "installer": {"script": ["Expand-Archive foo.zip $dir"]},
    })
    manifest("skip-md5", {
        "version": "1.0", "url": base_url + "/files/arch32-9.9.zip",
        "hash": "md5:" + "0" * 32, "bin": "x.cmd",
    })
    cyc_zip = os.path.join(files, "cyc-1.0.zip")
    with zipfile.ZipFile(cyc_zip, "w", zipfile.ZIP_DEFLATED) as handle:
        handle.writestr("cyc.cmd", "@echo off\\r\\necho CYC\\r\\n")
    cyc_hash = sha256_file(cyc_zip)
    manifest("cyc-a", {
        "version": "1.0", "url": base_url + "/files/cyc-1.0.zip", "hash": "sha256:" + cyc_hash,
        "bin": "cyc.cmd", "depends": ["cyc-b"],
    })
    manifest("cyc-b", {
        "version": "1.0", "url": base_url + "/files/cyc-1.0.zip", "hash": "sha256:" + cyc_hash,
        "bin": "cyc.cmd", "depends": ["cyc-a"],
    })
    manifest("skip-msi", {
        "version": "1.0", "url": base_url + "/files/setup.msi",
        "hash": "sha256:" + "0" * 64, "bin": "x.exe",
    })
    archive = shutil.make_archive(os.path.join(docroot, "bucket"), "zip", docroot, "bucket")
    return archive


def make_dep_pkg(root, name, version, requires=None):
    directory = os.path.join(root, "pkgs-dep", name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files", "bin"))
    manifest = {"schema": 1, "id": name, "version": version, "platform": "win-x64"}
    if requires:
        manifest["requires"] = requires
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    with open(os.path.join(directory, "files", "bin", "marker.txt"), "w", encoding="utf-8") as handle:
        handle.write(name + "@" + version)
    return directory


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else default_root()
    print(f"stick root: {root}")
    shutil.rmtree(root, ignore_errors=True)
    subprocess.run([DOTNET, KERNEL, "--root", root, "--init", "--once"],
                   check=True, capture_output=True, text=True)
    deploy(root)

    docroot = os.path.join(root, "http-fixtures")
    handler = functools.partial(QuietHandler, directory=docroot)
    server = socketserver.ThreadingTCPServer(("127.0.0.1", 0), handler, bind_and_activate=False)
    server.allow_reuse_address = True
    server.server_bind()
    server.server_activate()
    port = server.server_address[1]
    base_url = f"http://127.0.0.1:{port}"
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        make_bucket(docroot, base_url)
        tarball = base_url + "/bucket.zip"

        log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
        child = subprocess.Popen([DOTNET, KERNEL, "--root", root], stdout=log, stderr=subprocess.STDOUT, env=pd_env())
        stream = None
        try:
            stream = connect(root)
            send(stream, {"v": 1, "kind": "hello", "data": {
                "protocol": 1, "client": "scoop-e2e", "clientVersion": "0.0.1", "features": ["json"]}})
            hello = recv(stream)
            check("hello.ok", hello and hello.get("kind") == "hello.ok", hello)

            print("scoop source")
            done = call(stream, ["source", "add", "scoop-main", "gh:fake/bucket", "--kind", "scoop", "--tarball-url", tarball])
            check("source add scoop", done and done.get("exitCode") == 0, done)

            done = call(stream, ["install", "alpha"])
            check("install alpha (zip) exit 0", done and done.get("exitCode") == 0, done)
            installed = packages_of(root)
            check("dep beta auto-installed", installed is not None and installed.get("beta") == "1.0.0", installed)
            check("alpha installed", installed is not None and installed.get("alpha") == "3.2.1", installed)

            result = pd(root, "run", "alpha", "--name", "alpha")
            check("run scoop launcher", result.returncode == 0 and "SCOOP-ALPHA" in result.stdout, result.stderr[-300:])

            done = call(stream, ["install", "arch32"])
            check("install arch32 (top-level url fallback) exit 0", done and done.get("exitCode") == 0, done)

            done = call(stream, ["install", "skip-installer"])
            check("scripted installer refused", done and done.get("exitCode") == 20, done)
            done = call(stream, ["install", "skip-md5"])
            check("md5-only hash refused", done and done.get("exitCode") == 20, done)
            done = call(stream, ["install", "skip-msi"])
            check("msi refused", done and done.get("exitCode") == 20, done)

            print("architecture-specific fields and 32-bit-only buckets")
            done = call(stream, ["install", "arch64"])
            check("install arch64 (64bit entry) exit 0", done and done.get("exitCode") == 0, done)
            result = pd(root, "run", "arch64", "--name", "tool")
            check("arch64 launcher from 64bit.bin works", result.returncode == 0 and "ARCH64" in result.stdout,
                  (result.returncode, result.stdout[-300:], result.stderr[-300:]))
            done = call(stream, ["install", "only32"])
            check("install only32 (32-bit fallback) exit 0", done and done.get("exitCode") == 0, done)

            print("url fragment and persist skip")
            done = call(stream, ["install", "frag"])
            check("url with #/dl.zip fragment installs", done and done.get("exitCode") == 0, done)
            done = call(stream, ["install", "persist-only"])
            check("persist manifest is skipped", done and done.get("exitCode") == 20, done)

            result = pd(root, "--json", "install", "alpha", "--dry-run")
            check("dry-run shows dependencies", result.returncode == 0 and "beta" in result.stdout, result.stdout[-300:])

            print("requires recursion (dir packages)")
            dir_c = make_dep_pkg(root, "dep-c", "1.0.0")
            dir_b = make_dep_pkg(root, "dep-b", "2.0.0", [{"id": "dep-c", "range": ">=1.0.0"}])
            dir_a = make_dep_pkg(root, "dep-a", "1.0.0", [{"id": "dep-b"}])
            done = call(stream, ["install", dir_c])
            check("install dep-c exit 0", done and done.get("exitCode") == 0, done)
            done = call(stream, ["install", dir_b])
            check("install dep-b (dep satisfied) exit 0", done and done.get("exitCode") == 0, done)
            done = call(stream, ["install", dir_a])
            check("install dep-a pulls dep-b", done and done.get("exitCode") == 0, done)

            done = call(stream, ["install", "cyc-a"])
            check("circular dependency fails 24", done and done.get("exitCode") == 24, done)

            dir_lone = make_dep_pkg(root, "dep-lone", "1.0.0")
            dir_needy = make_dep_pkg(root, "dep-needy", "1.0.0", [{"id": "dep-lone", "range": ">=2.0.0"}])
            done = call(stream, ["install", dir_lone])
            check("install dep-lone exit 0", done and done.get("exitCode") == 0, done)
            done = call(stream, ["install", dir_needy])
            check("version conflict fails 24", done and done.get("exitCode") == 24, done)
            check("conflict keeps installed version", packages_of(root).get("dep-lone") == "1.0.0", packages_of(root))

            dir_boot = make_dep_pkg(root, "dep-boot", "1.0.0", [{"id": "solo"}])
            done = call(stream, ["install", dir_boot, "--no-deps"])
            check("no-deps skips recursion", done and done.get("exitCode") == 0, done)
            installed = packages_of(root)
            check("no-deps installs the package itself", installed is not None and installed.get("dep-boot") == "1.0.0", installed)
            check("no-deps leaves dep absent", installed is not None and "solo" not in installed, installed)

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
    finally:
        server.shutdown()
        thread.join(timeout=5)

    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
