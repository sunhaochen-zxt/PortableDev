#!/usr/bin/env python3
"""End-to-end tests for pkgmgr + download + unpack + storage install flow."""
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
import threading
import time
import zipfile
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

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


def sha256(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def make_zip(path, entries):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)


def make_targz(path, entries):
    with tarfile.open(path, "w:gz") as archive:
        for name, data in entries.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))


def build_fixtures(root, served, packages, port=None):
    os.makedirs(served, exist_ok=True)
    os.makedirs(packages, exist_ok=True)

    hello_manifest = {
        "schema": 1, "id": "hello", "version": "1.0.0", "platform": "win-x64",
        "asserts": [{"kind": "exists", "path": "bin/hello.txt"}],
    }
    make_zip(os.path.join(packages, "hello-1.0.0.pdpkg"), {
        "package.json": json.dumps(hello_manifest).encode(),
        "files/bin/hello.txt": b"hello from a pdpkg\n",
    })

    dir_root = os.path.join(packages, "dirpkg")
    os.makedirs(os.path.join(dir_root, "files", "bin"), exist_ok=True)
    open(os.path.join(dir_root, "package.json"), "w", encoding="utf-8").write(json.dumps({
        "schema": 1, "id": "dirpkg", "version": "0.1.0", "platform": "win-x64",
    }))
    open(os.path.join(dir_root, "files", "bin", "dir.txt"), "w", encoding="utf-8").write("from a directory\n")

    payload = os.path.join(served, "remote-1.0.0.tar.gz")
    make_targz(payload, {"bin/remote.txt": b"hello from the network\n"})
    remote_hash = sha256(payload)
    make_zip(os.path.join(packages, "remote-1.0.0.pdpkg"), {
        "package.json": json.dumps({
            "schema": 1, "id": "remote", "version": "1.0.0", "platform": "win-x64",
            "source": {"kind": "tar.gz", "url": f"http://127.0.0.1:{port}/remote-1.0.0.tar.gz", "sha256": remote_hash},
            "unpack": {"kind": "tar.gz"},
            "asserts": [{"kind": "exists", "path": "bin/remote.txt"}],
        }).encode(),
    })

    make_zip(os.path.join(packages, "badhash-1.0.0.pdpkg"), {
        "package.json": json.dumps({
            "schema": 1, "id": "badhash", "version": "1.0.0",
            "source": {"kind": "tar.gz", "url": f"http://127.0.0.1:{port}/remote-1.0.0.tar.gz",
                       "sha256": "0" * 64},
        }).encode(),
    })

    evil = os.path.join(served, "evil.tar.gz")
    make_targz(evil, {"../escaped.txt": b"should never land outside\n"})
    make_zip(os.path.join(packages, "evil-1.0.0.pdpkg"), {
        "package.json": json.dumps({
            "schema": 1, "id": "evil", "version": "1.0.0",
            "source": {"kind": "tar.gz", "url": f"http://127.0.0.1:{port}/evil.tar.gz", "sha256": sha256(evil)},
        }).encode(),
    })

    make_zip(os.path.join(packages, "platform-1.0.0.pdpkg"), {
        "package.json": json.dumps({
            "schema": 1, "id": "platform", "version": "1.0.0", "platform": "linux-x64",
        }).encode(),
        "files/bin/x.txt": b"x\n",
    })

    make_zip(os.path.join(packages, "localhash-1.0.0.pdpkg"), {
        "package.json": json.dumps({
            "schema": 1, "id": "localhash", "version": "1.0.0", "platform": "win-x64",
        }).encode(),
        "files/bin/hash.txt": b"hashed\n",
    })

    broken = os.path.join(served, "broken-1.0.0.tar.gz")
    make_targz(broken, {"bin/missing.txt": b"x"})
    make_zip(os.path.join(packages, "broken-1.0.0.pdpkg"), {
        "package.json": json.dumps({
            "schema": 1, "id": "broken", "version": "1.0.0",
            "source": {"kind": "tar.gz", "url": f"http://127.0.0.1:{port}/broken-1.0.0.tar.gz",
                       "sha256": sha256(broken)},
            "asserts": [{"kind": "exists", "path": "bin/absent.txt"}],
        }).encode(),
    })

    symlink = os.path.join(served, "symlink.tar.gz")
    with tarfile.open(symlink, "w:gz") as archive:
        link = tarfile.TarInfo("link")
        link.type = tarfile.SYMTYPE
        link.linkname = "../escaped-symlink.txt"
        archive.addfile(link)
        payload = b"pwned\n"
        entry = tarfile.TarInfo("link/pwned.txt")
        entry.size = len(payload)
        archive.addfile(entry, io.BytesIO(payload))
    make_zip(os.path.join(packages, "symlink-1.0.0.pdpkg"), {
        "package.json": json.dumps({
            "schema": 1, "id": "symlink", "version": "1.0.0",
            "source": {"kind": "tar.gz", "url": f"http://127.0.0.1:{port}/symlink.tar.gz",
                       "sha256": sha256(symlink)},
        }).encode(),
    })


def prepare(root):
    builtins = {"storage": os.path.join("app", "extensions", "storage"),
                "rollback": os.path.join("app", "extensions", "rollback"),
                "log": os.path.join("tests", "fixtures", "log-ext"),
                "download": os.path.join("app", "extensions", "download"),
                "unpack": os.path.join("app", "extensions", "unpack"),
                "pkgmgr": os.path.join("app", "extensions", "pkgmgr")}
    for name, source in builtins.items():
        target = os.path.join(root, "extensions", name)
        shutil.rmtree(target, ignore_errors=True)
        shutil.copytree(source, target)
    for directory in ("store", "current", "staging"):
        shutil.rmtree(os.path.join(root, directory), ignore_errors=True)
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent"):
        shutil.rmtree(os.path.join(root, "state", directory), ignore_errors=True)
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)
    shutil.rmtree(os.path.join(root, "state", "logs"), ignore_errors=True)
    os.makedirs(os.path.join(root, "state", "logs"), exist_ok=True)
    shutil.rmtree(os.path.join(root, "state", "sources"), ignore_errors=True)
    os.makedirs(os.path.join(root, "state", "sources"), exist_ok=True)
    os.makedirs(os.path.join(root, "config"), exist_ok=True)
    try:
        os.remove(os.path.join(root, "config", "sources.json"))
    except OSError:
        pass


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
    time.sleep(0.5)


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


def store_dirs(root):
    store = os.path.join(root, "store")
    return sorted(name for name in os.listdir(store) if os.path.isdir(os.path.join(store, name)))


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ".run"
    kill_leftovers(root)
    time.sleep(0.5)
    prepare(root)
    served = os.path.join(root, "pkgfix")
    packages = os.path.join(root, "pkgs")
    build_fixtures(root, served, packages)

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=served, **kwargs)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    build_fixtures(root, served, packages, port)

    kernel, log = start_kernel(root)
    try:
        hello = os.path.join(packages, "hello-1.0.0.pdpkg")
        before = store_dirs(root)

        print("dry-run")
        result = pd(root, "--json", "install", hello, "--dry-run")
        done = done_of(result.stdout)
        check("dry-run exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("dry-run reports plan", (done or {}).get("metrics", {}).get("dryRun") is True, done)
        check("dry-run does not adopt", store_dirs(root) == before, store_dirs(root))

        print("install from .pdpkg")
        result = pd(root, "--json", "install", hello)
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("install exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:], result.stderr[-300:]))
        store_path = metrics.get("storePath", "")
        check("store dir created", os.path.isdir(store_path), store_path)
        check("content unpacked", os.path.isfile(os.path.join(store_path, "bin", "hello.txt")), store_path)
        link = os.path.join(root, "current", "hello")
        check("current pointer set", os.path.realpath(link) == os.path.realpath(store_path), (os.path.realpath(link), store_path))
        record = json.load(open(os.path.join(root, "state", "records", "hello.json"), encoding="utf-8"))
        check("record written", record.get("version") == "1.0.0" and record.get("storePath") == store_path, record)
        check("staging cleaned", os.listdir(os.path.join(root, "staging")) == [], os.listdir(os.path.join(root, "staging")))

        print("platform and local --sha256")
        platform_pkg = os.path.join(packages, "platform-1.0.0.pdpkg")
        result = pd(root, "--json", "install", platform_pkg)
        done = done_of(result.stdout)
        check("platform mismatch rejected", result.returncode == 11, (result.returncode, result.stdout[-250:]))
        check("platform mismatch code", (done or {}).get("error", {}).get("code") == "ERR_PKG_PLATFORM", done)
        hash_pkg = os.path.join(packages, "localhash-1.0.0.pdpkg")
        result = pd(root, "--json", "install", hash_pkg, "--sha256", "0" * 64)
        done = done_of(result.stdout)
        check("local sha256 mismatch rejected", result.returncode == 3, (result.returncode, result.stdout[-250:]))
        check("local sha256 mismatch code", (done or {}).get("error", {}).get("code") == "ERR_HASH", done)
        result = pd(root, "--json", "install", hash_pkg, "--sha256", sha256(hash_pkg))
        check("local sha256 match installs", result.returncode == 0, (result.returncode, result.stdout[-300:]))

        print("install from a directory")
        result = pd(root, "--json", "install", os.path.join(packages, "dirpkg"))
        done = done_of(result.stdout)
        check("dir install exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        dir_metrics = (done or {}).get("metrics") or {}
        check("dir install adopted", os.path.isdir(dir_metrics.get("storePath", "")), dir_metrics.get("storePath"))

        print("install with a remote payload")
        result = pd(root, "--json", "install", os.path.join(packages, "remote-1.0.0.pdpkg"))
        done = done_of(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("remote install exit 0", result.returncode == 0, (result.returncode, result.stdout[-400:]))
        remote_store = metrics.get("storePath", "")
        check("remote content present", os.path.isfile(os.path.join(remote_store, "bin", "remote.txt")), remote_store)
        blobs = os.listdir(os.path.join(root, "cache", "blobs"))
        check("blob cached by hash", len(blobs) >= 1 and all(len(name) == 64 for name in blobs), blobs)

        print("install a .pdpkg by URL")
        url_pkg = f"http://127.0.0.1:{port}/hello-copy.pdpkg"
        shutil.copyfile(hello, os.path.join(served, "hello-copy.pdpkg"))
        result = pd(root, "--json", "install", url_pkg, "--sha256", sha256(hello))
        done = done_of(result.stdout)
        check("url install exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("url install reused store", (done or {}).get("metrics", {}).get("existed") is True, done)

        print("hash mismatch")
        bad = os.path.join(packages, "badhash-1.0.0.pdpkg")
        result = pd(root, "--json", "install", bad)
        done = done_of(result.stdout)
        check("bad hash exit 3", result.returncode == 3, (result.returncode, result.stdout[-300:]))
        check("bad hash code", (done or {}).get("error", {}).get("code") == "ERR_HASH", done)
        check("bad hash left no store dir", not any(name.endswith("-badhash-1.0.0") for name in store_dirs(root)), store_dirs(root))

        print("path traversal")
        result = pd(root, "--json", "install", os.path.join(packages, "evil-1.0.0.pdpkg"))
        done = done_of(result.stdout)
        check("traversal rejected", result.returncode == 11, (result.returncode, result.stdout[-300:]))
        check("traversal code", (done or {}).get("error", {}).get("code") == "ERR_UNSUPPORTED", done)
        check("nothing escaped", not os.path.isfile(os.path.join(root, "staging", "escaped.txt")), None)

        print("archive with a symlink entry")
        result = pd(root, "--json", "install", os.path.join(packages, "symlink-1.0.0.pdpkg"))
        done = done_of(result.stdout)
        check("symlink archive rejected", result.returncode == 11, (result.returncode, result.stdout[-300:]))
        check("symlink archive code", (done or {}).get("error", {}).get("code") == "ERR_UNSUPPORTED", done)
        check("symlink target not written", not os.path.isfile(os.path.join(root, "escaped-symlink.txt")), None)
        check("symlink left no record", not os.path.isfile(os.path.join(root, "state", "records", "symlink.json")), None)

        print("assert failure rolls back")
        result = pd(root, "--json", "install", os.path.join(packages, "broken-1.0.0.pdpkg"))
        done = done_of(result.stdout)
        check("assert failure exit 12", result.returncode == 12, (result.returncode, result.stdout[-300:]))
        check("assert failure code", (done or {}).get("error", {}).get("code") == "ERR_ASSERT", done)
        check("assert failure left no record", not os.path.isfile(os.path.join(root, "state", "records", "broken.json")), None)
        check("assert failure cleaned staging", os.listdir(os.path.join(root, "staging")) == [], os.listdir(os.path.join(root, "staging")))

        print("unknown package name")
        result = pd(root, "--json", "install", "firefox")
        done = done_of(result.stdout)
        check("unknown name resolves to 20", result.returncode == 20 and (done or {}).get("error", {}).get("code") == "ERR_PKG_NOT_FOUND", done)

        print("audit recorded the install")
        result = pd(root, "log", "--grep", "storage.adopt", "--tail", "10")
        check("storage logged adopt", result.returncode == 0 and "adopted" in result.stdout, result.stdout[-200:])
    finally:
        server.shutdown()
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
