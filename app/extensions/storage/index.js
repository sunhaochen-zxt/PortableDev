// Storage extension: the only writer of store/, current/ and state records.
// Audit entries go to the log provider (log.audit, durable) and carry their own undo instruction.
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { execFileSync } = require("child_process");
const { connect, ready } = require("./protocol");

const ROOT = process.env.PD_ROOT || ".";
const STATE = process.env.PD_STATE || path.join(ROOT, "state");
const STORE = path.join(ROOT, "store");
const STAGING = path.join(ROOT, "staging");
const CURRENT = path.join(ROOT, "current");
const RECORDS = path.join(STATE, "records");
const INTENTS = path.join(STATE, "intent");

for (const dir of [STORE, STAGING, CURRENT, RECORDS, INTENTS]) {
  fs.mkdirSync(dir, { recursive: true });
}

const grants = new Map();
const beganTasks = new Set();
const pendingCalls = new Map();
let grantCounter = 0;
let callCounter = 0;
let send = () => {};

function now() {
  return new Date().toISOString();
}

function StorageError(code, message) {
  this.code = code;
  this.message = message;
}

function realInsideStick(candidate) {
  try {
    const real = fs.realpathSync(candidate);
    return inside(ROOT, real) ? real : null;
  } catch (error) {
    return null;
  }
}

function inside(root, candidate) {
  const base = path.resolve(root).toLowerCase();
  const full = path.resolve(candidate).toLowerCase();
  if (full === base) {
    return false;
  }
  const relative = path.relative(base, full);
  return relative.length > 0 && !relative.startsWith("..") && !path.isAbsolute(relative);
}

// Package ids may start with a digit (scoop "7zip").
const PACKAGE_ID = /^[a-z0-9][a-z0-9-]{0,63}$/;
const VERSION = /^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$/;
const SHA256 = /^[0-9a-f]{64}$/;

function packageId(value, what) {
  const text = String(value === undefined || value === null ? "" : value);
  if (!PACKAGE_ID.test(text)) {
    throw new StorageError("ERR_PKG_INVALID", (what || "package id") + " is not a valid package id: " + JSON.stringify(text));
  }
  return text;
}

function packageVersion(value, what) {
  const text = String(value === undefined || value === null ? "" : value);
  if (!VERSION.test(text)) {
    throw new StorageError("ERR_PKG_INVALID", (what || "version") + " is not a valid version: " + JSON.stringify(text));
  }
  return text;
}

function sha256Hex(value) {
  const text = String(value || "").toLowerCase();
  if (!SHA256.test(text)) {
    throw new StorageError("ERR_PKG_INVALID", "contentHash must be a 64-character sha256");
  }
  return text;
}

function envName(value) {
  const text = String(value || "");
  if (!/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(text)) {
    throw new StorageError("ERR_BAD_ARGS", "invalid environment variable name: " + JSON.stringify(text));
  }
  return text;
}

function parseTtlSeconds(value) {
  const text = String(value || "10m").trim().toLowerCase();
  const match = /^(\d+)(s|m|h)?$/.exec(text);
  if (!match) {
    return 600;
  }
  const amount = Number(match[1]);
  const unit = match[2] || "s";
  const seconds = unit === "h" ? amount * 3600 : unit === "m" ? amount * 60 : amount;
  return Math.min(Math.max(seconds, 1), 3600);
}

function grantFor(stagingPath, caller) {
  const full = path.resolve(stagingPath);
  let entry = null;
  let key = null;
  for (const [name, candidate] of grants) {
    const root = path.resolve(candidate.grant.path);
    if (full === root || inside(root, full)) {
      entry = candidate;
      key = name;
      break;
    }
  }

  if (!entry) {
    return { ok: false, reason: "staging path was not issued by storage (missing grant)" };
  }
  if (Date.now() > entry.expiresAt) {
    grants.delete(key);
    return { ok: false, reason: "grant expired: " + key };
  }
  if (entry.owner && caller && entry.owner !== caller) {
    return { ok: false, reason: "grant belongs to another extension: " + key };
  }
  return { ok: true };
}

function safeTask(value) {
  const text = String(value === undefined || value === null ? "" : value);
  return text.replace(/[^A-Za-z0-9._-]/g, "_").slice(0, 64);
}

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(value, null, 2) + "\n");
}

let writeCounter = 0;

// State files (intent/records/generations) must survive a crash half-way: write temp, fsync, rename.
function writeJsonAtomic(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  writeCounter += 1;
  const temp = file + ".tmp-" + process.pid + "-" + writeCounter;
  const text = JSON.stringify(value, null, 2) + "\n";
  const fd = fs.openSync(temp, "w");
  try {
    fs.writeSync(fd, text, 0, "utf8");
    fs.fsyncSync(fd);
  } finally {
    fs.closeSync(fd);
  }
  fs.renameSync(temp, file);
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function hashFile(file) {
  const digest = crypto.createHash("sha256");
  const fd = fs.openSync(file, "r");
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  try {
    let read;
    while ((read = fs.readSync(fd, buffer, 0, buffer.length, null)) > 0) {
      digest.update(buffer.subarray(0, read));
    }
  } finally {
    fs.closeSync(fd);
  }
  return digest.digest("hex");
}

function walk(dir, base, out) {
  for (const name of fs.readdirSync(dir).sort()) {
    const full = path.join(dir, name);
    const rel = base.length > 0 ? base + "/" + name : name;
    const stat = fs.lstatSync(full);
    if (stat.isSymbolicLink()) {
      throw new StorageError("ERR_UNSUPPORTED", "package content contains a symbolic link or junction: " + rel);
    }
    if (stat.isDirectory()) {
      out.push({ type: "dir", rel });
      walk(full, rel, out);
    } else {
      out.push({ type: "file", rel, size: stat.size, hash: hashFile(full) });
    }
  }
}

function treeHash(dir) {
  const entries = [];
  walk(dir, "", entries);
  const digest = crypto.createHash("sha256");
  for (const entry of entries) {
    if (entry.type === "file") {
      digest.update("f\u0000" + entry.rel + "\u0000" + entry.size + "\u0000" + entry.hash + "\n");
    } else {
      digest.update("d\u0000" + entry.rel + "\n");
    }
  }
  return digest.digest("hex");
}

function removeDir(dir) {
  if (!fs.existsSync(dir)) {
    return;
  }
  fs.rmSync(dir, { recursive: true, force: true });
}

function removeLink(link) {
  try {
    fs.rmdirSync(link);
    return true;
  } catch (error) {
    return false;
  }
}

function readLink(link) {
  try {
    return fs.readlinkSync(link);
  } catch (error) {
    return null;
  }
}

function logInfo(evt, message, fields) {
  send({
    v: 1,
    kind: "ctl",
    data: {
      action: "log.write",
      schema: 1,
      records: [Object.assign({ ts: now(), pri: 6, level: "info", evt, msg: message }, fields || {})],
    },
  });
}

// ---- outbound capability calls (used for log.audit) ----

function callCapability(capability, op, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    callCounter += 1;
    const id = "s" + callCounter;
    const timer = setTimeout(() => {
      pendingCalls.delete(id);
      reject(new StorageError("ERR_TIMEOUT", "call timeout: " + capability + "/" + op));
    }, timeoutMs || 30000);
    pendingCalls.set(id, { resolve, reject, timer });
    send({ v: 1, kind: "call", id, data: { cap: capability, op, args } });
  });
}

// Durable: resolves only after the provider fsynced the batch.
function auditAppend(task, records) {
  return callCapability("log.audit", "append", { stream: "audit", task: task == null ? null : String(task), records });
}

function auditQuery(task) {
  return callCapability("log.audit", "query", { task: task == null ? null : String(task) });
}

let entrySeq = 0;
const PROCESS_NONCE = crypto.randomBytes(4).toString("hex");

async function ledgerEntry(task, entry) {
  if (task == null) {
    return null;
  }
  entrySeq += 1;
  const record = Object.assign({ id: now() + "-" + PROCESS_NONCE + "-" + entrySeq, ts: now(), task: String(task) }, entry);
  await auditAppend(task, [record]);
  return record;
}

async function ensureBegin(task, pkg) {
  if (task == null || beganTasks.has(String(task))) {
    return;
  }
  beganTasks.add(String(task));
  const existing = await auditQuery(task);
  if (!(existing.entries || []).some((entry) => entry.phase === "begin")) {
    await ledgerEntry(task, { phase: "begin", op: "task", pkg: pkg || null });
  }
}

// ---- store operations ----

async function adopt(args, caller) {
  const task = args.task == null ? null : String(args.task);
  const pkg = packageId(args.pkg, "adopt package id");
  const version = packageVersion(args.version || "0.0.0");

  const stagingPath = path.resolve(String(args.stagingPath || ""));
  if (!inside(STAGING, stagingPath)) {
    throw new StorageError("ERR_UNSUPPORTED", "staging path is outside the staging root");
  }

  const grantCheck = grantFor(stagingPath, caller);
  if (!grantCheck.ok) {
    throw new StorageError("ERR_UNSUPPORTED", grantCheck.reason);
  }

  await ensureBegin(task, pkg);

  const archiveHash = args.contentHash ? sha256Hex(args.contentHash) : null;
  const treeHashValue = treeHash(stagingPath);
  const short = treeHashValue.slice(0, 16);
  const name = short + "-" + pkg + "-" + version;
  const target = path.join(STORE, name);
  const recordPath = path.join(RECORDS, pkg + ".json");
  const intentPath = path.join(INTENTS, safeTask(task || "t0") + ".json");

  const existingIndex = fs.existsSync(target);
  let beforeRecord = null;
  if (fs.existsSync(recordPath)) {
    try {
      beforeRecord = readJson(recordPath);
    } catch (error) {
      beforeRecord = null;
    }
  }

  const record = {
    schema: 1,
    id: pkg,
    version,
    storePath: target,
    hashes: { tree: treeHashValue, archive: archiveHash },
    origin: args.origin || "local",
    wiring: args.wiring || {},
    installedAt: now(),
  };

  // Write-ahead intent: recovery can finish the record from afterRecord if we die before the record write.
  writeJsonAtomic(intentPath, {
    schema: 1,
    task: task || "t0",
    pkg,
    version,
    target,
    phase: "begin",
    beforeRecord,
    afterRecord: record,
    createdAt: now(),
  });

  if (existingIndex) {
    removeDir(stagingPath);
    if (!beforeRecord || beforeRecord.storePath !== target) {
      // A previous crash may have renamed the store but never written the record; finish it now.
      await ledgerEntry(task, {
        op: "record.write",
        pkg,
        before: beforeRecord,
        after: record,
        undo: beforeRecord === null
          ? { cap: "storage.record", op: "delete", args: { pkg } }
          : { cap: "storage.record", op: "write", args: { record: beforeRecord } },
      });
      writeJsonAtomic(recordPath, record);
    }
    fs.rmSync(intentPath, { force: true });
    logInfo("storage.adopt", "package already in the store", { pkg, version, storePath: target });
    return { storePath: target, treeHash: treeHashValue, existed: true };
  }

  // Write-ahead: the audit entry (with its undo) must be durable before the record changes.
  await ledgerEntry(task, {
    op: "record.write",
    pkg,
    before: beforeRecord,
    after: record,
    undo: beforeRecord === null
      ? { cap: "storage.record", op: "delete", args: { pkg } }
      : { cap: "storage.record", op: "write", args: { record: beforeRecord } },
  });

  fs.renameSync(stagingPath, target);
  const intentDocument = (phase) => ({
    schema: 1,
    task: task || "t0",
    pkg,
    version,
    target,
    phase,
    beforeRecord,
    afterRecord: record,
    createdAt: now(),
  });
  writeJsonAtomic(intentPath, intentDocument("renamed"));

  writeJsonAtomic(recordPath, record);

  writeJsonAtomic(intentPath, intentDocument("done"));
  fs.rmSync(intentPath, { force: true });
  logInfo("storage.adopt", "package adopted into the store", { pkg, version, storePath: target });
  return { storePath: target, treeHash: treeHashValue, hashShort: short, existed: false };
}

function abort(args, caller) {
  const task = args.task == null ? null : String(args.task);
  const stagingPath = path.resolve(String(args.stagingPath || ""));
  if (!inside(STAGING, stagingPath)) {
    throw new StorageError("ERR_UNSUPPORTED", "staging path is outside the staging root");
  }

  const grantCheck = grantFor(stagingPath, caller);
  if (!grantCheck.ok) {
    throw new StorageError("ERR_UNSUPPORTED", grantCheck.reason);
  }

  grants.delete(path.basename(stagingPath));
  removeDir(stagingPath);
  if (task !== null) {
    fs.rmSync(path.join(INTENTS, safeTask(task) + ".json"), { force: true });
  }
  logInfo("storage.abort", "staging grant aborted", { task });
  return { removed: true };
}

function recordGet(args) {
  const pkg = packageId(args.pkg, "record.get package id");
  const file = path.join(RECORDS, pkg + ".json");
  if (!fs.existsSync(file)) {
    throw new StorageError("ERR_PKG_NOT_FOUND", "no record for package " + pkg);
  }
  return readJson(file);
}

function recordList() {
  const items = [];
  for (const name of fs.readdirSync(RECORDS).sort()) {
    if (!name.endsWith(".json")) {
      continue;
    }
    try {
      const record = readJson(path.join(RECORDS, name));
      const stored = Array.isArray(record.provides) ? record.provides.filter((name) => typeof name === "string") : null;
      const storedRequires = Array.isArray(record.requires)
        ? record.requires.filter((dep) => dep && typeof dep.id === "string").map((dep) => ({ id: dep.id, range: typeof dep.range === "string" ? dep.range : "" }))
        : null;
      const storedConflicts = Array.isArray(record.conflicts) ? record.conflicts.filter((name) => typeof name === "string") : null;
      items.push({ id: record.id, version: record.version, storePath: record.storePath, pinned: record.pinned === true, provides: stored || providesOf(record), requires: storedRequires || [], conflicts: storedConflicts || [] });
    } catch (error) {
      // A corrupt record is reported by doctor, not here.
    }
  }
  return { items };
}

// Virtual capabilities come from the installed manifest; a missing/unreadable one means none.
function providesOf(record) {
  try {
    const manifest = readJson(path.join(record.storePath, "package.json"));
    if (Array.isArray(manifest.provides)) {
      return manifest.provides.filter((name) => typeof name === "string" && /^[a-z][a-z0-9-]{0,63}$/.test(name));
    }
  } catch (error) {
    // Fall through to no provides.
  }
  return [];
}

function recordWrite(args) {
  const record = args.record;
  if (!record || !record.id) {
    throw new StorageError("ERR_BAD_ARGS", "record.write needs a record with an id");
  }
  const id = packageId(record.id, "record.write package id");
  // Atomic: a crash mid-write must not corrupt the record the kernel reads on recovery.
  writeJsonAtomic(path.join(RECORDS, id + ".json"), record);
  return { ok: true };
}

function recordDelete(args) {
  const pkg = packageId(args.pkg, "record.delete package id");
  fs.rmSync(path.join(RECORDS, pkg + ".json"), { force: true });
  return { ok: true };
}

async function currentSet(args) {
  const task = args.task == null ? null : String(args.task);
  const pkg = packageId(args.pkg, "current.set package id");
  const target = path.resolve(String(args.storePath || ""));
  if (!inside(STORE, target)) {
    throw new StorageError("ERR_UNSUPPORTED", "current needs a store path inside store/");
  }
  if (!fs.existsSync(target)) {
    throw new StorageError("ERR_PKG_NOT_FOUND", "store path no longer exists (removed by gc?): " + target);
  }

  await ensureBegin(task, pkg);

  const link = path.join(CURRENT, pkg);
  if (!inside(CURRENT, link)) {
    throw new StorageError("ERR_UNSUPPORTED", "current link escapes the current directory");
  }
  const previous = readLink(link);
  const before = previous === null ? null : path.resolve(previous);

  await ledgerEntry(task, {
    op: "current.set",
    pkg,
    before,
    after: target,
    undo: before === null
      ? { cap: "storage.current", op: "clear", args: { pkg } }
      : { cap: "storage.current", op: "set", args: { pkg, storePath: before } },
  });

  if (fs.existsSync(link) || readLink(link) !== null) {
    if (!removeLink(link)) {
      throw new StorageError("ERR_IO", "cannot remove the existing current link: " + link);
    }
  }
  fs.mkdirSync(CURRENT, { recursive: true });
  fs.symlinkSync(target, link, "junction");
  logInfo("storage.current", "current pointer moved", { pkg, target });
  return { link, target };
}

function currentGet(args) {
  const link = path.join(CURRENT, packageId(args.pkg, "current.get package id"));
  return { target: readLink(link) };
}

function currentClear(args) {
  const pkg = packageId(args.pkg, "current.clear package id");
  const link = path.join(CURRENT, pkg);
  if (fs.existsSync(link) || readLink(link) !== null) {
    removeLink(link);
  }
  return { cleared: true };
}

// Uninstall is the mirror of install: wiring, current pointer, home junction and record go away inside
// one audited task, so rollback can put the package back exactly as it was. Store content is left in
// place on purpose; pd gc collects it once neither a record nor a generation references it.
async function uninstall(args) {
  const task = args.task == null ? null : String(args.task);
  const pkg = packageId(args.pkg, "uninstall package id");
  const purgeData = args.purgeData === true;
  const skipGeneration = args.skipGeneration === true;
  if (task === null || task.length === 0) {
    // Never delete records/user data without an audit trail: nothing would be undoable.
    throw new StorageError("ERR_BAD_ARGS", "storage.uninstall needs a non-empty task id");
  }
  const record = recordGet({ pkg });
  if (String(record.id) !== pkg) {
    throw new StorageError("ERR_PKG_INVALID", "record id does not match its file name: " + record.id + " vs " + pkg);
  }

  const wiring = recordWiring(record);
  const rawBefore = wiring.before && typeof wiring.before === "object" ? wiring.before : {};
  const hasMachineWiring = (wiring.path || []).length > 0 || Object.keys(wiring.env || {}).length > 0 ||
    (wiring.registry || []).length > 0 || (wiring.launch || []).length > 0 || (wiring.data || []).length > 0;
  if (rawBefore.fake !== undefined && (rawBefore.fake === true) !== FAKE && hasMachineWiring) {
    // Deleting the record here would orphan wiring that this machine view never touched.
    throw new StorageError("ERR_UNSUPPORTED",
      "package " + pkg + " was wired in the " + (rawBefore.fake === true ? "fake" : "real") +
      " machine view; run pd remove in that mode first");
  }

  await ensureBegin(task, pkg);

  const wiringChanges = await restoreWiring(record, task);

  let currentCleared = false;
  const link = path.join(CURRENT, pkg);
  const previousTarget = readLink(link);
  if (previousTarget !== null) {
    const actual = path.resolve(previousTarget);
    const restoreTarget = inside(STORE, actual) ? actual : record.storePath;
    await ledgerEntry(task, {
      op: "current.clear",
      pkg,
      before: actual,
      after: null,
      undo: { cap: "storage.current", op: "set", args: { pkg, storePath: restoreTarget } },
    });
    if (!removeLink(link)) {
      throw new StorageError("ERR_IO", "cannot remove the current link: " + link);
    }
    currentCleared = true;
  }

  // Keep the user's files by default; only the machine-side junction is detached so the package is
  // gone from PATH/launchers. --purge moves the stick-side home directory to state/trash, where
  // rollback can rename it back and pd gc can finally delete it.
  // With C:\Dev pointing at this stick root there is no separate machine-side home to detach:
  // homeMachinePath() and homeStickPath() are the same directory.
  let sameRootHome = false;
  try {
    sameRootHome = path.resolve(fs.realpathSync(stableRoot())).toLowerCase() ===
      path.resolve(fs.realpathSync(ROOT)).toLowerCase();
  } catch (error) {
    sameRootHome = path.resolve(stableRoot()).toLowerCase() === path.resolve(ROOT).toLowerCase();
  }

  let homeDetached = false;
  const machineHome = homeMachinePath(pkg);
  const homeTarget = sameRootHome ? null : readLinkTarget(machineHome);
  if (homeTarget !== null) {
    if (path.resolve(homeTarget).toLowerCase() !== path.resolve(homeStickPath(pkg)).toLowerCase()) {
      throw new StorageError("ERR_IO", "home junction points elsewhere: " + machineHome);
    }
    await ledgerEntry(task, {
      op: "home.unlink",
      pkg,
      before: machineHome,
      after: null,
      undo: { cap: "storage.home", op: "ensure", args: { pkg } },
    });
    // writeLink is FAKE aware and verifies the real junction removal, unlike bare removeLink.
    writeLink(machineHome, null);
    homeDetached = true;
  } else if (!sameRootHome && fs.existsSync(machineHome)) {
    throw new StorageError("ERR_IO", "machine home exists but is not a junction: " + machineHome);
  }

  let homePurged = false;
  let homeTrash = null;
  const stickHome = homeStickPath(pkg);
  if (purgeData && fs.existsSync(stickHome)) {
    const trashDir = path.join(STATE, "trash");
    fs.mkdirSync(trashDir, { recursive: true });
    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
    homeTrash = path.join(trashDir, safeTask(task) + "-" + pkg + "-" + stamp + "-" + process.pid);
    if (!inside(STATE, homeTrash)) {
      throw new StorageError("ERR_UNSUPPORTED", "home trash path escapes state/");
    }
    await ledgerEntry(task, {
      op: "home.purge",
      pkg,
      before: stickHome,
      trash: homeTrash,
      after: null,
      undo: { cap: "storage.home", op: "restore", args: { pkg, from: homeTrash, to: stickHome } },
    });
    fs.renameSync(stickHome, homeTrash);
    homePurged = true;
  }

  await ledgerEntry(task, {
    op: "record.delete",
    pkg,
    before: { id: record.id, version: record.version, storePath: record.storePath },
    after: null,
    undo: { cap: "storage.record", op: "write", args: { record } },
  });
  recordDelete({ pkg });
  // last-apply describes the machine wiring of the previous apply; the package just left, so it is stale.
  fs.rmSync(LAST_APPLY_FILE, { force: true });

  const generationSeq = skipGeneration ? null : writeGeneration(readAllRecords());
  logInfo("storage.uninstall", "package uninstalled", {
    pkg,
    version: record.version,
    purgeData,
    wiringChanges,
    currentCleared,
    homeDetached,
    homePurged,
    homeTrash,
  });
  return {
    pkg,
    version: record.version,
    storePath: record.storePath,
    wiringChanges,
    currentCleared,
    homeDetached,
    homePurged,
    homeTrash,
    keptHome: !homePurged,
    generationSeq,
  };
}

function referencedStores() {
  const referenced = new Set();
  for (const record of Object.values(readAllRecords())) {
    if (record.storePath) {
      referenced.add(path.resolve(record.storePath));
    }
  }
  for (const pkg of fs.readdirSync(CURRENT)) {
    const target = readLink(path.join(CURRENT, pkg));
    if (target) {
      referenced.add(path.resolve(target));
    }
  }
  for (const name of fs.readdirSync(INTENTS)) {
    try {
      const intent = readJson(path.join(INTENTS, name));
      if (intent.target) {
        referenced.add(path.resolve(intent.target));
      }
    } catch (error) {
      // ignore corrupt intent
    }
  }
  for (const name of generationFiles()) {
    try {
      const generation = readJson(path.join(GENERATIONS, name));
      for (const record of Object.values(generation.packages || {})) {
        if (record.storePath) {
          referenced.add(path.resolve(record.storePath));
        }
      }
    } catch (error) {
      // ignore corrupt generation
    }
  }
  return referenced;
}

function referencedBlobs() {
  const referenced = new Set();
  const add = (record) => {
    const archive = record && record.hashes ? record.hashes.archive : null;
    if (archive) {
      referenced.add(String(archive).toLowerCase());
    }
  };
  for (const record of Object.values(readAllRecords())) {
    add(record);
  }
  for (const name of generationFiles()) {
    try {
      const generation = readJson(path.join(GENERATIONS, name));
      for (const record of Object.values(generation.packages || {})) {
        add(record);
      }
    } catch (error) {
      // ignore corrupt generation
    }
  }
  return referenced;
}

function sizeOf(target) {
  try {
    const stat = fs.lstatSync(target);
    if (stat.isFile()) {
      return stat.size;
    }
    if (!stat.isDirectory()) {
      return 0;
    }
    let total = 0;
    for (const name of fs.readdirSync(target)) {
      total += sizeOf(path.join(target, name));
    }
    return total;
  } catch (error) {
    return 0;
  }
}

function corruptRecordCount() {
  let count = 0;
  for (const name of fs.readdirSync(RECORDS)) {
    if (!name.endsWith(".json")) {
      continue;
    }
    try {
      readJson(path.join(RECORDS, name));
    } catch (error) {
      count += 1;
    }
  }
  return count;
}

async function gc(args) {
  const options = args || {};
  const dryRun = options.dryRun === true;
  const keep = Math.max(1, Number(options.keep) > 0 ? Number(options.keep) : readSettingNumber("gc", "keepGenerations", 50));
  const cleanBlobs = options.blobs === true;
  const errors = [];

  const unreadable = corruptRecordCount();
  if (unreadable > 0 && options.force !== true) {
    throw new StorageError("ERR_IO", unreadable + " record file(s) are unreadable; refusing to gc until they are fixed");
  }

  let generationsRemoved = 0;
  const files = generationFiles();
  while (files.length > keep) {
    const oldest = files.shift();
    if (!dryRun) {
      try {
        fs.rmSync(path.join(GENERATIONS, oldest), { force: true });
      } catch (error) {
        errors.push("generation " + oldest + ": " + (error.message || String(error)));
        files.unshift(oldest);
        break;
      }
    }
    generationsRemoved += 1;
  }

  for (const key of [...grants.keys()]) {
    const entry = grants.get(key);
    if (!entry || entry.expiresAt <= Date.now()) {
      grants.delete(key);
    }
  }

  const intentTasks = [];
  for (const name of fs.readdirSync(INTENTS)) {
    if (name.endsWith(".json")) {
      intentTasks.push(name.slice(0, -5));
    }
  }

  // Task ids may contain dashes (e.g. s-1a2b3c4d-r1), so prefix matching is the only safe claim test.
  const claimedByIntent = (name) => intentTasks.some((task) => name === task || name.startsWith(task + "-"));

  let stagingRemoved = 0;
  let bytesFreed = 0;
  for (const name of fs.readdirSync(STAGING)) {
    const full = path.join(STAGING, name);
    if (claimedByIntent(name) || grants.has(name)) {
      continue;
    }
    const bytes = sizeOf(full);
    if (!dryRun) {
      try {
        removeDir(full);
      } catch (error) {
        errors.push("staging " + name + ": " + (error.message || String(error)));
        continue;
      }
    }
    stagingRemoved += 1;
    bytesFreed += bytes;
  }

  const referenced = referencedStores();
  let storeRemoved = 0;
  for (const name of fs.readdirSync(STORE)) {
    const full = path.join(STORE, name);
    if (referenced.has(path.resolve(full))) {
      continue;
    }
    const bytes = sizeOf(full);
    if (!dryRun) {
      try {
        removeDir(full);
      } catch (error) {
        errors.push("store " + name + ": " + (error.message || String(error)));
        continue;
      }
    }
    storeRemoved += 1;
    bytesFreed += bytes;
  }

  let blobsRemoved = 0;
  let blobsKept = 0;
  const blobsDir = path.join(ROOT, "cache", "blobs");
  if (cleanBlobs && fs.existsSync(blobsDir)) {
    const referencedBlobSet = referencedBlobs();
    for (const name of fs.readdirSync(blobsDir)) {
      const full = path.join(blobsDir, name);
      if (referencedBlobSet.has(name.toLowerCase())) {
        blobsKept += 1;
        continue;
      }
      const bytes = sizeOf(full);
      if (!dryRun) {
        try {
          fs.rmSync(full, { force: true });
        } catch (error) {
          errors.push("blob " + name + ": " + (error.message || String(error)));
          continue;
        }
      }
      blobsRemoved += 1;
      bytesFreed += bytes;
    }
  }

  // --purge moves a package home into state/trash so rollback can restore it; gc is the final owner.
  let trashRemoved = 0;
  const trashDir = path.join(STATE, "trash");
  if (fs.existsSync(trashDir)) {
    for (const name of fs.readdirSync(trashDir)) {
      const full = path.join(trashDir, name);
      const bytes = sizeOf(full);
      if (!dryRun) {
        try {
          removeDir(full);
        } catch (error) {
          errors.push("trash " + name + ": " + (error.message || String(error)));
          continue;
        }
      }
      trashRemoved += 1;
      bytesFreed += bytes;
    }
  }

  if (!dryRun) {
    logInfo("storage.gc", "garbage collected", { stagingRemoved, storeRemoved, generationsRemoved, blobsRemoved, trashRemoved, errors: errors.length });
  }

  return { stagingRemoved, storeRemoved, generationsRemoved, blobsRemoved, trashRemoved, blobsKept, bytesFreed, dryRun, keepGenerations: keep, errors };
}


async function ledgerOp(op, args) {
  const task = args.task == null ? null : String(args.task);
  if (op === "begin") {
    await ensureBegin(task, args.pkg || null);
    if (task !== null) {
      beganTasks.add(task);
    }
    return { ok: true };
  }
  if (op === "commit") {
    await ledgerEntry(task, { phase: "done" });
    return { ok: true };
  }
  if (op === "list") {
    const result = await auditQuery(task);
    return { entries: result.entries || [], truncated: result.truncated === true };
  }
  throw new StorageError("ERR_BAD_ARGS", "unknown ledger op " + op);
}

// ---- wiring layer: the only place that touches PATH / environment / registry / launchers ----

const WIRE_DIR = path.join(STATE, "wire");
const LAST_APPLY_FILE = path.join(WIRE_DIR, "last-apply.json");
const FAKE_FILE = path.join(WIRE_DIR, "fake.json");
fs.mkdirSync(WIRE_DIR, { recursive: true });

const FAKE = process.env.PD_WIRE_FAKE === "1" || fs.existsSync(path.join(WIRE_DIR, "fake-mode"));
// Set from hello.ok; records remember which machine captured their pre-install values.
let MACHINE_ID = "";

// Elevated helper: used for HKLM and machine paths outside the user's own directories.
let elevateCounter = 0;

function elevateHelperCommand() {
  const explicit = process.env.PD_ELEVATE_HELPER;
  if (explicit && fs.existsSync(explicit)) {
    return explicit.toLowerCase().endsWith(".dll")
      ? [process.env.PD_DOTNET || "dotnet", explicit]
      : [explicit];
  }
  // app\tools is the packaged location; the app root is accepted for older deployments.
  for (const directory of [path.join(ROOT, "app", "tools"), path.join(ROOT, "app")]) {
    const exe = path.join(directory, "pd-elevate.exe");
    if (fs.existsSync(exe)) {
      return [exe];
    }
    const dll = path.join(directory, "pd-elevate.dll");
    if (fs.existsSync(dll)) {
      return [process.env.PD_DOTNET || "dotnet", dll];
    }
  }
  return null;
}

function helperAvailable() {
  return elevateHelperCommand() !== null;
}

// Runs one batch through pd-elevate. noElevate=true is used for HKCU reads/writes: those never need
// UAC, and going through the helper avoids depending on reg.exe being enabled on the machine.
function runHelper(ops, noElevate) {
  const command = elevateHelperCommand();
  if (!command) {
    throw new StorageError("ERR_UNSUPPORTED",
      "elevation helper not found (expected " + path.join(ROOT, "app", "tools", "pd-elevate.exe") + ")");
  }

  // Tests cannot answer a UAC prompt: when state\wire\elevate-fake.json exists the helper runs
  // with --fake (it still executes process.run, only registry/link ops go to the fake state).
  const fakePath = path.join(WIRE_DIR, "elevate-fake.json");
  // Only honoured in the fake test mode: a production stick must never silently run an "elevated"
  // script without the helper actually being elevated.
  const fakeElevation = FAKE && fs.existsSync(fakePath) ? fakePath : null;
  const dir = path.join(STATE, "tmp");
  fs.mkdirSync(dir, { recursive: true });
  elevateCounter += 1;
  const base = "elevate-" + process.pid + "-" + elevateCounter;
  const requestPath = path.join(dir, base + ".json");
  const resultPath = path.join(dir, base + ".result.json");
  writeJson(requestPath, { schema: 1, result: resultPath, ops: ops });
  // Bind the elevated run to these exact bytes: the request file sits in a user-writable
  // directory and must not be swappable during the UAC consent dwell.
  const requestDigest = crypto.createHash("sha256").update(fs.readFileSync(requestPath)).digest("hex");
  let spawnError = null;
  try {
    const args = command.slice(1).concat([requestPath]);
    if (noElevate) {
      args.push("--no-elevate");
    }

    if (fakeElevation) {
      args.push("--fake", fakeElevation);
    }
    args.push("--result", resultPath, "--sha256", requestDigest);
    execFileSync(command[0], args, {
      stdio: ["ignore", "pipe", "pipe"],
      timeout: 300000,
      windowsHide: true,
    });
  } catch (error) {
    spawnError = error;
  } finally {
    fs.rmSync(requestPath, { force: true });
  }

  let result = null;
  try {
    result = readJson(resultPath);
  } catch (error) {
    result = null;
  } finally {
    fs.rmSync(resultPath, { force: true });
  }

  if (result && result.ok !== true) {
    const failure = result.error || {};
    const first = (result.results || []).find((item) => item && item.ok === false) || null;
    const detail = first && first.error ? first.error.code + ": " + first.error.message : "";
    throw new StorageError(failure.code || "ERR_IO",
      (failure.message || "elevation failed") + (detail ? " (" + detail + ")" : ""));
  }

  if (spawnError) {
    if (spawnError.code === "ENOENT") {
      throw new StorageError("ERR_UNSUPPORTED", "elevation helper cannot start: " + (spawnError.message || String(spawnError)));
    }
    const stderr = spawnError.stderr ? String(spawnError.stderr).trim() : "";
    throw new StorageError("ERR_IO", "elevation failed or was cancelled: " + (spawnError.message || String(spawnError)) +
      (stderr ? " | " + stderr : ""));
  }

  if (!result) {
    throw new StorageError("ERR_IO", "elevation helper produced no result (cancelled?)");
  }

  return result.results || [];
}

function runElevated(ops) {
  return runHelper(ops, false);
}

function runHelperDirect(ops) {
  return runHelper(ops, true);
}

// ---- persist: install-dir directories become junctions to <stick>/home/<pkg>/persist/<rel> ----
function normalizePersistPath(value) {
  const rel = String(value === undefined || value === null ? "" : value).replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
  const segments = rel.split("/");
  const badSegment = segments.some((segment) =>
    !segment || segment === "." || segment === ".." || segment.includes(":") ||
    /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(segment));
  if (!rel || badSegment || /^[A-Za-z]:/.test(rel) || rel.includes("\u0000") || segments.length > 16) {
    throw new StorageError("ERR_PKG_INVALID", "bad persist path: " + String(value));
  }
  return rel;
}

function persistStickPath(pkg, rel) {
  return path.join(ROOT, "home", String(pkg), "persist", ...normalizePersistPath(rel).split("/"));
}

function movePersistContents(from, to) {
  fs.mkdirSync(to, { recursive: true });
  for (const name of fs.readdirSync(from)) {
    const source = path.join(from, name);
    const target = path.join(to, name);
    if (fs.existsSync(target)) {
      // Upgrades keep the user's existing data; only seed files that are still missing.
      continue;
    }
    fs.renameSync(source, target);
  }
}

async function wirePersistLink(args) {
  const source = path.resolve(String(args.source || ""));
  const target = path.resolve(String(args.target || ""));
  if (!inside(path.join(ROOT, "home"), source)) {
    throw new StorageError("ERR_UNSUPPORTED", "persist source must stay inside the stick home dir");
  }
  const storeDir = path.resolve(String(args.root || ""));
  if (!inside(STORE, target) || !storeDir || storeDir === target || !inside(storeDir, target)) {
    throw new StorageError("ERR_UNSUPPORTED", "persist target must be a strict child of the package store dir");
  }
  // persist links are stick-local (store <-> home on the same disk), not machine wiring: they are
  // always created for real, even in the fake-machine test mode.
  const existing = readLink(target);
  if (existing !== null) {
    const resolved = String(existing).replace(/^\\\\\?\\|^\\\?\?\\/, "");
    if (path.resolve(resolved).toLowerCase() === source.toLowerCase()) {
      return { changed: false, source: source, target: target };
    }
    throw new StorageError("ERR_IO", "persist junction points elsewhere: " + target);
  }
  fs.mkdirSync(source, { recursive: true });
  if (fs.existsSync(target)) {
    const stat = fs.lstatSync(target);
    if (!stat.isDirectory()) {
      throw new StorageError("ERR_PKG_INVALID", "persist path must be a directory: " + target);
    }
    movePersistContents(target, source);
    fs.rmdirSync(target);
  }
  await ledgerEntry(args.task, {
    op: "persist.link",
    source: source,
    target: target,
    before: null,
    after: { link: source },
    undo: { cap: "storage.wire", op: "persist.unlink", args: { target: target } },
  });
  fs.mkdirSync(path.dirname(target), { recursive: true });
  try {
    fs.symlinkSync(path.resolve(source), target, "junction");
  } catch (error) {
    throw new StorageError("ERR_IO", "cannot create persist junction " + target + ": " + (error.message || String(error)));
  }
  return { changed: true, source: source, target: target };
}

// Re-point the machine stable-root junction (C:\Dev by default) at this stick root.
// Machine-level, so it always goes through the elevated helper; the caller (doctor --fix) has
// already verified that the current target is missing or points elsewhere.
async function ensureStableRoot(args) {
  const target = path.resolve(String(args.target || stableRoot()));
  const source = path.resolve(ROOT);
  const existing = readLink(target);
  if (existing !== null) {
    const resolved = String(existing).replace(/^\\\\\?\\|^\\\?\?\\/, "");
    if (path.resolve(resolved).toLowerCase() === source.toLowerCase()) {
      return { changed: false, target: target, source: source };
    }
  } else {
    let stat = null;
    try { stat = fs.lstatSync(target); } catch (error) { stat = null; }
    if (stat !== null) {
      throw new StorageError("ERR_UNSUPPORTED", "stable root exists but is not a junction: " + target);
    }
  }
  if (FAKE) {
    return { changed: false, target: target, source: source, fake: true };
  }
  // One elevated batch: never remove the old junction in this (non-elevated) process first.
  // If the helper cannot elevate, the existing stable root stays untouched.
  const ops = [];
  if (existing !== null) {
    ops.push({ op: "link.remove", target: target });
  }
  ops.push({ op: "link.create", source: source, target: target });
  runElevated(ops);
  return { changed: true, target: target, source: source };
}

// Real, ungated removal for the stick-local persist junction (never a fake/machine link).
function persistUnlink(args) {
  const target = path.resolve(String(args.target || ""));
  if (readLink(target) === null) {
    return { changed: false };
  }
  if (!removeLink(target)) {
    throw new StorageError("ERR_IO", "cannot remove persist junction: " + target);
  }
  return { changed: true };
}

async function applyPersist(record, task) {
  let changed = 0;
  for (const relative of recordWiring(record).persist || []) {
    const rel = normalizePersistPath(relative);
    const source = persistStickPath(record.id, rel);
    const target = path.join(record.storePath, ...rel.split("/"));
    const result = await wirePersistLink({ task: task, root: record.storePath, source: source, target: target });
    if (result.changed) {
      changed += 1;
    }
  }
  return changed;
}

function stableRoot() {
  try {
    const settings = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "settings.json"), "utf8"));
    if (settings.root && settings.root.stableRoot) {
      return String(settings.root.stableRoot);
    }
  } catch (error) {
    // fall through to the documented default
  }
  return "C:\\Dev";
}

function readFake() {
  try {
    return JSON.parse(fs.readFileSync(FAKE_FILE, "utf8"));
  } catch (error) {
    return { path: null, env: {}, registry: {}, launchers: {}, links: {}, copies: {} };
  }
}

function writeFake(value) {
  writeJson(FAKE_FILE, value);
}

function powershell(script) {
  const powershell = path.join(process.env.SystemRoot || process.env.windir || "C:\\Windows",
    "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
  return execFileSync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
}

// Keep the entries owned by this stick (stableRoot\current\...) ahead of pre-existing PATH
// entries: otherwise an old machine-wide install can shadow a migrated tool (e.g. code --version
// still resolved to an installed VS Code 1.91 while the stick carries 1.144).
function prioritizeManagedPath() {
  if (FAKE) {
    return false;
  }
  const raw = readUserPath();
  if (!raw) {
    return false;
  }
  const entries = raw.split(";").map((item) => item.trim()).filter(Boolean);
  const prefix = path.join(stableRoot(), "current").toLowerCase() + path.sep;
  const managed = [];
  const others = [];
  const seen = new Set();
  for (const entry of entries) {
    const key = path.resolve(entry).toLowerCase();
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    if (key.startsWith(prefix)) {
      managed.push(entry);
    } else {
      others.push(entry);
    }
  }
  const ordered = managed.concat(others);
  if (ordered.join(";") === entries.join(";")) {
    return false;
  }
  writeUserPath(ordered.join(";"));
  return true;
}

function readUserPath() {
  if (FAKE) {
    return readFake().path;
  }
  try {
    const value = powershell("[Environment]::GetEnvironmentVariable('Path','User')").trim();
    return value.length > 0 ? value : null;
  } catch (error) {
    return null;
  }
}

function writeUserPath(value) {
  if (FAKE) {
    const fake = readFake();
    fake.path = value;
    writeFake(fake);
    return;
  }
  const escaped = value === null ? null : value.replace(/'/g, "''");
  powershell(value === null
    ? "[Environment]::SetEnvironmentVariable('Path',$null,'User')"
    : "[Environment]::SetEnvironmentVariable('Path','" + escaped + "','User')");
}

function readUserEnv(name) {
  if (FAKE) {
    const fake = readFake();
    return Object.prototype.hasOwnProperty.call(fake.env, name) ? fake.env[name] : null;
  }
  try {
    const value = powershell("[Environment]::GetEnvironmentVariable('" + String(name).replace(/'/g, "''") + "','User')").trim();
    return value.length > 0 ? value : null;
  } catch (error) {
    return null;
  }
}

function writeUserEnv(name, value) {
  if (FAKE) {
    const fake = readFake();
    if (value === null) {
      delete fake.env[name];
    } else {
      fake.env[name] = value;
    }
    writeFake(fake);
    return;
  }
  const safeName = String(name).replace(/'/g, "''");
  powershell(value === null
    ? "[Environment]::SetEnvironmentVariable('" + safeName + "',$null,'User')"
    : "[Environment]::SetEnvironmentVariable('" + safeName + "','" + value.replace(/'/g, "''") + "','User')");
}

function registryId(entry) {
  return String(entry.hive) + "\\" + String(entry.key) + "\\" + String(entry.name || "");
}

function readRegistry(entry) {
  if (FAKE) {
    const fake = readFake();
    return Object.prototype.hasOwnProperty.call(fake.registry, registryId(entry)) ? fake.registry[registryId(entry)] : null;
  }
  if (helperAvailable()) {
    try {
      const results = runHelperDirect([{
        op: "registry.get",
        hive: String(entry.hive || "HKCU"),
        key: String(entry.key || ""),
        name: String(entry.name || ""),
      }]);
      const before = results[0] && results[0].before;
      if (!before || before.value === undefined || before.value === null) {
        return null;
      }
      return { type: String(before.type || "REG_SZ"), value: String(before.value) };
    } catch (error) {
      // fall through to reg.exe for environments where the helper cannot run
    }
  }
  const full = String(entry.hive) + "\\" + String(entry.key);
  const args = ["query", full];
  if (entry.name) {
    args.push("/v", String(entry.name));
  } else {
    args.push("/ve");
  }
  try {
    const output = execFileSync("reg", args, { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
    const match = /(REG_[A-Z_]+)\s+(.*)$/m.exec(output);
    if (!match) {
      return null;
    }
    return { type: match[1], value: match[2].trim() };
  } catch (error) {
    return null;
  }
}

function writeRegistry(entry, value) {
  if (FAKE) {
    const fake = readFake();
    if (value === null) {
      delete fake.registry[registryId(entry)];
    } else {
      fake.registry[registryId(entry)] = value;
    }
    writeFake(fake);
    return;
  }
  if (helperAvailable()) {
    try {
      const op = value === null
        ? { op: "registry.delete", hive: String(entry.hive || "HKCU"), key: String(entry.key || ""), name: String(entry.name || "") }
        : {
            op: "registry.set",
            hive: String(entry.hive || "HKCU"),
            key: String(entry.key || ""),
            name: String(entry.name || ""),
            type: String(value.type || "REG_SZ"),
            value: String(value.value),
          };
      runHelperDirect([op]);
      return;
    } catch (error) {
      if (!(error instanceof StorageError) || error.code !== "ERR_UNSUPPORTED") {
        throw error;
      }
      // helper is present but cannot start; fall back to reg.exe
    }
  }
  const full = String(entry.hive) + "\\" + String(entry.key);
  const args = value === null
    ? ["delete", full, entry.name ? "/v" : "/ve", ...(entry.name ? [String(entry.name)] : []), "/f"]
    : ["add", full, entry.name ? "/v" : "/ve", ...(entry.name ? [String(entry.name)] : []), "/t", String(value.type || "REG_SZ"), "/d", String(value.value), "/f"];
  execFileSync("reg", args, { stdio: "ignore" });
}

function launcherId(file) {
  return path.resolve(file);
}

function linkPath(target) {
  return path.resolve(String(target));
}

function readLinkTarget(target) {
  if (FAKE) {
    const fake = readFake();
    return Object.prototype.hasOwnProperty.call(fake.links || {}, linkPath(target)) ? fake.links[linkPath(target)] : null;
  }
  return readLink(target);
}

function writeLink(target, source) {
  if (FAKE) {
    const fake = readFake();
    fake.links = fake.links || {};
    if (source === null) {
      delete fake.links[linkPath(target)];
    } else {
      fake.links[linkPath(target)] = linkPath(source);
    }
    writeFake(fake);
    return;
  }
  if (source === null) {
    if (!removeLink(target)) {
      throw new StorageError("ERR_IO", "cannot remove the junction: " + target);
    }
    return;
  }
  fs.mkdirSync(path.dirname(target), { recursive: true });
  try {
    fs.symlinkSync(path.resolve(source), target, "junction");
  } catch (error) {
    throw new StorageError("ERR_IO", "cannot create junction " + target + ": " + (error.message || String(error)));
  }
}

function allowedLinkTarget(target) {
  if (FAKE) {
    return true;
  }
  for (const root of [stableRoot(), process.env.USERPROFILE, process.env.APPDATA, process.env.LOCALAPPDATA]) {
    if (root && inside(root, target)) {
      return true;
    }
  }
  return false;
}

// Merge an existing real directory into the stick copy: missing files are copied and files the
// machine copy changed more recently overwrite the stick copy (the machine copy wins on conflict).
function adoptLinkTarget(target, source) {
  fs.mkdirSync(source, { recursive: true });
  for (const name of fs.readdirSync(target)) {
    const from = path.join(target, name);
    const to = path.join(source, name);
    const stat = fs.lstatSync(from);
    if (stat.isDirectory()) {
      adoptLinkTarget(from, to);
    } else if (stat.isFile()) {
      let copy = true;
      try {
        copy = stat.mtimeMs > fs.statSync(to).mtimeMs;
      } catch (error) {
        copy = true;
      }
      if (copy) {
        fs.mkdirSync(path.dirname(to), { recursive: true });
        fs.copyFileSync(from, to);
      }
    }
  }
}

async function wireLinkCreate(args) {
  const target = linkPath(String(args.target || ""));
  const source = path.resolve(String(args.source || ""));
  if (!target || !source || target.length === 0) {
    throw new StorageError("ERR_BAD_ARGS", "link.create needs source and target");
  }
  if (!inside(ROOT, source) && !inside(ROOT, realInsideStick(source) || "")) {
    throw new StorageError("ERR_UNSUPPORTED", "link source must be inside the stick: " + source);
  }
  if (!allowedLinkTarget(target)) {
    runElevated([{ op: "link.create", source: source, target: target }]);
    await ledgerEntry(args.task, {
      op: "link.create",
      target: target,
      source: source,
      before: null,
      after: { source: source },
      undo: { cap: "storage.wire", op: "link.remove", args: { target: target } },
    });
    return { changed: true, target: target, source: source, elevated: true };
  }
  const existing = readLinkTarget(target);
  if (existing !== null) {
    if (path.resolve(existing) === source) {
      return { changed: false, target: target, source: source };
    }
    throw new StorageError("ERR_IO", "target already exists and points elsewhere: " + target);
  }
  // A real machine directory may exist where the link should go (an app recreated its data dir,
  // or the user already used the non-portable copy). Adopt its newest files into the stick copy,
  // rename the original aside as a backup, then create the junction: no data is silently lost.
  let adopted = false;
  let adoptedBackup = null;
  if (existing === null && fs.existsSync(target)) {
    const stat = fs.lstatSync(target);
    if (!stat.isDirectory()) {
      throw new StorageError("ERR_IO", "link target exists and is not a directory: " + target);
    }
    adoptLinkTarget(target, source);
    adoptedBackup = target + ".pd-adopt-" + new Date().toISOString().replace(/[:.]/g, "-");
    fs.renameSync(target, adoptedBackup);
    adopted = true;
  }
  const before = null;
  await ledgerEntry(args.task, {
    op: "link.create",
    target: target,
    source: source,
    before: before,
    after: { source: source },
    undo: { cap: "storage.wire", op: "link.remove", args: { target: target } },
  });
  writeLink(target, source);
  return { changed: true, target: target, source: source, adopted: adopted, adoptedBackup: adoptedBackup };
}

async function wireCopyCreate(args) {
  const target = path.resolve(String(args.target || ""));
  const source = path.resolve(String(args.source || ""));
  if (!target || !source) {
    throw new StorageError("ERR_BAD_ARGS", "copy.create needs source and target");
  }
  if (!inside(ROOT, source)) {
    throw new StorageError("ERR_UNSUPPORTED", "copy source must be inside the stick: " + source);
  }
  if (!allowedLinkTarget(target) && !FAKE) {
    runElevated([{ op: "copy.create", source: source, target: target }]);
    await ledgerEntry(args.task, {
      op: "copy.create",
      target: target,
      source: source,
      undo: { cap: "storage.wire", op: "copy.remove", args: { target: target } },
    });
    return { changed: true, target: target, source: source, elevated: true };
  }
  if (!allowedLinkTarget(target)) {
    throw new StorageError("ERR_UNSUPPORTED", "copy target needs elevation or a user-writable location: " + target);
  }
  if (fs.existsSync(target)) {
    throw new StorageError("ERR_IO", "copy target already exists: " + target);
  }
  await ledgerEntry(args.task, {
    op: "copy.create",
    target: target,
    source: source,
    undo: { cap: "storage.wire", op: "copy.remove", args: { target: target } },
  });
  if (FAKE) {
    const fake = readFake();
    fake.copies = fake.copies || {};
    fake.copies[target] = source;
    writeFake(fake);
    return { changed: true, target: target, source: source };
  }
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.cpSync(source, target, { recursive: true });
  return { changed: true, target: target, source: source };
}

async function wireCopyRemove(args) {
  const target = path.resolve(String(args.target || ""));
  if (!target) {
    throw new StorageError("ERR_BAD_ARGS", "copy.remove needs target");
  }
  if (FAKE) {
    const fake = readFake();
    if (!fake.copies || !Object.prototype.hasOwnProperty.call(fake.copies, target)) {
      return { changed: false, target: target };
    }
    const copySource = fake.copies[target] || (args.source ? path.resolve(String(args.source)) : null);
    await ledgerEntry(args.task, {
      op: "copy.remove",
      target: target,
      before: copySource ? { source: copySource } : null,
      undo: copySource ? { cap: "storage.wire", op: "copy.create", args: { target: target, source: copySource } } : null,
    });
    delete fake.copies[target];
    writeFake(fake);
    return { changed: true, target: target, undo: copySource !== null };
  }
  if (!allowedLinkTarget(target)) {
    // The elevated helper cannot hand back the copied content, so this removal is not undoable.
    await ledgerEntry(args.task, {
      op: "copy.remove",
      target: target,
      after: null,
      undo: null,
      irreversible: true,
    });
    runElevated([{ op: "copy.remove", target: target }]);
    return { changed: true, target: target, elevated: true };
  }
  if (!fs.existsSync(target)) {
    return { changed: false, target: target };
  }
  const copySource = args.source ? path.resolve(String(args.source)) : null;
  await ledgerEntry(args.task, {
    op: "copy.remove",
    target: target,
    before: copySource ? { source: copySource } : null,
    undo: copySource ? { cap: "storage.wire", op: "copy.create", args: { target: target, source: copySource } } : null,
  });
  fs.rmSync(target, { recursive: true, force: true });
  return { changed: true, target: target, undo: copySource !== null };
}

async function wireLinkRemove(args) {
  const target = linkPath(String(args.target || ""));
  if (!target) {
    throw new StorageError("ERR_BAD_ARGS", "link.remove needs target");
  }
  if (!allowedLinkTarget(target) && !FAKE) {
    const results = runElevated([{ op: "link.remove", target: target }]);
    const elevatedBefore = (results[0] && results[0].before) || null;
    if (elevatedBefore === null) {
      return { changed: false, target: target };
    }
    await ledgerEntry(args.task, {
      op: "link.remove",
      target: target,
      before: elevatedBefore,
      after: null,
      undo: { cap: "storage.wire", op: "link.create", args: { target: target, source: elevatedBefore.target } },
    });
    return { changed: true, target: target, before: elevatedBefore, elevated: true };
  }
  const before = readLinkTarget(target);
  if (before === null) {
    return { changed: false, target: target };
  }
  await ledgerEntry(args.task, {
    op: "link.remove",
    target: target,
    before: before,
    after: null,
    undo: { cap: "storage.wire", op: "link.create", args: { target: target, source: before } },
  });
  writeLink(target, null);
  return { changed: true, target: target, before: before };
}

function readLauncher(file) {
  if (FAKE) {
    const fake = readFake();
    return Object.prototype.hasOwnProperty.call(fake.launchers, launcherId(file)) ? fake.launchers[launcherId(file)] : null;
  }
  try {
    return fs.readFileSync(file, "utf8");
  } catch (error) {
    return null;
  }
}

function writeLauncher(file, content) {
  if (FAKE) {
    const fake = readFake();
    if (content === null) {
      delete fake.launchers[launcherId(file)];
    } else {
      fake.launchers[launcherId(file)] = content;
    }
    writeFake(fake);
    return;
  }
  if (content === null) {
    fs.rmSync(file, { force: true });
    return;
  }
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, content, "utf8");
}

function splitPath(value) {
  return String(value || "").split(";").map((part) => part.trim()).filter(Boolean);
}

const RESERVED_ENV = new Set(["path", "pathext", "comspec", "systemroot", "windir", "userprofile", "appdata", "temp", "tmp", "username", "computername"]);

async function wireSetEnv(args) {
  const name = envName(args.name);
  if (RESERVED_ENV.has(name.toLowerCase())) {
    throw new StorageError("ERR_UNSUPPORTED", "refusing to overwrite the system variable " + name);
  }
  const value = args.value === undefined || args.value === null ? null : String(args.value);
  if (value !== null && (value.length > 4096 || value.includes("\u0000"))) {
    throw new StorageError("ERR_BAD_ARGS", "environment value is too long or contains a NUL");
  }
  const before = readUserEnv(name);
  if (before === value) {
    return { before, after: value, changed: false };
  }
  await ledgerEntry(args.task, {
    op: "env.set",
    name,
    before,
    after: value,
    undo: before === null
      ? { cap: "storage.wire", op: "env.delete", args: { name } }
      : { cap: "storage.wire", op: "env.set", args: { name, value: before } },
  });
  writeUserEnv(name, value);
  return { before, after: value, changed: true };
}

async function wireDeleteEnv(args) {
  return wireSetEnv({ task: args.task, name: args.name, value: null });
}

async function wireAddPath(args) {
  const dir = path.resolve(String(args.dir || ""));
  if (!inside(path.join(stableRoot(), "current"), dir)) {
    throw new StorageError("ERR_UNSUPPORTED", "path.add only accepts directories under the stable root's current/");
  }
  const before = readUserPath();
  const parts = splitPath(before);
  if (parts.some((part) => part.toLowerCase() === dir.toLowerCase())) {
    return { before, after: before, changed: false };
  }
  const after = [...parts, dir].join(";");
  await ledgerEntry(args.task, {
    op: "path.add",
    dir,
    before,
    after,
    undo: { cap: "storage.wire", op: "path.remove", args: { dir } },
  });
  writeUserPath(after);
  return { before, after, changed: true };
}

async function wireRemovePath(args) {
  const dir = String(args.dir || "");
  const before = readUserPath();
  const parts = splitPath(before);
  const kept = parts.filter((part) => part.toLowerCase() !== dir.toLowerCase());
  if (kept.length === parts.length) {
    return { before, after: before, changed: false };
  }
  const after = kept.length > 0 ? kept.join(";") : null;
  await ledgerEntry(args.task, {
    op: "path.remove",
    dir,
    before,
    after,
    undo: { cap: "storage.wire", op: "path.add", args: { dir } },
  });
  writeUserPath(after);
  return { before, after, changed: true };
}

async function wireSetRegistry(args) {
  const hive = String(args.hive || "HKCU").toUpperCase();
  const entry = { hive: hive, key: String(args.key || ""), name: args.name || "" };
  if (hive !== "HKCU" && hive !== "HKLM") {
    throw new StorageError("ERR_UNSUPPORTED", "only HKCU/HKLM are supported (asked for " + args.hive + ")");
  }
  if (entry.key.length === 0 || entry.key.includes("\u0000") || entry.name.includes("\u0000")) {
    throw new StorageError("ERR_BAD_ARGS", "registry key/name is empty or contains a NUL");
  }
  const value = args.value === undefined || args.value === null
    ? null
    : { type: String(args.type || "REG_SZ"), value: String(args.value) };
  if (value !== null && /(^|[^A-Za-z])[A-Za-z]:[\\/]/.test(value.value)) {
    throw new StorageError("ERR_UNSUPPORTED", "registry path values must use the stable root, not a drive letter");
  }
  if (hive === "HKLM" && !FAKE) {
    // Elevation: one helper call does get+set. The ledger entry is written after the effect
    // (documented limitation: a crash between the two leaves an unaudited machine change).
    const results = runElevated([{
      op: value === null ? "registry.delete" : "registry.set",
      hive: hive,
      key: entry.key,
      name: entry.name,
      type: value === null ? undefined : value.type,
      value: value === null ? undefined : value.value,
    }]);
    const before = (results[0] && results[0].before) || null;
    if (JSON.stringify(before) === JSON.stringify(value)) {
      return { before, after: value, changed: false };
    }
    await ledgerEntry(args.task, {
      op: "registry.set",
      hive: entry.hive,
      key: entry.key,
      name: entry.name,
      before,
      after: value,
      undo: before === null
        ? { cap: "storage.wire", op: "registry.delete", args: { hive: entry.hive, key: entry.key, name: entry.name } }
        : { cap: "storage.wire", op: "registry.set", args: { hive: entry.hive, key: entry.key, name: entry.name, type: before.type, value: before.value } },
    });
    return { before, after: value, changed: true };
  }

  const before = readRegistry(entry);
  if (JSON.stringify(before) === JSON.stringify(value)) {
    return { before, after: value, changed: false };
  }
  await ledgerEntry(args.task, {
    op: "registry.set",
    hive: entry.hive,
    key: entry.key,
    name: entry.name,
    before,
    after: value,
    undo: before === null
      ? { cap: "storage.wire", op: "registry.delete", args: { hive: entry.hive, key: entry.key, name: entry.name } }
      : { cap: "storage.wire", op: "registry.set", args: { hive: entry.hive, key: entry.key, name: entry.name, type: before.type, value: before.value } },
  });
  writeRegistry(entry, value);
  return { before, after: value, changed: true };
}

async function wireDeleteRegistry(args) {
  return wireSetRegistry({ task: args.task, hive: args.hive, key: args.key, name: args.name, value: null });
}

// Launchers live directly under the stable root as <name>.cmd. A lexical inside() check is not
// enough: C:\Dev\current\<pkg> is a junction into the CAS, so a path that only looks inside could
// resolve through a link and overwrite store binaries. Require a direct .cmd child whose real
// path still sits directly under the stable root.
function realPathOfExisting(target) {
  let current = path.resolve(target);
  const missing = [];
  while (!fs.existsSync(current)) {
    const parent = path.dirname(current);
    if (parent === current) {
      break;
    }
    missing.unshift(path.basename(current));
    current = parent;
  }

  let real = current;
  try {
    real = fs.realpathSync.native(current);
  } catch (error) {
    real = current;
  }

  return missing.length > 0 ? path.join(real, ...missing) : real;
}

function assertLauncherPath(file, op) {
  const base = path.resolve(stableRoot());
  const launcherDir = path.join(base, "launchers");
  const target = path.resolve(file);
  const direct = path.dirname(target).toLowerCase();
  if (!inside(base, target) || (direct !== base.toLowerCase() && direct !== launcherDir.toLowerCase())) {
    throw new StorageError("ERR_UNSUPPORTED", op + " only accepts launchers under the stable root or its launchers/ directory");
  }
  if (!/\.cmd$/i.test(target)) {
    throw new StorageError("ERR_UNSUPPORTED", op + " only accepts .cmd launchers");
  }
  // The stable root itself may be a junction onto the stick (C:\Dev -> stick in the
  // default setup), so compare against its resolved form, not the literal setting.
  let realBase = base;
  try {
    realBase = fs.realpathSync.native(base);
  } catch (error) {
    realBase = base;
  }

  const real = path.resolve(realPathOfExisting(target));
  const realParent = path.dirname(real).toLowerCase();
  const realBaseNorm = path.resolve(realBase).toLowerCase();
  if (realParent !== realBaseNorm && realParent !== path.join(realBase, "launchers").toLowerCase()) {
    throw new StorageError("ERR_UNSUPPORTED", op + " path crosses a link or junction");
  }
}

async function wireWriteLauncher(args) {
  const file = path.resolve(String(args.path || ""));
  assertLauncherPath(file, "launcher.write");
  const content = String(args.content || "");
  if (content.includes("\u0000")) {
    throw new StorageError("ERR_BAD_ARGS", "launcher content contains a NUL");
  }
  const before = readLauncher(file);
  if (before === content) {
    return { before, after: content, changed: false };
  }
  await ledgerEntry(args.task, {
    op: "launcher.write",
    path: file,
    before,
    after: content,
    undo: before === null
      ? { cap: "storage.wire", op: "launcher.remove", args: { path: file } }
      : { cap: "storage.wire", op: "launcher.write", args: { path: file, content: before } },
  });
  fs.mkdirSync(path.dirname(file), { recursive: true });
  writeLauncher(file, content);
  return { before, after: content, changed: true };
}

async function wireRemoveLauncher(args) {
  const file = path.resolve(String(args.path || ""));
  assertLauncherPath(file, "launcher.remove");
  const before = readLauncher(file);
  if (before === null) {
    return { before, after: null, changed: false };
  }
  await ledgerEntry(args.task, {
    op: "launcher.remove",
    path: file,
    before,
    after: null,
    undo: { cap: "storage.wire", op: "launcher.write", args: { path: file, content: before } },
  });
  writeLauncher(file, null);
  return { before, after: null, changed: true };
}

function expandText(record, value) {
  return String(value === undefined || value === null ? "" : value)
    .replace(/\{\{ROOT\}\}/g, stableRoot())
    .replace(/\{\{STICK\}\}/g, ROOT)
    .replace(/\{\{HOME\}\}/g, path.join(stableRoot(), "home", record.id))
    .replace(/\{\{PERSIST\}\}/g, path.join(stableRoot(), "home", record.id))
    .replace(/\{\{APPDATA\}\}/g, process.env.APPDATA || "")
    .replace(/\{\{LOCALAPPDATA\}\}/g, process.env.LOCALAPPDATA || "")
    .replace(/\{\{USERPROFILE\}\}/g, process.env.USERPROFILE || "")
    .replace(/\{\{ID\}\}/g, record.id)
    .replace(/\{\{VERSION\}\}/g, String(record.version || ""));
}

function homeStickPath(pkg) {
  return path.join(ROOT, "home", pkg);
}

function homeMachinePath(pkg) {
  return path.join(stableRoot(), "home", pkg);
}

async function ensureHome(args) {
  const pkg = packageId(args.pkg, "home package id");
  const stick = homeStickPath(pkg);
  const machine = homeMachinePath(pkg);
  fs.mkdirSync(stick, { recursive: true });
  // When the stable root IS this stick root (C:\Dev is a junction to the stick), the
  // machine-visible home path and the stick path are the same directory; a junction there
  // would point at itself and fail with EEXIST.
  let sameRoot = false;
  try {
    sameRoot = path.resolve(fs.realpathSync(stableRoot())).toLowerCase() ===
      path.resolve(fs.realpathSync(ROOT)).toLowerCase();
  } catch (error) {
    sameRoot = path.resolve(stableRoot()).toLowerCase() === path.resolve(ROOT).toLowerCase();
  }
  if (sameRoot) {
    return { stickPath: stick, machinePath: stick, changed: false };
  }
  let changed = false;
  const existing = readLinkTarget(machine);
  if (existing === null) {
    writeLink(machine, stick);
    changed = true;
  } else if (path.resolve(existing) !== path.resolve(stick)) {
    throw new StorageError("ERR_IO", "home junction points elsewhere: " + machine);
  }

  logInfo("storage.home", "home directory ready", { pkg: pkg, stickPath: stick, machinePath: machine });
  return { stickPath: stick, machinePath: machine, changed: changed };
}

// Undo primitive for home.purge: the data directory is moved to state/trash (same-volume rename,
// atomic) instead of being deleted, so rollback can put the user's files back. pd gc empties trash.
async function restoreHome(args) {
  const pkg = packageId(args.pkg, "home.restore package id");
  const from = path.resolve(String(args.from || ""));
  const stick = path.resolve(homeStickPath(pkg));
  if (!inside(STATE, from)) {
    throw new StorageError("ERR_UNSUPPORTED", "home.restore source must be inside state/: " + from);
  }
  if (path.resolve(String(args.to || stick)) !== stick) {
    throw new StorageError("ERR_BAD_ARGS", "home.restore target does not match the package home");
  }
  let restored = false;
  if (fs.existsSync(from) && !fs.existsSync(stick)) {
    fs.mkdirSync(path.dirname(stick), { recursive: true });
    fs.renameSync(from, stick);
    restored = true;
  }
  const home = await ensureHome({ pkg });
  logInfo("storage.home", "home directory restored", { pkg, stickPath: stick, restored });
  return { pkg, restored, stickPath: stick, machinePath: home.machinePath };
}

function recordWiring(record) {
  return record.wiring && typeof record.wiring === "object" ? record.wiring : {};
}

function ensureBefore(record) {
  const wiring = recordWiring(record);
  if (!wiring.before || typeof wiring.before !== "object") {
    wiring.before = {};
  }

  if (!Object.prototype.hasOwnProperty.call(wiring.before, "fake")) {
    // Remember which machine view the values came from: restoring fake-mode values onto the real
    // machine (or vice versa) is meaningless, so those records fall back to plain removal.
    wiring.before.fake = FAKE;
  }

  if (!Object.prototype.hasOwnProperty.call(wiring.before, "machine")) {
    // The stick travels between machines; restoring A's captured env/registry values on B would
    // corrupt B. Records without this field predate the feature and keep the old behaviour.
    wiring.before.machine = MACHINE_ID || "unknown";
  }

  for (const key of ["path", "env", "registry", "launchers"]) {
    if (!wiring.before[key] || typeof wiring.before[key] !== "object") {
      wiring.before[key] = {};
    }
  }

  return wiring.before;
}

function hasOwn(object, key) {
  return Object.prototype.hasOwnProperty.call(object, key);
}

function registryKey(entry) {
  return String(entry.hive || "HKCU") + "|" + String(entry.key || "") + "|" + String(entry.name || "");
}

// A launcher stub is only useful when the executable is not already reachable through the PATH
// entries the package wires. Scoop packages put every bin directory on PATH, so their stubs were
// pure clutter in the stick root (cmake.cmd, ffmpeg.cmd, ...).
function launcherCoveredByPath(record, wiring, launcher) {
  const rel = String((launcher && launcher.exe) || "");
  if (!rel) {
    return false;
  }
  const exeDir = path.dirname(path.join(stableRoot(), "current", record.id, expandText(record, rel)));
  const normalize = (value) => path.resolve(value).toLowerCase();
  for (const entry of wiring.path || []) {
    const dir = path.join(stableRoot(), "current", record.id, expandText(record, String(entry)));
    if (normalize(dir) === normalize(exeDir)) {
      return true;
    }
  }
  return false;
}

function launcherPath(record, launcher) {
  const name = String((launcher && launcher.name) || "");
  if (!/^[A-Za-z0-9._-]{1,64}$/.test(name)) {
    throw new StorageError("ERR_PKG_INVALID", "launcher name is not a safe file name: " + JSON.stringify(name));
  }
  return path.join(stableRoot(), "launchers", name + ".cmd");
}

async function applyRecord(record, task, dryRun) {
  let changed = 0;
  const wiring = recordWiring(record);
  const link = path.join(CURRENT, record.id);
  const linkTarget = readLink(link);
  const needsLink = Boolean(record.storePath) && fs.existsSync(record.storePath) &&
    (!linkTarget || path.resolve(linkTarget) !== path.resolve(record.storePath));

  if (dryRun) {
    if (needsLink) {
      changed += 1;
    }
    const pathEntries = splitPath(readUserPath());
    for (const relative of wiring.path || []) {
      const dir = path.join(stableRoot(), "current", record.id, String(relative));
      if (!pathEntries.some((part) => part.toLowerCase() === dir.toLowerCase())) {
        changed += 1;
      }
    }
    for (const [name, value] of Object.entries(wiring.env || {})) {
      if (readUserEnv(name) !== expandText(record, value)) {
        changed += 1;
      }
    }
    for (const entry of wiring.registry || []) {
      const before = readRegistry({ hive: entry.hive || "HKCU", key: entry.key, name: entry.name || "" });
      const after = { type: String(entry.type || "REG_SZ"), value: expandText(record, entry.value) };
      if (JSON.stringify(before) !== JSON.stringify(after)) {
        changed += 1;
      }
    }
    for (const launcher of wiring.launch || []) {
      if (!launcher || !launcher.name || launcherCoveredByPath(record, wiring, launcher)) {
        continue;
      }
      if (readLauncher(launcherPath(record, launcher)) !== launcherContent(record, launcher)) {
        changed += 1;
      }
    }
    return changed;
  }

  await ensureBegin(task, record.id);

  if (needsLink) {
    await currentSet({ task, pkg: record.id, storePath: record.storePath });
    changed += 1;
  }

  const wiringText = JSON.stringify(wiring);
  const needsHome = wiringText.includes("{{HOME}}") || wiringText.includes("{{PERSIST}}") ||
    (Array.isArray(wiring.data) && wiring.data.length > 0) ||
    (Array.isArray(wiring.persist) && wiring.persist.length > 0);
  if (needsHome) {
    await ensureHome({ pkg: record.id });
  }

  // Snapshot the machine's pre-install values once, so clean/unwire can restore them instead of
  // blindly deleting (which used to wipe a user's own env var / registry value / launcher).
  const before = ensureBefore(record);
  const beforePath = splitPath(readUserPath());

  for (const relative of wiring.path || []) {
    const dir = path.join(stableRoot(), "current", record.id, String(relative));
    if (!hasOwn(before.path, dir)) {
      before.path[dir] = beforePath.some((part) => part.toLowerCase() === dir.toLowerCase());
    }
    if ((await wireAddPath({ task, dir })).changed) {
      changed += 1;
    }
  }

  for (const [name, value] of Object.entries(wiring.env || {})) {
    if (!hasOwn(before.env, name)) {
      before.env[name] = readUserEnv(name);
    }
    if ((await wireSetEnv({ task, name, value: expandText(record, value) })).changed) {
      changed += 1;
    }
  }

  for (const entry of wiring.registry || []) {
    const key = registryKey(entry);
    if (!hasOwn(before.registry, key)) {
      before.registry[key] = readRegistry({ hive: entry.hive || "HKCU", key: entry.key, name: entry.name || "" });
    }
    if ((await wireSetRegistry({ task, hive: entry.hive, key: entry.key, name: entry.name, type: entry.type, value: expandText(record, entry.value) })).changed) {
      changed += 1;
    }
  }

  for (const launcher of wiring.launch || []) {
    if (!launcher || !launcher.name) {
      continue;
    }
    if (launcherCoveredByPath(record, wiring, launcher)) {
      // Drop stubs written by older versions: the executable is already on PATH.
      try {
        const staleFile = launcherPath(record, launcher);
        const staleContent = readLauncher(staleFile);
        if (staleContent === launcherContent(record, launcher)) {
          if (!hasOwn(before.launchers, staleFile)) {
            before.launchers[staleFile] = staleContent;
          }
          fs.rmSync(staleFile, { force: true });
          changed += 1;
        }
      } catch (error) {
        // best effort: an unreadable stub is not worth failing the apply
      }
      continue;
    }
    const file = launcherPath(record, launcher);
    if (!hasOwn(before.launchers, file)) {
      before.launchers[file] = readLauncher(file);
    }
    if ((await wireWriteLauncher({ task, path: file, content: launcherContent(record, launcher) })).changed) {
      changed += 1;
    }
  }

  changed += await applyPersist(record, task);

  for (const item of wiring.data || []) {
    const kind = String((item && item.kind) || "");
    if (kind === "link") {
      const source = expandText(record, item.source || "");
      const target = expandText(record, item.target || "");
      if (!source || !target) {
        continue;
      }
      const result = await wireLinkCreate({ task: task, source: source, target: target });
      if (result.changed) {
        changed += 1;
      }
      continue;
    }

    if (kind === "copy") {
      const source = expandText(record, item.source || "");
      const target = expandText(record, item.target || "");
      if (!source || !target) {
        continue;
      }
      if ((await wireCopyCreate({ task: task, source: source, target: target })).changed) {
        changed += 1;
      }
      continue;
    }
  }

  return changed;
}

async function applyRecords(args) {
  const task = args.task;
  const only = args.only ? String(args.only) : null;
  const records = [];
  for (const name of fs.readdirSync(RECORDS)) {
    if (!name.endsWith(".json")) {
      continue;
    }
    try {
      const record = readJson(path.join(RECORDS, name));
      if (!only || record.id === only) {
        records.push(record);
      }
    } catch (error) {
      // a corrupt record is doctor's problem
    }
  }

  let changed = 0;
  const dirty = [];
  for (const record of records) {
    const snapshot = JSON.stringify(record);
    changed += await applyRecord(record, task, args.dryRun === true);
    if (args.dryRun !== true && JSON.stringify(record) !== snapshot) {
      dirty.push({ record, previous: JSON.parse(snapshot) });
    }
  }

  if (args.dryRun !== true) {
    for (const { record, previous } of dirty) {
      await ledgerEntry(task, {
        op: "record.write",
        pkg: record.id,
        before: previous,
        after: record,
        undo: { cap: "storage.record", op: "write", args: { record: previous } },
      });
      writeJsonAtomic(path.join(RECORDS, record.id + ".json"), record);
    }
  }

  let generationSeq = null;
  if (args.dryRun !== true) {
    if (prioritizeManagedPath()) {
      changed += 1;
    }
    if (changed > 0) {
      writeJsonAtomic(LAST_APPLY_FILE, { schema: 1, task: task || null, at: now(), packages: records.map((record) => record.id) });
    }
    if (changed > 0 || dirty.length > 0 || generationFiles().length === 0) {
      generationSeq = writeGeneration(readAllRecords());
    }
  }

  return { packages: records.length, changed, dryRun: args.dryRun === true, generationSeq };
}

async function cleanWiring(args) {
  const task = args.task;
  const dryRun = args.dryRun === true;
  let changed = 0;

  for (const name of fs.readdirSync(RECORDS)) {
    if (!name.endsWith(".json")) {
      continue;
    }
    let record;
    try {
      record = readJson(path.join(RECORDS, name));
    } catch (error) {
      continue;
    }

    if (dryRun) {
      changed += await previewRestore(record);
      continue;
    }

    changed += await restoreWiring(record, task);
  }

  let junctions = 0;
  if (!dryRun) {
    for (const name of fs.readdirSync(RECORDS)) {
      if (!name.endsWith(".json")) {
        continue;
      }
      try {
        const record = readJson(path.join(RECORDS, name));
        const link = path.join(CURRENT, record.id);
        if (fs.existsSync(link) || readLink(link) !== null) {
          removeLink(link);
          junctions += 1;
        }
      } catch (error) {
        // ignore corrupt record
      }
    }

    fs.rmSync(LAST_APPLY_FILE, { force: true });
  }

  return { changed, junctions, dryRun };
}

// ---- generations: snapshots of the active package set + managed wiring ----

const GENERATIONS = path.join(STATE, "generations");
fs.mkdirSync(GENERATIONS, { recursive: true });

function readSettingNumber(section, key, fallback) {
  try {
    const settings = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "settings.json"), "utf8"));
    const sectionValue = settings && settings[section] ? settings[section][key] : undefined;
    if (sectionValue === undefined || sectionValue === null || sectionValue === "") {
      return fallback;
    }
    const value = Number(sectionValue);
    return Number.isFinite(value) ? value : fallback;
  } catch (error) {
    return fallback;
  }
}

function launcherContent(record, launcher) {
  const relative = String((launcher && launcher.exe) || "");
  if (path.isAbsolute(relative) || /^[A-Za-z]:/.test(relative) || relative.replace(/\\/g, "/").split("/").includes("..")) {
    throw new StorageError("ERR_PKG_INVALID", "launcher exe must stay inside the package: " + relative);
  }

  const base = path.join(stableRoot(), "current", record.id);
  const exe = path.join(base, expandText(record, relative));
  if (/["\r\n\u0000]/.test(exe)) {
    throw new StorageError("ERR_PKG_INVALID", "launcher exe contains an illegal character");
  }

  const resolved = path.resolve(exe);
  if (!resolved.toLowerCase().startsWith(path.resolve(base).toLowerCase() + path.sep)) {
    throw new StorageError("ERR_PKG_INVALID", "launcher exe escapes the package directory: " + relative);
  }

  // Optional fixed arguments (for example Firefox's -profile/-no-remote); %* still passes extras.
  const fixed = (launcher.args || [])
    .map((item) => expandText(record, String(item)))
    .filter((item) => item.length > 0)
    .map((item) => {
      if (/[\r\n\u0000]/.test(item)) {
        throw new StorageError("ERR_PKG_INVALID", "launcher argument contains an illegal character");
      }
      const escaped = item.replace(/%/g, "%%");
      return /[ \t]/.test(escaped) ? "\"" + escaped + "\"" : escaped;
    })
    .join(" ");

  // Percent signs expand inside a batch file even in quotes; double them. %* stays a pass-through.
  return "@echo off\r\nsetlocal\r\nstart \"\" \"" + exe.replace(/%/g, "%%") + "\"" +
    (fixed ? " " + fixed : "") + " %*\r\n";
}

function readAllRecords() {
  const records = {};
  for (const name of fs.readdirSync(RECORDS)) {
    if (!name.endsWith(".json")) {
      continue;
    }
    try {
      const record = readJson(path.join(RECORDS, name));
      if (record && record.id && PACKAGE_ID.test(String(record.id))) {
        records[record.id] = record;
      }
    } catch (error) {
      // a corrupt record is doctor's problem
    }
  }
  return records;
}

function snapshotState(records) {
  const snapshot = { packages: {}, wiring: { path: [], env: {}, registry: [], launchers: [] } };
  for (const record of Object.values(records)) {
    snapshot.packages[record.id] = record;
    const wiring = recordWiring(record);
    for (const relative of wiring.path || []) {
      snapshot.wiring.path.push(path.join(stableRoot(), "current", record.id, String(relative)));
    }
    for (const [name, value] of Object.entries(wiring.env || {})) {
      snapshot.wiring.env[name] = expandText(record, value);
    }
    for (const entry of wiring.registry || []) {
      snapshot.wiring.registry.push({
        hive: entry.hive || "HKCU",
        key: entry.key,
        name: entry.name || "",
        type: entry.type || "REG_SZ",
        value: expandText(record, entry.value),
      });
    }
    for (const launcher of wiring.launch || []) {
      if (!launcher || !launcher.name) {
        continue;
      }
      snapshot.wiring.launchers.push({
        path: launcherPath(record, launcher),
        content: launcherContent(record, launcher),
      });
    }
  }
  return snapshot;
}

function generationFiles() {
  return fs.readdirSync(GENERATIONS)
    .filter((name) => name.endsWith(".json"))
    .map((name) => {
      const match = /^(\d+)-/.exec(name);
      return { name, seq: match ? Number(match[1]) : Number.MAX_SAFE_INTEGER };
    })
    .sort((left, right) => left.seq - right.seq || left.name.localeCompare(right.name))
    .map((entry) => entry.name);
}

function nextGenerationSeq() {
  let max = 0;
  for (const name of generationFiles()) {
    const match = /^(\d+)-/.exec(name);
    if (match) {
      max = Math.max(max, Number(match[1]));
    }
  }
  return max + 1;
}

function readGeneration(seq) {
  for (const name of generationFiles()) {
    const match = /^(\d+)-/.exec(name);
    if (match && Number(match[1]) === Number(seq)) {
      try {
        return readJson(path.join(GENERATIONS, name));
      } catch (error) {
        return null;
      }
    }
  }
  return null;
}

function writeGeneration(records) {
  const snapshot = snapshotState(records);
  const seq = nextGenerationSeq();
  const document = Object.assign({ schema: 1, seq, at: now() }, snapshot);
  const stamp = now().replace(/[:.]/g, "-");
  writeJsonAtomic(path.join(GENERATIONS, String(seq).padStart(4, "0") + "-" + stamp + ".json"), document);

  const keep = Math.max(1, readSettingNumber("gc", "keepGenerations", 50));
  const files = generationFiles();
  while (files.length > keep) {
    const oldest = files.shift();
    fs.rmSync(path.join(GENERATIONS, oldest), { force: true });
  }

  return seq;
}

// Restore one record's wiring to the pre-install values captured by applyRecord (missing before =
// plain removal, which is the old behavior for records installed before this feature).
async function restoreWiring(record, task) {
  let changed = 0;
  const wiring = recordWiring(record);
  const raw = wiring.before && typeof wiring.before === "object" ? wiring.before : {};
  const sameMode = (raw.fake === true) === FAKE;
  if (raw.fake !== undefined && !sameMode) {
    // This record was wired against the other machine view (fake vs real); leave this machine alone.
    return 0;
  }

  const crossMachine = Boolean(raw.machine) && Boolean(MACHINE_ID) && raw.machine !== MACHINE_ID;
  if (crossMachine) {
    logInfo("storage.wire", "record was wired on another machine; removing without restoring its old values",
      { pkg: record.id, recordMachine: raw.machine, machine: MACHINE_ID });
  }

  const before = sameMode && !crossMachine ? raw : {};
  const beforePath = before.path || {};
  const beforeEnv = before.env || {};
  const beforeRegistry = before.registry || {};
  const beforeLaunchers = before.launchers || {};

  for (const relative of wiring.path || []) {
    const dir = path.join(stableRoot(), "current", record.id, String(relative));
    if (hasOwn(beforePath, dir) && beforePath[dir] === true) {
      continue;
    }
    if ((await wireRemovePath({ task, dir })).changed) {
      changed += 1;
    }
  }

  for (const name of Object.keys(wiring.env || {})) {
    const prior = hasOwn(beforeEnv, name) ? beforeEnv[name] : null;
    if (prior !== null && prior !== undefined) {
      if ((await wireSetEnv({ task, name, value: String(prior) })).changed) {
        changed += 1;
      }
    } else if ((await wireDeleteEnv({ task, name })).changed) {
      changed += 1;
    }
  }

  for (const entry of wiring.registry || []) {
    const key = registryKey(entry);
    const prior = hasOwn(beforeRegistry, key) ? beforeRegistry[key] : null;
    if (prior && typeof prior === "object" && prior.value !== null && prior.value !== undefined) {
      if ((await wireSetRegistry({ task, hive: entry.hive || "HKCU", key: entry.key, name: entry.name || "", type: prior.type || "REG_SZ", value: String(prior.value) })).changed) {
        changed += 1;
      }
    } else if ((await wireDeleteRegistry({ task, hive: entry.hive, key: entry.key, name: entry.name })).changed) {
      changed += 1;
    }
  }

  for (const launcher of wiring.launch || []) {
    if (!launcher || !launcher.name) {
      continue;
    }
    const file = launcherPath(record, launcher);
    const prior = hasOwn(beforeLaunchers, file) ? beforeLaunchers[file] : null;
    if (typeof prior === "string") {
      if ((await wireWriteLauncher({ task, path: file, content: prior })).changed) {
        changed += 1;
      }
    } else if ((await wireRemoveLauncher({ task, path: file })).changed) {
      changed += 1;
    }
  }

  for (const item of wiring.data || []) {
    const kind = String((item && item.kind) || "");
    const target = expandText(record, item.target || "");
    if (!target) {
      continue;
    }
    if (kind === "link") {
      if ((await wireLinkRemove({ task: task, target: target })).changed) {
        changed += 1;
      }
    } else if (kind === "copy") {
      if ((await wireCopyRemove({ task: task, target: target, source: expandText(record, item.source || "") })).changed) {
        changed += 1;
      }
    }
  }

  for (const relative of wiring.persist || []) {
    // Stick-local links: remove them for real regardless of the fake machine mode.
    const target = path.join(record.storePath, ...normalizePersistPath(relative).split("/"));
    if (readLink(target) !== null) {
      if (removeLink(target)) {
        changed += 1;
      }
    }
  }

  return changed;
}

async function previewRestore(record) {
  let wouldChange = 0;
  const wiring = recordWiring(record);
  const raw = wiring.before && typeof wiring.before === "object" ? wiring.before : {};
  const sameMode = (raw.fake === true) === FAKE;
  if (raw.fake !== undefined && !sameMode) {
    return 0;
  }

  const crossMachine = Boolean(raw.machine) && Boolean(MACHINE_ID) && raw.machine !== MACHINE_ID;
  const before = sameMode && !crossMachine ? raw : {};
  const beforePath = before.path || {};
  const beforeEnv = before.env || {};
  const beforeRegistry = before.registry || {};
  const beforeLaunchers = before.launchers || {};
  const pathEntries = splitPath(readUserPath());

  for (const relative of wiring.path || []) {
    const dir = path.join(stableRoot(), "current", record.id, String(relative));
    if (hasOwn(beforePath, dir) && beforePath[dir] === true) {
      continue;
    }
    if (pathEntries.some((part) => part.toLowerCase() === dir.toLowerCase())) {
      wouldChange += 1;
    }
  }

  for (const name of Object.keys(wiring.env || {})) {
    const prior = hasOwn(beforeEnv, name) ? beforeEnv[name] : null;
    const current = readUserEnv(name);
    if (prior !== null && prior !== undefined ? current !== String(prior) : current !== null) {
      wouldChange += 1;
    }
  }

  for (const entry of wiring.registry || []) {
    const key = registryKey(entry);
    const prior = hasOwn(beforeRegistry, key) ? beforeRegistry[key] : null;
    const current = readRegistry({ hive: entry.hive || "HKCU", key: entry.key, name: entry.name || "" });
    if (prior && typeof prior === "object" && prior.value !== null && prior.value !== undefined) {
      if (JSON.stringify(current) !== JSON.stringify({ type: prior.type || "REG_SZ", value: String(prior.value) })) {
        wouldChange += 1;
      }
    } else if (current !== null) {
      wouldChange += 1;
    }
  }

  for (const launcher of wiring.launch || []) {
    if (!launcher || !launcher.name) {
      continue;
    }
    const file = launcherPath(record, launcher);
    const prior = hasOwn(beforeLaunchers, file) ? beforeLaunchers[file] : null;
    const current = readLauncher(file);
    const target = typeof prior === "string" ? prior : null;
    if (current !== target) {
      wouldChange += 1;
    }
  }

  for (const item of wiring.data || []) {
    const kind = String((item && item.kind) || "");
    const target = expandText(record, item.target || "");
    if (!target) {
      continue;
    }
    if (kind === "link" && readLinkTarget(target) !== null) {
      wouldChange += 1;
    } else if (kind === "copy") {
      const exists = FAKE
        ? Object.prototype.hasOwnProperty.call(readFake().copies || {}, path.resolve(target))
        : fs.existsSync(target);
      if (exists) {
        wouldChange += 1;
      }
    }
  }

  return wouldChange;
}

async function unwireRecord(record, task) {
  return restoreWiring(record, task);
}

function validateGeneration(document, expectedSeq) {
  if (!document || typeof document !== "object" || Array.isArray(document)) {
    throw new StorageError("ERR_PKG_INVALID", "generation document is not an object");
  }
  if (document.schema !== 1) {
    throw new StorageError("ERR_PKG_INVALID", "generation schema must be 1");
  }
  if (expectedSeq !== undefined && Number(document.seq) !== Number(expectedSeq)) {
    throw new StorageError("ERR_PKG_INVALID", "generation file seq " + expectedSeq + " does not match document seq " + document.seq);
  }

  const packages = document.packages;
  if (!packages || typeof packages !== "object" || Array.isArray(packages)) {
    throw new StorageError("ERR_PKG_INVALID", "generation packages must be an object");
  }

  for (const [id, record] of Object.entries(packages)) {
    packageId(id, "generation package id");
    if (!record || typeof record !== "object" || Array.isArray(record)) {
      throw new StorageError("ERR_PKG_INVALID", "generation record for " + id + " is not an object");
    }
    if (String(record.id) !== id) {
      throw new StorageError("ERR_PKG_INVALID", "generation record id mismatch for " + id);
    }
    packageVersion(record.version, "generation record version");
    if (!record.storePath || !inside(STORE, record.storePath)) {
      throw new StorageError("ERR_PKG_INVALID", "generation store path for " + id + " escapes store/");
    }
    if (!fs.existsSync(record.storePath)) {
      throw new StorageError("ERR_PKG_NOT_FOUND", "store path for " + id + " was removed by gc: " + record.storePath, 20);
    }
    if (record.wiring !== undefined && (record.wiring === null || typeof record.wiring !== "object" || Array.isArray(record.wiring))) {
      throw new StorageError("ERR_PKG_INVALID", "generation wiring for " + id + " is not an object");
    }
  }

  return packages;
}

// ---- restore checkpoint: an interrupted generation restore resumes on the next attempt ----
function restoreCheckpointPath(seq) {
  return path.join(GENERATIONS, "restore-" + seq + ".json");
}

function readRestoreCheckpoint(seq) {
  try {
    const document = readJson(restoreCheckpointPath(seq));
    if (document && document.schema === 1 && Number(document.seq) === Number(seq) && Array.isArray(document.steps)) {
      return document;
    }
  } catch (error) {
    // A corrupt checkpoint only means the restore starts over; the operations are idempotent.
  }
  return { schema: 1, seq: seq, steps: [], appliedSeq: null };
}

function saveRestoreCheckpoint(checkpoint) {
  writeJsonAtomic(restoreCheckpointPath(checkpoint.seq), checkpoint);
}

async function restoreGeneration(args) {
  const seq = Number(args.seq);
  const generation = readGeneration(seq);
  if (!generation) {
    throw new StorageError("ERR_PKG_NOT_FOUND", "generation " + seq + " does not exist", 20);
  }

  const target = validateGeneration(generation, seq);

  const checkpoint = readRestoreCheckpoint(seq);
  const resumedFromCheckpoint = checkpoint.steps.length > 0;
  const finished = new Set(checkpoint.steps);
  const mark = (step) => {
    if (!finished.has(step)) {
      finished.add(step);
      checkpoint.steps = Array.from(finished);
      saveRestoreCheckpoint(checkpoint);
    }
  };
  if (finished.size > 0) {
    logInfo("storage.generation", "resuming an interrupted restore", { seq: seq, steps: checkpoint.steps.length });
  }

  const task = args.task;
  await ensureBegin(task, "generation");
  const current = readAllRecords();

  for (const [id, record] of Object.entries(current)) {
    if (target[id]) {
      continue;
    }
    const unwireStep = "unwire:" + id;
    if (!finished.has(unwireStep)) {
      await unwireRecord(record, task);
      mark(unwireStep);
    }

    const deleteStep = "delete:" + id;
    if (!finished.has(deleteStep)) {
      const link = path.join(CURRENT, id);
      if (fs.existsSync(link) || readLink(link) !== null) {
        removeLink(link);
      }
      await ledgerEntry(task, {
        op: "record.delete",
        pkg: id,
        before: record,
        after: null,
        undo: { cap: "storage.record", op: "write", args: { record } },
      });
      fs.rmSync(path.join(RECORDS, id + ".json"), { force: true });
      mark(deleteStep);
    }
  }

  for (const [id, record] of Object.entries(target)) {
    const existing = current[id] || null;
    if (existing && JSON.stringify(existing) === JSON.stringify(record)) {
      continue;
    }
    const writeStep = "write:" + id;
    if (finished.has(writeStep)) {
      continue;
    }
    await ledgerEntry(task, {
      op: "record.write",
      pkg: id,
      before: existing,
      after: record,
      undo: existing === null
        ? { cap: "storage.record", op: "delete", args: { pkg: id } }
        : { cap: "storage.record", op: "write", args: { record: existing } },
    });
    writeJsonAtomic(path.join(RECORDS, id + ".json"), record);
    mark(writeStep);
  }

  let applied = { changed: 0, generationSeq: checkpoint.appliedSeq || null };
  if (!finished.has("apply")) {
    applied = await applyRecords({ task });
    checkpoint.appliedSeq = applied.generationSeq;
    mark("apply");
  }

  try {
    fs.rmSync(restoreCheckpointPath(seq), { force: true });
  } catch (error) {
    // A leftover checkpoint only causes one extra idempotent attempt later.
  }
  return { seq, packages: Object.keys(target).length, changes: applied.changed, newSeq: applied.generationSeq, resumed: resumedFromCheckpoint };
}

async function handleGenerationCall(op, args) {
  switch (op) {
    case "list": {
      const items = generationFiles().map((name) => {
        try {
          const document = readJson(path.join(GENERATIONS, name));
          return { seq: document.seq, at: document.at, packages: Object.keys(document.packages || {}) };
        } catch (error) {
          return null;
        }
      }).filter(Boolean);
      return { items };
    }
    case "get": {
      const document = readGeneration(Number(args.seq));
      if (!document) {
        throw new StorageError("ERR_PKG_NOT_FOUND", "generation " + args.seq + " does not exist", 20);
      }
      return { seq: document.seq, at: document.at, packages: Object.keys(document.packages || {}) };
    }
    case "create":
      return { seq: writeGeneration(readAllRecords()) };
    case "restore":
      return restoreGeneration(args);
    default:
      throw new StorageError("ERR_UNSUPPORTED", "unknown storage.generation op " + op);
  }
}

function parseFlags(argv) {
  const flags = {};
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (!arg.startsWith("--")) {
      continue;
    }
    const name = arg.slice(2);
    const next = argv[index + 1];
    if (next !== undefined && !next.startsWith("--")) {
      flags[name] = next;
      index += 1;
    } else {
      flags[name] = true;
    }
  }
  return flags;
}

const STORAGE_EXIT_CODES = { ERR_BAD_ARGS: 2, ERR_PKG_INVALID: 3, ERR_PKG_NOT_FOUND: 20, ERR_UNSUPPORTED: 11, ERR_TIMEOUT: 25, ERR_AUDIT_EXPIRED: 20 };

function exitCodeFor(error) {
  return (error && error.exitCode) || STORAGE_EXIT_CODES[(error && error.code) || ""] || 1;
}

let writeQueue = Promise.resolve();

// storage is a single writer: mutating capability calls must not interleave (gc vs adopt, restore vs install).
function serialize(work) {
  const next = writeQueue.then(work, work);
  writeQueue = next.then(() => undefined, () => undefined);
  return next;
}

function isReadOnlyCall(capability, op, args) {
  if (capability === "storage.record") {
    return op === "get" || op === "list";
  }
  if (capability === "storage.current") {
    return op === "get";
  }
  if (capability === "storage.ledger") {
    return op === "list";
  }
  if (capability === "storage.wire") {
    return op.endsWith(".get") || (op === "apply" && Boolean(args && args.dryRun === true));
  }
  if (capability === "storage.generation") {
    return op === "list" || op === "get";
  }
  return false;
}

async function handleCommand(message) {
  const id = message.id;
  const argv = (message.data && message.data.argv) || [];
  const command = (message.data && message.data.cmd) || argv[0] || "";
  const flags = parseFlags(argv);
  let seq = 0;
  const done = (payload) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) });
  const fail = (error) => send({ v: 1, kind: "evt", id, seq: ++seq, data: { type: "done", ok: false, exitCode: exitCodeFor(error), error: { code: error.code || "ERR_INTERNAL", message: error.message || String(error) } } });

  try {
    if (command === "apply") {
      const result = await serialize(() => applyRecords({ task: id, dryRun: flags["dry-run"] === true }));
      done({ summary: "applied " + result.packages + " packages (" + result.changed + " changes)", metrics: result });
      return;
    }
    if (command === "clean") {
      const result = await serialize(() => cleanWiring({ task: id, dryRun: flags["dry-run"] === true }));
      done({ summary: "cleaned " + result.changed + " wiring changes", metrics: result });
      return;
    }
    if (command === "gc") {
      const result = await serialize(() => gc({
        dryRun: flags["dry-run"] === true,
        keep: flags.keep,
        blobs: flags.blobs === true,
        force: flags.force === true,
      }));
      done({
        summary: "gc: " + result.storeRemoved + " store, " + result.stagingRemoved + " staging, " +
          result.generationsRemoved + " generations, " + result.blobsRemoved + " blobs" +
          (result.dryRun ? " (dry-run)" : "") +
          (result.errors.length > 0 ? " (" + result.errors.length + " errors)" : ""),
        metrics: result,
      });
      return;
    }
    fail(new StorageError("ERR_UNKNOWN_COMMAND", "storage has no command " + command));
  } catch (error) {
    fail(error);
  }
}

async function handleWireCall(capability, op, args) {
  switch (op) {
    case "env.set":
      return wireSetEnv(args);
    case "env.delete":
      return wireDeleteEnv(args);
    case "env.get":
      return { value: readUserEnv(String(args.name || "")) };
    case "path.add":
      return wireAddPath(args);
    case "path.remove":
      return wireRemovePath(args);
    case "path.get":
      return { value: readUserPath() };
    case "registry.set":
      return wireSetRegistry(args);
    case "registry.delete":
      return wireDeleteRegistry(args);
    case "registry.get": {
      const entry = { hive: args.hive || "HKCU", key: String(args.key || ""), name: args.name || "" };
      return { value: readRegistry(entry) };
    }
    case "launcher.write":
      return wireWriteLauncher(args);
    case "launcher.remove":
      return wireRemoveLauncher(args);
    case "link.create":
      return wireLinkCreate(args);
    case "link.remove":
      return wireLinkRemove(args);
    case "persist.link":
      return wirePersistLink(args);
    case "persist.unlink":
      return persistUnlink(args);
    case "stableRoot":
      return ensureStableRoot(args);
    case "link.get":
      return { source: readLinkTarget(String(args.target || "")) };
    // Read-only probes used by pd doctor: the machine's user env vars plus a package's home
    // junction. Tests run in fake mode, where these read state\wire\fake.json instead.
    case "env.list":
      if (FAKE) {
        return { env: Object.assign({}, readFake().env || {}) };
      }
      {
        const document = {};
        try {
          const raw = powershell("[Environment]::GetEnvironmentVariables('User') | ConvertTo-Json -Compress");
          const parsed = raw ? JSON.parse(raw) : {};
          for (const [name, value] of Object.entries(parsed || {})) {
            if (value !== null && value !== undefined) {
              document[name] = String(value);
            }
          }
        } catch (error) {
          return { env: document };
        }
        return { env: document };
      }
    case "home.get":
      {
        const pkg = packageId(args.pkg, "home package id");
        return {
          machinePath: homeMachinePath(pkg),
          stickPath: homeStickPath(pkg),
          source: readLinkTarget(homeMachinePath(pkg)),
        };
      }
    case "copy.create":
      return wireCopyCreate(args);
    case "copy.remove":
      return wireCopyRemove(args);
    case "home.ensure":
      return ensureHome(args);
    case "apply":
      return applyRecords(args);
    case "clean":
      return cleanWiring(args);
    case "generation.list":
    case "generation.get":
    case "generation.create":
    case "generation.restore":
      return handleGenerationCall(op.slice("generation.".length), args);
    default:
      throw new StorageError("ERR_UNSUPPORTED", "unknown storage.wire op " + op);
  }
}

function handleGrant(message) {
  const taskId = safeTask(message.id || "t0");
  const owner = String((message.data && message.data.from) || "");
  const ttlSeconds = parseTtlSeconds(message.data && message.data.ttl);
  const hint = String((message.data && message.data.hint) || "work").replace(/[^a-z0-9._-]/gi, "_").slice(0, 24);
  grantCounter += 1;
  const dir = path.join(STAGING, taskId + "-" + hint + "-" + grantCounter);
  fs.mkdirSync(dir, { recursive: true });
  const expiresAt = new Date(Date.now() + ttlSeconds * 1000).toISOString();
  const grant = {
    id: "g" + grantCounter,
    kind: "dir",
    path: dir,
    mode: "rw",
    owner,
    expiresAt,
  };
  grants.set(path.basename(dir), { grant, owner, expiresAt: Date.parse(expiresAt) });
  send({ v: 1, kind: "ctl", id: taskId, data: { action: "grant.reply", grant } });
}

// Elevated script execution: only reachable for a package whose manifest opted into
// script.elevated; the helper request is SHA-256 bound and args never pass through a shell.
async function elevateRun(args) {
  const command = String(args.command || "");
  const cwd = String(args.cwd || "");
  if (!path.isAbsolute(command) || !path.isAbsolute(cwd)) {
    throw new StorageError("ERR_BAD_ARGS", "storage.elevate run needs an absolute command and cwd");
  }
  if (!fs.existsSync(command)) {
    throw new StorageError("ERR_BAD_ARGS", "storage.elevate command does not exist: " + command);
  }
  if (!fs.existsSync(cwd)) {
    throw new StorageError("ERR_BAD_ARGS", "storage.elevate cwd does not exist: " + cwd);
  }
  const results = runElevated([{
    op: "process.run",
    command: command,
    cwd: cwd,
    args: Array.isArray(args.args) ? args.args.map((item) => String(item)) : [],
    timeoutMs: Number(args.timeoutMs) || 600000,
    env: args.env && typeof args.env === "object" && !Array.isArray(args.env) ? args.env : {},
  }]);
  const entry = (results || [])[0] || {};
  if (entry.ok !== true) {
    const error = entry.error || {};
    throw new StorageError(error.code || "ERR_IO", error.message || "elevated command failed");
  }
  return entry.before || {};
}

async function handleCall(message) {
  const id = message.id;
  const capability = message.data && message.data.cap;
  const op = (message.data && message.data.op) || "";
  const args = (message.data && message.data.args) || {};

  const dispatch = async () => {
    const caller = (message.data && message.data.from) || "";
    if (capability === "storage.adopt" && op === "commit") {
      return adopt(args, caller);
    }
    if (capability === "storage.adopt" && op === "abort") {
      return abort(args, caller);
    }
    if (capability === "storage.record" && op === "get") {
      return recordGet(args);
    }
    if (capability === "storage.record" && op === "list") {
      return recordList();
    }
    if (capability === "storage.record" && op === "write") {
      return recordWrite(args);
    }
    if (capability === "storage.record" && op === "delete") {
      return recordDelete(args);
    }
    if (capability === "storage.current" && op === "set") {
      return currentSet(args);
    }
    if (capability === "storage.current" && op === "get") {
      return currentGet(args);
    }
    if (capability === "storage.current" && op === "clear") {
      return currentClear(args);
    }
    if (capability === "storage.uninstall" && op === "run") {
      return uninstall(args);
    }
    if (capability === "storage.gc" && op === "run") {
      return gc(args);
    }
    if (capability === "storage.ledger") {
      return ledgerOp(op, args);
    }
    if (capability === "storage.elevate") {
      if (op === "run") {
        return elevateRun(args);
      }
      throw new StorageError("ERR_UNSUPPORTED", "unknown storage.elevate op " + op);
    }
    if (capability === "storage.wire") {
      return handleWireCall(capability, op, args);
    }
    if (capability === "storage.generation") {
      return handleGenerationCall(op, args);
    }
    if (capability === "storage.home") {
      if (op === "ensure") {
        return ensureHome(args);
      }
      if (op === "restore") {
        return restoreHome(args);
      }
    }
    throw new StorageError("ERR_UNSUPPORTED", "unknown capability " + capability + " op " + op);
  };

  try {
    const result = isReadOnlyCall(capability, op, args) ? await dispatch() : await serialize(dispatch);
    send({ v: 1, kind: "call.ok", id, data: { result } });
  } catch (error) {
    send({ v: 1, kind: "call.err", id, data: { error: { code: error.code || "ERR_IO", message: error.message || String(error) } } });
  }
}

const link = connect({
  onMessage(message, outbound) {
    send = outbound;
    if (message.kind === "hello.ok") {
      MACHINE_ID = String((message.data && message.data.machineId) ||
        process.env.COMPUTERNAME || process.env.HOSTNAME || "");
      ready(send, [
        { name: "storage.grant", version: 1, mutates: true },
        { name: "storage.adopt", version: 1, mutates: true },
        { name: "storage.record", version: 1, mutates: true },
        { name: "storage.current", version: 1, mutates: true },
        { name: "storage.gc", version: 1, mutates: true },
        { name: "storage.ledger", version: 1, mutates: true },
        { name: "storage.wire", version: 1, mutates: true },
        { name: "storage.elevate", version: 1, mutates: true },
        { name: "storage.generation", version: 1, mutates: true },
        { name: "storage.home", version: 1, mutates: true },
        { name: "storage.uninstall", version: 1, mutates: true },
      ]);
      return;
    }

    if (message.kind === "ctl") {
      const action = message.data && message.data.action;
      if (action === "grant.request") {
        handleGrant(message);
      }
      return;
    }

    if (message.kind === "call.ok" || message.kind === "call.err") {
      const entry = pendingCalls.get(message.id);
      if (entry) {
        clearTimeout(entry.timer);
        pendingCalls.delete(message.id);
        if (message.kind === "call.ok") {
          entry.resolve((message.data && message.data.result) || {});
        } else {
          const error = (message.data && message.data.error) || {};
          entry.reject(new StorageError(error.code || "ERR_IO", error.message || "call failed"));
        }
      }
      return;
    }

    if (message.kind === "call") {
      handleCall(message);
    }

    if (message.kind === "ext.invoke") {
      handleCommand(message);
    }
  },
});

send = link.send;
