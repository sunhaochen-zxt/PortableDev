#!/usr/bin/env python3
"""pd doctor + pd selftest e2e.

- doctor reports a healthy stick, flags a deleted built-in extension, restores it with --fix
  and clears stale intents;
- selftest runs the assertions stored on the record, honours --only/--full and exits 12/20.

Usage: python -u tools/features-e2e.py [stick-root]
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
PRODUCT_EXTENSIONS = ["storage", "rollback", "download", "unpack", "pkgmgr", "proc"]

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


def connect(root, timeout=30):
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


def call(stream, argv, timeout=180, events=None):
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
        event = env.get("data") or {}
        if events is not None:
            events.append(event)
        if event.get("type") == "done":
            return event
    return None


def default_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(os.path.dirname(os.path.dirname(here)), ".run-features")


def deploy(root):
    """Builtin extensions + their pristine baseline + a fake bundled runtime for doctor."""
    app_ext = os.path.join(root, "app", "extensions")
    baseline = os.path.join(root, "app", "baseline", "extensions")
    for name in PRODUCT_EXTENSIONS:
        shutil.copytree(os.path.join("app", "extensions", name), os.path.join(app_ext, name), dirs_exist_ok=True)
        shutil.copytree(os.path.join("app", "extensions", name), os.path.join(baseline, name), dirs_exist_ok=True)
    shutil.copytree(os.path.join("tests", "fixtures", "log-ext"), os.path.join(app_ext, "log"), dirs_exist_ok=True)
    shutil.copytree(os.path.join("tests", "fixtures", "log-ext"), os.path.join(baseline, "log"), dirs_exist_ok=True)
    runtime = os.path.join(root, "app", "runtime")
    os.makedirs(os.path.join(runtime, "node"), exist_ok=True)
    os.makedirs(os.path.join(runtime, "shared", "Microsoft.NETCore.App", "8.0.0"), exist_ok=True)
    open(os.path.join(runtime, "dotnet.exe"), "w", encoding="utf-8").close()
    open(os.path.join(runtime, "node", "node.exe"), "w", encoding="utf-8").close()
    os.makedirs(os.path.join(root, "state", "wire"), exist_ok=True)
    with open(os.path.join(root, "state", "wire", "fake-mode"), "w", encoding="utf-8") as handle:
        handle.write("1\n")
    for directory in ("store", "current", "staging", "packages"):
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent", "wire", "logs", "tmp", "sources"):
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)


def check_row(done, name):
    checks = (done or {}).get("metrics", {}).get("checks") or []
    for item in checks:
        if item.get("check") == name:
            return item
    return None


def make_pkg(directory, pkg_id, extra=None, version="1.0.0"):
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(os.path.join(directory, "files"))
    manifest = {"schema": 1, "id": pkg_id, "version": version, "platform": "win-x64"}
    if extra:
        manifest.update(extra)
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle)
    with open(os.path.join(directory, "files", "marker.txt"), "w", encoding="utf-8") as handle:
        handle.write(pkg_id)
    return directory


def make_pdpkg(directory, pkg_id, version):
    """Directories are only scanned for *.pdpkg, so build a zip package for source tests."""
    build = os.path.join(directory, pkg_id + "-build")
    make_pkg(build, pkg_id, version=version)
    dest = os.path.join(directory, pkg_id + "-" + version + ".pdpkg")
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as handle:
        for base, _dirs, files in os.walk(build):
            for name in files:
                full = os.path.join(base, name)
                handle.write(full, os.path.relpath(full, build).replace(os.sep, "/"))
    shutil.rmtree(build, ignore_errors=True)
    return dest


def set_stable_root(root):
    """Point the stable root at the hermetic root so current links and doctor agree."""
    path = os.path.join(root, "config", "settings.json")
    with open(path, "r", encoding="utf-8") as handle:
        settings = json.load(handle)
    settings.setdefault("root", {})["stableRoot"] = root
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(settings, handle)


def bad_rows(done):
    return [(item.get("check"), str(item.get("detail"))[:80]) for item in (done.get("metrics") or {}).get("checks") or []
            if item.get("ok") is False]


def find_link_key(links, needle):
    needle = needle.lower()
    for key in links:
        if needle in key.lower():
            return key
    return None


def prepare_recovery_orphan(root):
    """Recovery must drop this intent (policy=drop) and hand its task to the rollback extension."""
    settings_path = os.path.join(root, 'config', 'settings.json')
    with open(settings_path, 'r', encoding='utf-8') as handle:
        settings = json.load(handle)
    settings.setdefault('recovery', {})['intentDefault'] = 'drop'
    with open(settings_path, 'w', encoding='utf-8') as handle:
        json.dump(settings, handle)
    target = os.path.join(root, 'store', 'orphan-task')
    os.makedirs(target, exist_ok=True)
    intents = os.path.join(root, 'state', 'intent')
    os.makedirs(intents, exist_ok=True)
    with open(os.path.join(intents, 'orphan-task.json'), 'w', encoding='utf-8') as handle:
        json.dump({'phase': 'begin', 'task': 'orphan-task', 'pkg': 'ghostpkg', 'target': target}, handle)
    return target


def installed_ids(root):
    return sorted(os.path.splitext(name)[0] for name in os.listdir(os.path.join(root, "state", "records")) if name.endswith(".json"))


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else default_root()
    print(f"stick root: {root}")
    shutil.rmtree(root, ignore_errors=True)
    subprocess.run([DOTNET, KERNEL, "--root", root, "--init", "--once"],
                   check=True, capture_output=True, text=True)
    deploy(root)
    set_stable_root(root)
    orphan_target = prepare_recovery_orphan(root)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.dirname(DOTNET)
    # The fake app/runtime/node/node.exe only satisfies doctor's existence check; extensions must
    # run with a real Node, so override the bundled path for this hermetic root.
    real_node = shutil.which("node")
    if not real_node:
        for candidate in (r"C:\Dev\tools\nodejs\26.13.0\node.exe",):
            if os.path.isfile(candidate):
                real_node = candidate
                break
    if real_node:
        env["PD_NODE"] = real_node
    # Same for the dotnet-hosted extension (proc): the fake app/runtime/dotnet.exe only exists so
    # doctor's existence check passes, never to execute anything.
    env["PD_DOTNET"] = DOTNET
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    child = subprocess.Popen([DOTNET, KERNEL, "--root", root], stdout=log, stderr=subprocess.STDOUT, env=env)
    stream = None
    try:
        stream = connect(root)
        send(stream, {"v": 1, "kind": "hello", "data": {
            "protocol": 1, "client": "features-e2e", "clientVersion": "0.0.1", "features": ["json"]}})
        hello = recv(stream)
        check("hello.ok", hello and hello.get("kind") == "hello.ok", hello)

        print("recovery: dropped intent triggers an automatic ledger rollback")
        check("orphan staging dropped", not os.path.isdir(orphan_target), orphan_target)
        check("orphan intent settled", not os.path.exists(os.path.join(root, "state", "intent", "orphan-task.json")),
              os.listdir(os.path.join(root, "state", "intent")))
        done = call(stream, ["rollback", "--task", "orphan-task", "--dry-run"])
        check("recovery rollback recorded in the ledger", done and done.get("exitCode") == 0, done)

        print("doctor: healthy stick")
        done = call(stream, ["doctor"])
        check("healthy exit 0", done and done.get("exitCode") == 0, done)
        check("healthy no issues", done and done.get("metrics", {}).get("issues") == 0, done)

        print("doctor: deleted built-in extension is reported, not silently restored")
        unpack = os.path.join(root, "app", "extensions", "unpack")
        shutil.rmtree(unpack, ignore_errors=True)
        done = call(stream, ["doctor", "--json"], events=[])
        row = check_row(done, "内置扩展")
        check("missing extension reported", row is not None and row.get("ok") is False, row)
        check("doctor did not repair without --fix", not os.path.isdir(unpack), os.listdir(os.path.join(root, "app", "extensions")))

        print("doctor --fix restores from the baseline")
        done = call(stream, ["doctor", "--fix"])
        check("fix exit 0", done and done.get("exitCode") == 0, done)
        check("fixed count", done and done.get("metrics", {}).get("fixed", 0) >= 1, done)
        check("extension restored", os.path.isfile(os.path.join(unpack, "ext.json")), os.listdir(unpack) if os.path.isdir(unpack) else "missing")
        done = call(stream, ["doctor"])
        check("healthy again", done and done.get("metrics", {}).get("issues") == 0, done)

        print("doctor: stale intent is reported and cleaned")
        stale = os.path.join(root, "state", "intent", "stale.json")
        with open(stale, "w", encoding="utf-8") as handle:
            json.dump({"pkg": "stale", "phase": "done"}, handle)
        done = call(stream, ["doctor"])
        row = check_row(done, "残留 intent")
        check("intent reported", row is not None and row.get("ok") is False, row)
        done = call(stream, ["doctor", "--fix"])
        check("stale intent removed", not os.path.exists(stale), done)

        print("selftest: stored assertions pass")
        good = make_pkg(os.path.join(root, "pkgs", "good"), "goodpkg", {"asserts": [
            {"kind": "exists", "path": "marker.txt"},
            {"kind": "command", "run": "cmd /c echo hello-assert", "expect": "hello-assert", "timeout": "10s"},
        ]})
        done = call(stream, ["install", good])
        check("install with command assert", done and done.get("exitCode") == 0, done)
        done = call(stream, ["selftest"])
        check("selftest ok", done and done.get("ok") is True, done)
        check("selftest passed count", done and done.get("metrics", {}).get("passed", 0) >= 2, done)
        done = call(stream, ["selftest", "--only", "goodpkg"])
        check("selftest --only", done and done.get("ok") is True, done)
        done = call(stream, ["selftest", "--only", "nope"])
        check("selftest unknown --only exits 20", done and done.get("exitCode") == 20
              and (done.get("error") or {}).get("code") == "ERR_PKG_NOT_FOUND", done)

        print("selftest: failing command assertion fails selftest with 12")
        bad = make_pkg(os.path.join(root, "pkgs", "bad"), "badpkg", {"asserts": [
            {"kind": "command", "run": "cmd /c echo nope", "expect": "expected-text"},
        ]})
        done = call(stream, ["install", bad])
        check("install with failing assert exits 12", done and done.get("exitCode") == 12
              and (done.get("error") or {}).get("code") == "ERR_ASSERT", done)
        check("failed install rolled back", "badpkg" not in installed_ids(root), installed_ids(root))

        print("selftest --full runs the manifest selftest workflow")
        full = make_pkg(os.path.join(root, "pkgs", "full"), "fullpkg", {"selftest": [
            {"name": "workflow", "run": "cmd /c echo full-ok", "expect": "full-ok", "timeout": "10s"},
        ]})
        done = call(stream, ["install", full])
        check("install full-only package", done and done.get("exitCode") == 0, done)
        done = call(stream, ["selftest", "--full"])
        check("selftest --full ok", done and done.get("ok") is True, done)
        check("selftest --full passed count", done and done.get("metrics", {}).get("passed", 0) >= 1, done)

        print("info/env: wiring is reported")
        wired = make_pkg(os.path.join(root, "pkgs", "wired"), "wiredpkg", {
            "path": ["bin"], "env": {"PD_TEST_MARK": "42"},
        })
        done = call(stream, ["install", wired])
        check("install wired", done and done.get("exitCode") == 0, done)
        wire_file = os.path.join(root, "state", "wire", "fake.json")
        with open(wire_file, "r", encoding="utf-8") as handle:
            wired_state = json.load(handle)
        check("install wired PATH entry", "wiredpkg" in str(wired_state.get("path")), wired_state.get("path"))
        events = []
        done = call(stream, ["info", "wiredpkg"], events=events)
        kv = next((item for item in events if item.get("type") == "kv"
                   and (item.get("items") or {}).get("id") == "wiredpkg"), None)
        check("info exit 0", done and done.get("exitCode") == 0, done)
        check("info kv", kv is not None and (kv.get("items") or {}).get("version") == "1.0.0", kv)
        check("info wiring count", done and (done.get("metrics") or {}).get("wiring", 0) >= 2, done)
        done = call(stream, ["info", "nope"])
        check("info unknown exits 20", done and done.get("exitCode") == 20, done)
        done = call(stream, ["env"])
        metrics = (done or {}).get("metrics") or {}
        check("env metrics", done and done.get("exitCode") == 0
              and metrics.get("path", 0) >= 1 and metrics.get("env", 0) >= 1, done)

        print("outdated: local source with a newer build")
        source_dir = os.path.join(root, "src-new")
        os.makedirs(source_dir, exist_ok=True)
        make_pdpkg(source_dir, "goodpkg", "2.0.0")
        done = call(stream, ["source", "add", "localdev", source_dir, "--kind", "dir", "--priority", "10"])
        check("source add", done and done.get("exitCode") == 0, done)
        events = []
        done = call(stream, ["outdated"], events=events)
        rows = []
        for item in events:
            if item.get("type") == "table" and item.get("title") == "outdated":
                rows = item.get("rows") or []
        check("outdated exit 0", done and done.get("exitCode") == 0, done)
        check("outdated lists goodpkg 2.0.0",
              any(len(row) >= 3 and row[0] == "goodpkg" and row[2] == "2.0.0" for row in rows), rows)

        print("doctor --deep: stable root, current links, blobs, sources and filesystem")
        blob_dir = os.path.join(root, "cache", "blobs", "ab")
        os.makedirs(blob_dir, exist_ok=True)
        with open(os.path.join(blob_dir, "blob1"), "wb") as handle:
            handle.write(b"blob-data")
        source2 = os.path.join(root, "src-old")
        os.makedirs(source2, exist_ok=True)
        make_pdpkg(source2, "goodpkg", "1.5.0")
        done = call(stream, ["source", "add", "localdev2", source2, "--kind", "dir", "--priority", "5"])
        check("source add lower priority", done and done.get("exitCode") == 0, done)
        done = call(stream, ["doctor", "--deep"])
        rows = {item.get("check"): item for item in (done.get("metrics") or {}).get("checks") or []}
        check("deep exit 0", done and done.get("exitCode") == 0, done)
        for name in ("稳定根", "current 接线", "blob 缓存", "源冲突", "文件系统"):
            check("deep check present: " + name, name in rows, sorted(rows))
        check("deep filesystem is NTFS", rows.get("文件系统", {}).get("ok") is True, rows.get("文件系统"))
        check("deep reports shadowed goodpkg", "goodpkg" in str(rows.get("源冲突", {}).get("detail", "")),
              rows.get("源冲突"))
        check("deep no issues", (done.get("metrics") or {}).get("issues") == 0, bad_rows(done))
        check("deep blob count", "1 个文件" in str((rows.get("blob 缓存") or {}).get("detail", "")), rows.get("blob 缓存"))

        print("ext: list, disable/enable and update")
        events = []
        done = call(stream, ["ext", "list"], events=events)
        rows = next((item.get("rows") for item in events if item.get("type") == "table"), [])
        check("ext list exit 0", done and done.get("exitCode") == 0, done)
        check("ext list rows", len(rows or []) >= 6, len(rows or []))
        log_manifest = os.path.join(root, "app", "extensions", "log", "ext.json")
        done = call(stream, ["ext", "disable", "log"])
        with open(log_manifest, "r", encoding="utf-8") as handle:
            disabled = json.load(handle)
        check("ext disable", (done.get("metrics") or {}).get("enabled") is False
              and disabled.get("enabled") is False, done)
        done = call(stream, ["ext", "enable", "log"])
        with open(log_manifest, "r", encoding="utf-8") as handle:
            enabled = json.load(handle)
        check("ext enable", (done.get("metrics") or {}).get("enabled") is True
              and enabled.get("enabled") is True, done)
        download_source = os.path.abspath(os.path.join("app", "extensions", "download"))
        done = call(stream, ["ext", "update", "download", download_source])
        check("ext update", done and done.get("exitCode") == 0
              and (done.get("metrics") or {}).get("restartRequired") is True, done)

        print("recover: settles orphan staging")
        orphan = os.path.join(root, "staging", "orphan-" + str(int(time.time())))
        os.makedirs(orphan, exist_ok=True)
        done = call(stream, ["recover"])
        check("recover exit 0", done and done.get("exitCode") == 0, done)
        check("recover deletes orphan staging",
              (done.get("metrics") or {}).get("stagingDeleted", 0) >= 1 and not os.path.isdir(orphan), done)

        print("subcommand granularity: source list resolves as its own read-only command")
        events = []
        done = call(stream, ["complete", "source"], events=events)
        rows = next((item.get("rows") for item in events if item.get("type") == "table"), [])
        names = [row[0] for row in rows if row]
        check("complete includes source list", "source list" in names, names[:10])
        done = call(stream, ["source", "list"])
        check("source list runs", done and done.get("exitCode") == 0, done)
        done = call(stream, ["source", "status"])
        check("source fallback validates subcommand", done and done.get("exitCode") == 2, done)

        print("doctor: external PATH/env drift is detected and repaired")
        fake_path = os.path.join(root, "state", "wire", "fake.json")
        with open(fake_path, "r", encoding="utf-8") as handle:
            fake = json.load(handle)
        ghost = os.path.join(root, "current", "ghost", "bin")
        fake["path"] = (fake.get("path") or "") + ";" + ghost
        fake.setdefault("env", {})["PD_TEST_MARK"] = "tampered"
        with open(fake_path, "w", encoding="utf-8") as handle:
            json.dump(fake, handle)
        done = call(stream, ["doctor"])
        rows = {item.get("check"): item for item in (done.get("metrics") or {}).get("checks") or []}
        check("drift PATH reported", (rows.get("PATH 接线") or {}).get("ok") is False, rows.get("PATH 接线"))
        check("drift env reported", (rows.get("环境变量") or {}).get("ok") is False, rows.get("环境变量"))
        check("drift raises issues", (done.get("metrics") or {}).get("issues", 0) >= 2, done)
        done = call(stream, ["doctor", "--fix"])
        check("drift fix exit 0", done and done.get("exitCode") == 0, done)
        done = call(stream, ["doctor"])
        check("drift repaired", (done.get("metrics") or {}).get("issues") == 0, bad_rows(done))
        with open(fake_path, "r", encoding="utf-8") as handle:
            after_fix = json.load(handle)
        check("stale PATH entry removed", "ghost" not in str(after_fix.get("path")), after_fix.get("path"))

        print("home: data link creates a home junction that doctor checks and repairs")
        home_pkg = make_pkg(os.path.join(root, "pkgs", "homepkg"), "homepkg", {
            "data": [{"kind": "link", "source": "{{HOME}}\\cfg", "target": os.path.join(root, "appdata-home")}],
        })
        done = call(stream, ["install", home_pkg])
        check("install home package", done and done.get("exitCode") == 0, done)
        done = call(stream, ["doctor"])
        rows = {item.get("check"): item for item in (done.get("metrics") or {}).get("checks") or []}
        check("home junction linked", (rows.get("家目录联接") or {}).get("ok") is True, rows.get("家目录联接"))
        with open(fake_path, "r", encoding="utf-8") as handle:
            fake = json.load(handle)
        links = fake.get("links") or {}
        home_key = find_link_key(links, os.path.join("home", "homepkg"))
        check("fake home link recorded", home_key is not None, sorted(links))
        if home_key is not None:
            del links[home_key]
        with open(fake_path, "w", encoding="utf-8") as handle:
            json.dump(fake, handle)
        done = call(stream, ["doctor"])
        rows = {item.get("check"): item for item in (done.get("metrics") or {}).get("checks") or []}
        check("broken home junction reported", (rows.get("家目录联接") or {}).get("ok") is False, rows.get("家目录联接"))
        done = call(stream, ["doctor", "--fix"])
        check("home fix exit 0", done and done.get("exitCode") == 0, done)
        with open(fake_path, "r", encoding="utf-8") as handle:
            links = json.load(handle).get("links") or {}
        check("home junction restored", find_link_key(links, os.path.join("home", "homepkg")) is not None, sorted(links))

        print("doctor: restricted session probe")
        tmp_dir = os.path.join(root, "state", "tmp")
        moved_dir = tmp_dir + "-moved"
        os.rename(tmp_dir, moved_dir)
        with open(tmp_dir, "w", encoding="utf-8") as handle:
            handle.write("not a directory")
        done = call(stream, ["doctor"])
        rows = {item.get("check"): item for item in (done.get("metrics") or {}).get("checks") or []}
        check("restricted session reported", (rows.get("受限会话") or {}).get("ok") is False, rows.get("受限会话"))
        check("restricted session hint", "非受限" in str((rows.get("受限会话") or {}).get("detail", "")), rows.get("受限会话"))
        os.remove(tmp_dir)
        os.rename(moved_dir, tmp_dir)

    finally:
        try:
            call(stream, ["kernel", "stop"], timeout=30) if stream is not None else None
        except Exception:
            pass
        try:
            child.wait(timeout=20)
        except subprocess.TimeoutExpired:
            child.kill()
        log.close()
    print(f"\n{PASS} passed, {FAIL} failed")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())