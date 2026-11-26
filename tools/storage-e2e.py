#!/usr/bin/env python3
"""End-to-end tests for the storage extension: grants, adoption, records, current, gc, ledger, recovery.

Usage: python tools/storage-e2e.py [stick-root]
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time

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


def pd(root, *argv, timeout=90):
    command = [DOTNET, PD_DLL, "--root", root] + list(argv)
    environment = dict(os.environ)
    environment.pop("PD_KERNEL", None)
    return subprocess.run(
        command, capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=environment, timeout=timeout,
    )


def collect_audit(root):
    """Concatenate every provider file whose path mentions audit (implementations store it differently)."""
    text = ""
    for base, _dirs, names in os.walk(os.path.join(root, "state", "logs", "providers")):
        for name in names:
            full = os.path.join(base, name)
            if "audit" not in full.lower():
                continue
            try:
                text += open(full, encoding="utf-8").read()
            except OSError:
                pass
    return text


def done_metrics(stdout):
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


LOG_PROVIDER = os.environ.get("PD_LOG_FIXTURE", "log-ext")


def prepare(root):
    builtins = {
        "storage": os.path.join("app", "extensions", "storage"),
        "rollback": os.path.join("app", "extensions", "rollback"),
    }
    fixtures = {"storeprobe": "storage-probe", "log": LOG_PROVIDER}
    for name in ("storage", "rollback", "storeprobe", "log"):
        target = os.path.join(root, "extensions", name)
        shutil.rmtree(target, ignore_errors=True)
        source = builtins.get(name) or os.path.join("tests", "fixtures", fixtures[name])
        shutil.copytree(source, target)
    for directory in ("store", "current", "staging"):
        shutil.rmtree(os.path.join(root, directory), ignore_errors=True)
        os.makedirs(os.path.join(root, directory), exist_ok=True)
    for directory in ("records", "intent"):
        shutil.rmtree(os.path.join(root, "state", directory), ignore_errors=True)
        os.makedirs(os.path.join(root, "state", directory), exist_ok=True)
    shutil.rmtree(os.path.join(root, "state", "logs"), ignore_errors=True)
    os.makedirs(os.path.join(root, "state", "logs"), exist_ok=True)


def start_kernel(root):
    log = open(os.path.join(root, "kernel.out"), "w", encoding="utf-8")
    process = subprocess.Popen([DOTNET, KERNEL_DLL, "--root", root], stdout=log, stderr=subprocess.STDOUT, cwd=os.getcwd())
    deadline = time.time() + 20
    while time.time() < deadline:
        probe = pd(root, "version", timeout=15)
        if probe.returncode == 0:
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
    kernel, log = start_kernel(root)
    try:
        print("adopt")
        result = pd(root, "--json", "storeprobe", "run", "demo", "1.0.0")
        done = done_metrics(result.stdout)
        check("adopt exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:], result.stderr[-300:]))
        metrics = (done or {}).get("metrics") or {}
        store_path = metrics.get("storePath", "")
        check("store path shape", bool(re.search(r"[0-9a-f]{16}-demo-1\.0\.0$", store_path)), store_path)
        check("store dir exists", os.path.isdir(store_path), store_path)
        check("payload written", os.path.isfile(os.path.join(store_path, "a.txt")) and
              os.path.isfile(os.path.join(store_path, "bin", "tool.txt")), os.listdir(store_path) if os.path.isdir(store_path) else None)
        check("tree hash present", bool(re.search(r"^[0-9a-f]{64}$", metrics.get("treeHash", ""))), metrics.get("treeHash"))

        record_path = os.path.join(root, "state", "records", "demo.json")
        record = json.load(open(record_path, encoding="utf-8")) if os.path.isfile(record_path) else None
        check("record written", record is not None and record.get("storePath") == store_path, record)
        check("record version", (record or {}).get("version") == "1.0.0", record)

        link = os.path.join(root, "current", "demo")
        check("current pointer exists", os.path.exists(link), link)
        check("current resolves to store", os.path.realpath(link) == os.path.realpath(store_path),
              (os.path.realpath(link), os.path.realpath(store_path)))

        check("intents cleaned", os.listdir(os.path.join(root, "state", "intent")) == [], os.listdir(os.path.join(root, "state", "intent")))
        check("staging cleaned", os.listdir(os.path.join(root, "staging")) == [], os.listdir(os.path.join(root, "staging")))

        print("idempotent adopt")
        before = store_dirs(root)
        result = pd(root, "--json", "storeprobe", "run", "demo", "1.0.0")
        done = done_metrics(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("second adopt exit 0", result.returncode == 0, result.returncode)
        check("same store path", metrics.get("storePath") == store_path, metrics.get("storePath"))
        check("no duplicate store dir", store_dirs(root) == before, store_dirs(root))

        print("abort")
        result = pd(root, "--json", "storeprobe", "abort")
        done = done_metrics(result.stdout)
        check("abort exit 0", result.returncode == 0, result.returncode)
        check("abort removed staging", (done or {}).get("metrics", {}).get("removed") is True, done)
        check("staging empty after abort", os.listdir(os.path.join(root, "staging")) == [], os.listdir(os.path.join(root, "staging")))
        check("no extra store dir after abort", store_dirs(root) == before, store_dirs(root))

        print("grant expiry")
        result = pd(root, "--json", "storeprobe", "grant-expired")
        done = done_metrics(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("expired grant rejected", result.returncode == 0 and metrics.get("code") == "ERR_UNSUPPORTED", done)
        check("expired grant left no store dir", not any("expired" in name for name in store_dirs(root)), store_dirs(root))

        print("gc")
        orphan_staging = os.path.join(root, "staging", "ghost-task-work-1")
        orphan_store = os.path.join(root, "store", "deadbeefdeadbeef-ghost-1.0.0")
        os.makedirs(orphan_staging, exist_ok=True)
        os.makedirs(orphan_store, exist_ok=True)
        open(os.path.join(orphan_staging, "junk.txt"), "w", encoding="utf-8").write("junk")
        result = pd(root, "--json", "storeprobe", "gc")
        done = done_metrics(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("gc exit 0", result.returncode == 0, result.returncode)
        check("gc removed staging", metrics.get("stagingRemoved", 0) >= 1, metrics)
        check("gc removed store", metrics.get("storeRemoved", 0) >= 1, metrics)
        check("orphan staging gone", not os.path.exists(orphan_staging), orphan_staging)
        check("orphan store gone", not os.path.exists(orphan_store), orphan_store)
        check("real package survives gc", os.path.isdir(store_path), store_path)

        print("gc refuses corrupt records unless --force")
        corrupt_record = os.path.join(root, "state", "records", "corrupt-test.json")
        with open(corrupt_record, "w", encoding="utf-8") as handle:
            handle.write("{not valid json")
        result = pd(root, "--json", "storeprobe", "gc")
        done = done_metrics(result.stdout)
        check("gc without force fails on corrupt record", result.returncode != 0, (result.returncode, result.stdout[-200:]))
        result = pd(root, "--json", "gc", "--force")
        done = done_metrics(result.stdout)
        check("gc --force runs with corrupt record", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        os.remove(corrupt_record)

        print("gc respects generations")
        generations_dir = os.path.join(root, "state", "generations")
        gen_only_store = os.path.join(root, "store", "cafebabecafebabe-genonly-1.0.0")
        os.makedirs(gen_only_store, exist_ok=True)
        open(os.path.join(gen_only_store, "x.txt"), "w", encoding="utf-8").write("x")
        gen_file = os.path.join(generations_dir, "9000-manual.json")
        json.dump({
            "schema": 1, "seq": 9000, "at": "2026-01-01T00:00:00Z",
            "packages": {"genonly": {"id": "genonly", "version": "1.0.0",
                                     "storePath": os.path.abspath(gen_only_store)}},
            "wiring": {"path": [], "env": {}, "registry": [], "launchers": []},
        }, open(gen_file, "w", encoding="utf-8"))
        result = pd(root, "--json", "gc")
        check("gc keeps generation-referenced store", os.path.isdir(gen_only_store),
              (result.returncode, result.stdout[-200:]))
        os.remove(gen_file)
        result = pd(root, "--json", "gc")
        check("gc removes store once unreferenced", not os.path.isdir(gen_only_store),
              (result.returncode, result.stdout[-200:]))

        print("gc keep and blobs")
        blobs_dir = os.path.join(root, "cache", "blobs")
        os.makedirs(blobs_dir, exist_ok=True)
        fake_blob = "a" * 64
        open(os.path.join(blobs_dir, fake_blob), "w", encoding="utf-8").write("x")
        pd(root, "--json", "gc")
        check("blobs untouched without --blobs", os.path.isfile(os.path.join(blobs_dir, fake_blob)), None)
        result = pd(root, "--json", "gc", "--blobs")
        done = done_metrics(result.stdout)
        check("--blobs removes unreferenced blob",
              not os.path.isfile(os.path.join(blobs_dir, fake_blob)) and
              (done or {}).get("metrics", {}).get("blobsRemoved", 0) >= 1, done)

        for extra_seq in (9001, 9002):
            json.dump({"schema": 1, "seq": extra_seq, "at": "2026-01-01T00:00:00Z",
                       "packages": {}, "wiring": {"path": [], "env": {}, "registry": [], "launchers": []}},
                      open(os.path.join(generations_dir, str(extra_seq) + "-extra.json"), "w", encoding="utf-8"))

        before_files = sorted(name for name in os.listdir(generations_dir) if name.endswith(".json"))
        result = pd(root, "--json", "gc", "--keep", "1", "--dry-run")
        done = done_metrics(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("gc dry-run reports generation trims", metrics.get("generationsRemoved", 0) >= 1, metrics)
        check("gc dry-run deletes nothing",
              sorted(name for name in os.listdir(generations_dir) if name.endswith(".json")) == before_files, None)
        result = pd(root, "--json", "gc", "--keep", "1")
        check("gc trims generations",
              len([name for name in os.listdir(generations_dir) if name.endswith(".json")]) <= 1,
              os.listdir(generations_dir))

        print("ledger")
        result = pd(root, "--json", "storeprobe", "ledger")
        done = done_metrics(result.stdout)
        check("ledger exit 0", result.returncode == 0, result.returncode)
        check("ledger entries recorded", (done or {}).get("metrics", {}).get("entries", 0) >= 2, done)
        audit_text = collect_audit(root)
        check("audit stream written by provider", len(audit_text) > 0, audit_text[:120])
        check("audit has begin boundary", '"phase":"begin"' in audit_text, audit_text[:200])
        check("audit entries carry undo", '"undo"' in audit_text, audit_text[:200])

        print("rollback")
        result = pd(root, "--json", "storeprobe", "run", "demo", "2.0.0")
        done = done_metrics(result.stdout)
        v2 = (done or {}).get("metrics") or {}
        check("v2 install exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        record_v2 = json.load(open(record_path, encoding="utf-8")) if os.path.isfile(record_path) else {}
        check("record moved to v2", record_v2.get("version") == "2.0.0", record_v2)
        check("current moved to v2", os.path.realpath(link) == os.path.realpath(v2.get("storePath", "")), None)

        task2 = v2.get("task", "")
        check("probe reported its task id", bool(task2), v2)

        result = pd(root, "--json", "rollback", "--to", "999")
        done = done_metrics(result.stdout)
        check("missing generation exits 20", result.returncode == 20 and
              (done or {}).get("error", {}).get("code") == "ERR_PKG_NOT_FOUND", done)

        result = pd(root, "--json", "rollback", "--task", task2, "--dry-run")
        done = done_metrics(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("rollback dry-run exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("dry-run plans the undo actions", metrics.get("planned", 0) >= 2, metrics)
        record_after_plan = json.load(open(record_path, encoding="utf-8")) if os.path.isfile(record_path) else {}
        check("dry-run changes nothing", record_after_plan.get("version") == "2.0.0", record_after_plan)

        result = pd(root, "--json", "rollback", "--task", task2)
        done = done_metrics(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("rollback exit 0", result.returncode == 0, (result.returncode, result.stdout[-300:]))
        check("rollback reverted both entries", metrics.get("rolledBack", 0) >= 2 and metrics.get("failed", 1) == 0, metrics)
        record_v1 = json.load(open(record_path, encoding="utf-8")) if os.path.isfile(record_path) else {}
        check("record back to v1", record_v1.get("version") == "1.0.0", record_v1)
        check("current back to v1", os.path.realpath(link) == os.path.realpath(store_path), (os.path.realpath(link), store_path))
        check("rollback leaves store untouched", os.path.isdir(v2.get("storePath", "")), v2.get("storePath"))

        check("rollback progress recorded in audit", "rollback.step" in collect_audit(root), collect_audit(root)[-200:])

        result = pd(root, "--json", "rollback", "--task", task2)
        done = done_metrics(result.stdout)
        metrics = (done or {}).get("metrics") or {}
        check("second rollback is a no-op", result.returncode == 0 and metrics.get("rolledBack", -1) == 0, metrics)

        print("audit expiry")
        providers_dir = os.path.join(root, "state", "logs", "providers")
        audit_files = []
        if os.path.isdir(providers_dir):
            for name in os.listdir(providers_dir):
                candidate = os.path.join(providers_dir, name)
                if os.path.isdir(os.path.join(candidate, "audit")):
                    for audit_name in os.listdir(os.path.join(candidate, "audit")):
                        if audit_name.endswith(".jsonl"):
                            audit_files.append(os.path.join(candidate, "audit", audit_name))
                flat = os.path.join(candidate, "audit.jsonl")
                if os.path.isfile(flat):
                    audit_files.append(flat)
        for path_to_remove in audit_files:
            os.remove(path_to_remove)
        result = pd(root, "--json", "rollback", "--task", task2)
        done = done_metrics(result.stdout)
        check("rollback after audit vacuum exits 20", result.returncode == 20, (result.returncode, result.stdout[-300:]))
        check("rollback after audit vacuum code",
              (done or {}).get("error", {}).get("code") == "ERR_AUDIT_EXPIRED", done)

        print("log integration")
        result = pd(root, "log", "--grep", "storage.adopt", "--tail", "20")
        check("storage logged through provider", result.returncode == 0 and "adopted" in result.stdout, result.stdout[-300:])

        print("crash recovery")
        stop_kernel(root)
        recovery_target = os.path.join(root, "store", "cafebabecafebabe-recover-0.1.0")
        os.makedirs(recovery_target, exist_ok=True)
        open(os.path.join(recovery_target, "x.txt"), "w", encoding="utf-8").write("x")
        intent = {
            "schema": 1, "task": "t99", "pkg": "recover", "version": "0.1.0",
            "target": recovery_target, "phase": "begin",
        }
        open(os.path.join(root, "state", "intent", "t99.json"), "w", encoding="utf-8").write(json.dumps(intent))
        once = subprocess.run([DOTNET, KERNEL_DLL, "--root", root, "--once"], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=60)
        check("recovery run exit 0", once.returncode == 0, (once.returncode, once.stderr[-300:]))
        recovered = os.path.join(root, "state", "records", "recover.json")
        check("recovery adopted record", os.path.isfile(recovered), recovered)
        check("recovery cleared intent", not os.path.isfile(os.path.join(root, "state", "intent", "t99.json")), None)
        if os.path.isfile(recovered):
            data = json.load(open(recovered, encoding="utf-8"))
            # recovery normalizes the target to an absolute path, so compare normalized forms.
            check("recovered record points at store",
                  os.path.normcase(os.path.abspath(str(data.get("storePath")))) ==
                  os.path.normcase(os.path.abspath(recovery_target)), data)
    finally:
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
