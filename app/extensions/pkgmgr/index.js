// Package manager: owns the install flow. Storage still executes every disk effect.
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { connect, ready } = require("./protocol");
const { createSourceManager, compareVersions } = require("./sources");
const { runScript, runCapturedCommand, allowedByPolicy } = require("./scripts");

const ROOT = process.env.PD_ROOT || ".";
const CACHE = path.join(ROOT, "cache", "blobs");
const pendingCalls = new Map();
const pendingGrants = new Map();
let callCounter = 0;
let send = () => {};

function exitForCode(code) {
  switch (code) {
    case "ERR_BAD_ARGS":
    case "ERR_UNKNOWN_COMMAND":
      return 2;
    case "ERR_PKG_INVALID":
    case "ERR_INDEX_INVALID":
    case "ERR_HASH":
      return 3;
    case "ERR_ENV":
    case "ERR_PROTOCOL":
      return 4;
    case "ERR_ELEVATION":
      return 8;
    case "ERR_BUSY":
      return 10;
    case "ERR_UNSUPPORTED":
    case "ERR_PKG_PLATFORM":
      return 11;
    case "ERR_ASSERT":
      return 12;
    case "ERR_SOURCE_UNREACHABLE":
    case "ERR_NET":
      return 15;
    case "ERR_PKG_NOT_FOUND":
    case "ERR_EXT_MISSING":
    case "ERR_AUDIT_EXPIRED":
      return 20;
    case "ERR_PKG_AMBIGUOUS":
      return 21;
    case "ERR_PKG_YANKED":
      return 22;
    case "ERR_OFFLINE_MISS":
      return 23;
    case "ERR_DEP_UNSATISFIED":
      return 24;
    case "ERR_TIMEOUT":
    case "ERR_EXT_TIMEOUT":
      return 25;
    case "ERR_CANCELLED":
      return 130;
    default:
      return 1;
  }
}

function PkgError(code, message, exitCode) {
  this.code = code;
  this.message = message;
  this.exitCode = exitCode || exitForCode(code);
}

function callCapability(capability, op, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    callCounter += 1;
    const id = "p" + callCounter;
    const timer = setTimeout(() => {
      pendingCalls.delete(id);
      reject(new PkgError("ERR_TIMEOUT", "call timeout: " + capability + "/" + op, 25));
    }, timeoutMs || 1800000);
    pendingCalls.set(id, { resolve, reject, timer });
    send({ v: 1, kind: "call", id, data: { cap: capability, op, args } });
  });
}

// Client-side execution (spec 4.7 exec channel): the shell owns the terminal, so pkgmgr asks it
// through the kernel to spawn a program with inherited stdio and report the exit code.
const pendingExecs = new Map();
let execCounter = 0;

function cmdQuotePart(value) {
  return "\"" + String(value).replace(/%/g, "%%").replace(/"/g, "\"\"") + "\"";
}

function buildCmdLine(entry, args) {
  const parts = [entry].concat(args || []).map((part) => cmdQuotePart(part));
  return "\"" + parts.join(" ") + "\"";
}

function execOnClient(taskId, payload, timeoutMs) {
  return new Promise((resolve, reject) => {
    execCounter += 1;
    const execId = "e" + execCounter;
    const key = String(taskId) + ":" + execId;
    const timer = setTimeout(() => {
      pendingExecs.delete(key);
      reject(new PkgError("ERR_TIMEOUT", "the client did not answer the exec request", 25));
    }, timeoutMs || 86400000);
    pendingExecs.set(key, { resolve, reject, timer });
    send({ v: 1, kind: "evt", id: taskId, seq: 1, data: Object.assign({ type: "exec", execId: execId }, payload) });
  });
}

const cancelledTasks = new Set();

function isCancelled(id) {
  return cancelledTasks.has(String(id));
}

function throwIfCancelled(id) {
  if (isCancelled(id)) {
    throw new PkgError("ERR_CANCELLED", "cancelled by the client", 130);
  }
}

// rollback.run reports partial failures as a normal result; every caller must look at it instead of
// assuming a resolved promise means the machine was fully restored.
async function rollbackTask(taskId, event) {
  try {
    const result = (await callCapability("rollback.run", "run", { task: taskId })) || {};
    const failed = Number(result.failed || 0);
    if (failed > 0) {
      const detail = (result.failures || []).map((item) => item.op + ": " + item.message).join("; ");
      if (event) {
        event("log", { level: "warn", msg: "rollback of " + taskId + " left " + failed + " step(s) unapplied: " + detail });
      }
      return { failed: failed, failures: result.failures || [] };
    }
    return { failed: 0, failures: [] };
  } catch (error) {
    if (event) {
      event("log", { level: "warn", msg: "rollback of " + taskId + " failed: " + (error.message || String(error)) });
    }
    return { failed: 1, failures: [{ op: "rollback.run", message: error.message || String(error) }] };
  }
}

// Grants are a ctl exchange (spec 5.4), not a capability call.
function requestGrant(taskId, hint) {
  return new Promise((resolve, reject) => {
    const key = String(taskId);
    const timer = setTimeout(() => {
      pendingGrants.delete(key);
      reject(new PkgError("ERR_EXT_TIMEOUT", "storage did not answer the grant request", 25));
    }, 30000);
    pendingGrants.set(key, { resolve, reject, timer });
    send({ v: 1, kind: "ctl", id: taskId, data: { action: "grant.request", kind: "dir", hint, mode: "rw", ttl: "30m" } });
  });
}

let sourceManager = null;

function readSettings() {
  try {
    return JSON.parse(fs.readFileSync(path.join(ROOT, "config", "settings.json"), "utf8"));
  } catch (error) {
    return {};
  }
}

function sources() {
  if (sourceManager === null) {
    const settings = readSettings();
    sourceManager = createSourceManager({
      root: ROOT,
      call: (capability, op, args) => callCapability(capability, op, args),
      settings: {
        defaultChannel: (settings.install && settings.install.defaultChannel) || "stable",
      },
    });
  }

  return sourceManager;
}

const VALUE_FLAGS = new Set(["as", "from-dir", "sha256", "source", "to", "channel", "ttl", "kind", "priority", "name", "out", "limit"]);

function parseFlags(argv) {
  const flags = {};
  const positional = [];
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--") {
      positional.push(...argv.slice(i + 1));
      break;
    }
    if (arg.startsWith("--")) {
      const eq = arg.indexOf("=");
      if (eq > 2) {
        flags[arg.slice(2, eq)] = arg.slice(eq + 1);
        continue;
      }
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith("--")) {
        flags[arg.slice(2)] = next;
        i += 1;
      } else {
        if (VALUE_FLAGS.has(arg.slice(2))) {
          throw new PkgError("ERR_BAD_ARGS", "--" + arg.slice(2) + " needs a value", 2);
        }
        flags[arg.slice(2)] = true;
      }
    } else {
      positional.push(arg);
    }
  }
  return { flags, positional };
}

const PACKAGE_ID = /^[a-z0-9][a-z0-9-]{0,63}$/;
const VERSION = /^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$/;

function validPackageId(value) {
  const text = String(value === undefined || value === null ? "" : value);
  if (!PACKAGE_ID.test(text)) {
    throw new PkgError("ERR_PKG_INVALID", "invalid package id: " + JSON.stringify(text), 3);
  }
  return text;
}

function validVersion(value) {
  const text = String(value === undefined || value === null ? "" : value);
  if (!VERSION.test(text)) {
    throw new PkgError("ERR_PKG_INVALID", "invalid version: " + JSON.stringify(text), 3);
  }
  return text;
}

// manifest.persist: install-dir directories whose data lives on the stick (<root>/home/<pkg>/persist).
function normalizePersist(value) {
  if (value === undefined || value === null) {
    return [];
  }
  if (!Array.isArray(value)) {
    throw new PkgError("ERR_PKG_INVALID", "persist must be an array of relative paths", 3);
  }
  const result = [];
  for (const item of value) {
    const rel = String(item === undefined || item === null ? "" : item).replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
    const segments = rel.split("/");
    const badSegment = segments.some((segment) =>
      !segment || segment === "." || segment === ".." || segment.includes(":") ||
      /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(segment));
    if (!rel || badSegment || /^[A-Za-z]:/.test(rel) || rel.includes("\u0000") || segments.length > 16) {
      throw new PkgError("ERR_PKG_INVALID", "bad persist path: " + String(item), 3);
    }
    result.push(rel);
  }
  // Overlapping entries (data + data/sub) would nest junctions; reject instead of guessing.
  const sorted = result.slice().sort();
  for (let index = 1; index < sorted.length; index += 1) {
    if (sorted[index] === sorted[index - 1] || sorted[index].startsWith(sorted[index - 1] + "/")) {
      throw new PkgError("ERR_PKG_INVALID", "persist paths overlap: " + sorted[index - 1] + " / " + sorted[index], 3);
    }
  }
  return result;
}

function readManifest(file) {
  let text;
  try {
    text = fs.readFileSync(file, "utf8");
  } catch (error) {
    throw new PkgError("ERR_PKG_INVALID", "cannot read package.json: " + error.message, 3);
  }

  let manifest;
  try {
    manifest = JSON.parse(text);
  } catch (error) {
    throw new PkgError("ERR_PKG_INVALID", "package.json is not valid JSON: " + error.message, 3);
  }

  if (!manifest || !manifest.id || !manifest.version) {
    throw new PkgError("ERR_PKG_INVALID", "package.json needs id and version", 3);
  }
  validPackageId(manifest.id);
  validVersion(manifest.version);
  return manifest;
}

function assertFilesExist(root, asserts) {
  const failures = [];
  for (const assertion of asserts || []) {
    const kind = assertion && assertion.kind;
    if (!kind || String(kind).startsWith("x-")) {
      continue;
    }

    const relative = String(assertion.path || "");
    if (path.isAbsolute(relative) || relative.split(/[\\/]/).includes("..")) {
      failures.push("assertion path escapes the package root: " + relative);
      continue;
    }
    const target = path.join(root, relative);
    const exists = fs.existsSync(target);
    if (kind === "exists") {
      if (!exists) {
        failures.push("missing " + assertion.path);
      }
      continue;
    }

    if (kind === "missing") {
      if (exists) {
        failures.push("unexpected " + assertion.path);
      }
      continue;
    }

    if (kind === "json") {
      if (!exists) {
        failures.push("missing " + assertion.path);
        continue;
      }
      try {
        const document = JSON.parse(fs.readFileSync(target, "utf8"));
        const segments = String(assertion.pointer || "").split("/").filter(Boolean);
        let current = document;
        for (const segment of segments) {
          current = current == null ? undefined : current[segment];
        }
        if (assertion.equals !== undefined && JSON.stringify(current) !== JSON.stringify(assertion.equals)) {
          failures.push("json " + assertion.path + " mismatch at " + assertion.pointer);
        }
      } catch (error) {
        failures.push("json " + assertion.path + " is not valid JSON");
      }
      continue;
    }

    if (kind === "fileHash") {
      if (!exists) {
        failures.push("missing " + assertion.path);
        continue;
      }
      const actual = crypto.createHash("sha256").update(fs.readFileSync(target)).digest("hex");
      if (String(assertion.sha256 || "").toLowerCase() !== actual) {
        failures.push("hash mismatch for " + assertion.path);
      }
      continue;
    }

    failures.push("unsupported assertion kind: " + kind);
  }

  if (failures.length > 0) {
    throw new PkgError("ERR_ASSERT", "file assertions failed: " + failures.join("; "), 12);
  }
}

function parseAssertDuration(value, fallbackMs) {
  const text = String(value === undefined || value === null ? "" : value).trim();
  const match = /^(\d+(?:\.\d+)?)\s*(ms|s|m)?$/i.exec(text);
  if (!match) {
    return fallbackMs;
  }
  const amount = Number(match[1]);
  const unit = (match[2] || "s").toLowerCase();
  return Math.round(amount * (unit === "ms" ? 1 : unit === "m" ? 60000 : 1000));
}

function assertionEnv(pathPrefix) {
  const environment = {};
  for (const name of ["SystemRoot", "windir", "TEMP", "TMP", "PATH", "APPDATA", "LOCALAPPDATA", "USERPROFILE"]) {
    if (process.env[name]) {
      environment[name] = process.env[name];
    }
  }
  environment.PATH = pathPrefix + ";" + (environment.PATH || "");
  return environment;
}

// Evaluates package asserts (file kinds and command). Returns per-assert results so install-time
// validation and pd selftest share the same checks.
async function evaluateAsserts(root, asserts, options) {
  const results = [];
  const opts = options || {};
  for (const assertion of asserts || []) {
    if (!assertion || typeof assertion !== "object") {
      continue;
    }
    const kind = String(assertion.kind || "");
    if (!kind || kind.startsWith("x-")) {
      continue;
    }
    const label = String(assertion.name || (kind === "command" ? assertion.run : assertion.path) || kind);

    if (kind === "command") {
      const run = String(assertion.run || "").trim();
      if (!run) {
        results.push({ name: label, ok: false, detail: "command assertion needs run" });
        continue;
      }
      if (opts.allowCommands === false) {
        results.push({ name: label, ok: true, detail: "command assertion skipped: scripts are disabled by policy" });
        continue;
      }
      const timeoutMs = parseAssertDuration(assertion.timeout, 20000);
      let result;
      try {
        result = await runCapturedCommand({
          call: callCapability,
          command: process.env.ComSpec || "cmd.exe",
          args: ["/d", "/s", "/c", run],
          cwd: opts.cwd || root,
          env: assertionEnv(opts.pathPrefix || root),
          timeoutMs: timeoutMs,
        });
      } catch (error) {
        results.push({ name: label, ok: false, detail: "could not run: " + (error.message || String(error)) });
        continue;
      }
      if (result.exitCode !== 0) {
        results.push({ name: label, ok: false, detail: "exit " + result.exitCode + ": " + String(result.output || "").slice(-200) });
        continue;
      }
      const expect = assertion.expect === undefined ? null : String(assertion.expect);
      if (expect === null) {
        results.push({ name: label, ok: true, detail: "exit 0" });
        continue;
      }
      const mode = String(assertion.match || "contains").toLowerCase();
      const output = String(result.output || "").trim();
      let ok = false;
      if (mode === "equals") {
        ok = output === expect;
      } else if (mode === "regex") {
        try { ok = new RegExp(expect).test(output); } catch (error) { ok = false; }
      } else {
        ok = output.includes(expect);
      }
      results.push({ name: label, ok: ok, detail: ok ? "matched" : "output mismatch (" + mode + ")" });
      continue;
    }

    const relative = String(assertion.path || "");
    if (path.isAbsolute(relative) || relative.split(/[\\/]/).includes("..")) {
      results.push({ name: label, ok: false, detail: "path escapes the package root" });
      continue;
    }
    const target = path.join(root, relative);
    const exists = fs.existsSync(target);
    if (kind === "exists") {
      results.push({ name: label, ok: exists, detail: exists ? "present" : "missing" });
      continue;
    }
    if (kind === "missing") {
      results.push({ name: label, ok: !exists, detail: exists ? "unexpected" : "absent" });
      continue;
    }
    if (kind === "json") {
      if (!exists) {
        results.push({ name: label, ok: false, detail: "missing" });
        continue;
      }
      try {
        const document = JSON.parse(fs.readFileSync(target, "utf8"));
        const segments = String(assertion.pointer || "").split("/").filter(Boolean);
        let current = document;
        for (const segment of segments) {
          current = current == null ? undefined : current[segment];
        }
        const ok = assertion.equals === undefined || JSON.stringify(current) === JSON.stringify(assertion.equals);
        results.push({ name: label, ok: ok, detail: ok ? "matched" : "json value mismatch" });
      } catch (error) {
        results.push({ name: label, ok: false, detail: "not valid JSON" });
      }
      continue;
    }
    if (kind === "fileHash") {
      if (!exists) {
        results.push({ name: label, ok: false, detail: "missing" });
        continue;
      }
      const actual = crypto.createHash("sha256").update(fs.readFileSync(target)).digest("hex");
      const ok = String(assertion.sha256 || "").toLowerCase() === actual;
      results.push({ name: label, ok: ok, detail: ok ? "hash ok" : "hash mismatch" });
      continue;
    }
    results.push({ name: label, ok: false, detail: "unsupported assertion kind: " + kind });
  }
  return results;
}

async function assertPackage(root, asserts, options) {
  const results = await evaluateAsserts(root, asserts, options);
  const failures = results.filter((item) => !item.ok).map((item) => item.name + ": " + item.detail);
  if (failures.length > 0) {
    throw new PkgError("ERR_ASSERT", "package assertions failed: " + failures.join("; "), 12);
  }
}

function sanitizeFilePart(value) {
  return String(value || "untitled").replace(/[^A-Za-z0-9._-]/g, "_").slice(0, 64) || "untitled";
}

// Converts a downloaded scoop installer (zip or single exe) into a staging directory
// shaped like a package: { package.json, files/... }. The rest of install() then runs
// the ordinary dir flow (grant copy -> manifest -> script -> adopt).
async function prepareScoopStaging({ resolved, blob, event }) {
  const scoop = (resolved.entry && resolved.entry.scoop) || {};
  const installerKind = scoop.kind === "exe"
    ? "exe"
    : (["zip", "7z", "msi"].includes(scoop.kind) ? scoop.kind : "zip");
  // Stage inside the stick staging directory: unpack.extract refuses anywhere else.
  const staging = path.join(ROOT, "staging", "scoop-" + sanitizeFilePart(resolved.id) + "-" + sanitizeFilePart(resolved.version) + "-" + process.pid);
  fs.rmSync(staging, { recursive: true, force: true });
  fs.mkdirSync(path.join(staging, "files"), { recursive: true });
  if (installerKind !== "exe") {
    event("step", { stepId: "fetch", title: "unpack scoop archive", state: "running" });
    await callCapability("unpack.extract", "extract", {
      archive: blob.path,
      dest: path.join(staging, "files"),
      kind: installerKind,
      // scoop's extract_dir is already applied to the launch/path entries by the converter,
      // so staging must keep the archive's original layout.
    });
    event("step", { stepId: "fetch", title: "unpack scoop archive", state: "done" });
  } else {
    const exeFile = String(scoop.exeFile || (resolved.id + ".exe")).split(/[\\/]/).filter(Boolean).pop() || (resolved.id + ".exe");
    fs.mkdirSync(path.join(staging, "files", "bin"), { recursive: true });
    fs.copyFileSync(blob.path, path.join(staging, "files", "bin", exeFile));
    // Single-file exes land in bin/; manifest bin paths are package-root relative,
    // so qualify bare names to match the staged layout.
    for (const item of (scoop.launch || [])) {
      if (item && typeof item.exe === "string" && !item.exe.includes("/")) {
        item.exe = "bin/" + item.exe;
      }
    }
    // The downloaded file may have a different name than the launch target (scoop renames it);
    // place it at every expected path as well.
    for (const item of (scoop.launch || [])) {
      const rel = String((item && item.exe) || "").replace(/\\/g, "/");
      if (!rel || rel.split("/").includes("..")) {
        continue;
      }
      const expectedPath = path.join(staging, "files", ...rel.split("/"));
      if (!fs.existsSync(expectedPath)) {
        fs.mkdirSync(path.dirname(expectedPath), { recursive: true });
        fs.copyFileSync(blob.path, expectedPath);
      }
    }
  }
  // Bin names drift between scoop versions; drop launchers whose target is missing
  // with a warning instead of failing the whole install.
  const launch = [];
  for (const item of (scoop.launch || [])) {
    const exe = String((item && item.exe) || "").replace(/\\/g, "/");
    const name = String((item && item.name) || "");
    if (!exe || !name || exe.split("/").includes("..")) {
      continue;
    }
    if (!fs.existsSync(path.join(staging, "files", ...exe.split("/")))) {
      event("log", { level: "warn", msg: "scoop launcher target is missing, skipped: " + exe });
      continue;
    }
    launch.push({ name: name, exe: exe });
  }
  if (launch.length === 0) {
    throw new PkgError("ERR_PKG_INVALID", "scoop package has no usable launcher: " + resolved.id + "@" + resolved.version, 3);
  }
  const manifest = {
    schema: 1,
    id: resolved.id,
    version: resolved.version,
    platform: "win-x64",
    launch: launch,
    path: scoop.path && scoop.path.length > 0 ? scoop.path : ["bin"],
    env: scoop.env || {},
    persist: Array.isArray(scoop.persist) ? scoop.persist : [],
    requires: (resolved.entry && resolved.entry.requires) || [],
    meta: { scoop: { manifest: scoop.manifest || null, kind: installerKind } },
  };
  fs.writeFileSync(path.join(staging, "package.json"), JSON.stringify(manifest, null, 2) + "\n");
  return staging;
}

function depRef(raw, pkgId) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw) || typeof raw.id !== "string") {
    throw new PkgError("ERR_PKG_INVALID", "bad requires entry in " + pkgId + ": " + JSON.stringify(raw), 3);
  }
  const id = raw.id.trim().toLowerCase();
  if (!/^[a-z][a-z0-9-]{0,63}$/.test(id)) {
    throw new PkgError("ERR_PKG_INVALID", "bad requires id in " + pkgId + ": " + raw.id, 3);
  }
  const range = raw.range === undefined || raw.range === null ? "" : String(raw.range).trim();
  return { id: id, range: range, ref: range ? id + "@" + range : id };
}

async function installedRecord(pkg) {
  try {
    return await callCapability("storage.record", "get", { pkg: pkg });
  } catch (error) {
    if (error && error.code === "ERR_PKG_NOT_FOUND") {
      return null;
    }
    throw error;
  }
}

// Plans dependency installs from the package manifest. Pure checks only (no writes),
// so the dry-run path can display the plan and bail out before anything changes.
async function listInstalled() {
  try {
    const result = await callCapability("storage.record", "list", {});
    return (result && result.items) || [];
  } catch (error) {
    return [];
  }
}

async function planDependencies({ manifest, pkgId, event, flags, manager }) {
  const requires = Array.isArray(manifest.requires) ? manifest.requires : [];
  if (flags["no-deps"] === true) {
    if (requires.length > 0) {
      event("log", { level: "info", msg: "skipping " + requires.length + " dependencies (--no-deps)" });
    }
    return [];
  }
  const plan = [];
  for (const raw of requires) {
    const dep = depRef(raw, pkgId);
    let constraint;
    try {
      constraint = manager.parseConstraint(dep.range);
    } catch (error) {
      throw new PkgError("ERR_DEP_UNSATISFIED", "bad version range for " + dep.id + " required by " + pkgId + ": " + dep.range, 24);
    }
    const installed = await installedRecord(dep.id);
    if (installed && manager.satisfies(String(installed.version), constraint)) {
      event("log", { level: "info", msg: "dependency satisfied: " + dep.id + " " + installed.version });
      plan.push({ id: dep.id, range: dep.range, ref: dep.ref, status: "satisfied", version: String(installed.version) });
      continue;
    }
    if (installed) {
      throw new PkgError("ERR_DEP_UNSATISFIED", "dependency conflict: " + dep.id + " " + installed.version + " is installed but " + pkgId + " needs " + (dep.range || "another version") + "; upgrade it explicitly first", 24);
    }
    const providedBy = (await listInstalled()).filter((item) =>
      Array.isArray(item.provides) && item.provides.includes(dep.id) &&
      manager.satisfies(String(item.version), constraint));
    if (providedBy.length > 0) {
      event("log", { level: "info", msg: "dependency satisfied: " + dep.id + " provided by " + providedBy[0].id + " " + providedBy[0].version });
      plan.push({ id: dep.id, range: dep.range, ref: dep.ref, status: "satisfied", version: String(providedBy[0].version), provider: providedBy[0].id });
      continue;
    }
    plan.push({ id: dep.id, range: dep.range, ref: dep.ref, status: "install" });
  }
  return plan;
}

// Installs the planned dependencies topologically before the dependent itself.
// Deps installed by an aborted chain stay installed (each keeps its own transaction).
async function installDependencyPlan({ plan, chain, taskId, id, ctx, flags, event }) {
  let counter = 0;
  for (const dep of plan) {
    if (dep.status !== "install") {
      continue;
    }
    if (chain.some((link) => link.split("@")[0] === dep.id)) {
      throw new PkgError("ERR_DEP_UNSATISFIED", "circular dependency involving " + dep.id + " (chain: " + chain.join(" -> ") + ")", 24);
    }
    if (chain.length >= 25) {
      throw new PkgError("ERR_DEP_UNSATISFIED", "dependency chain too deep (chain: " + chain.join(" -> ") + ")", 24);
    }
    counter += 1;
    const childId = id + "-dep-" + counter;
    const argv = [dep.ref];
    for (const name of ["offline", "refresh", "channel", "prerelease", "allow-scripts", "no-scripts", "force"]) {
      const value = flags[name];
      if (value === true) {
        argv.push("--" + name);
      } else if (typeof value === "string" && value.length > 0) {
        argv.push("--" + name, value);
      }
    }
    event("step", { stepId: "dep-" + dep.id, title: "dependency " + dep.ref, state: "running" });
    try {
      // Like upgrade: the child reuses the outer wire id (the only id the kernel
      // knows for grants/cancel), while taskId stays the audit grouping.
      await runInstall(
        { id: id, data: { argv: argv, ctx: ctx, cmd: "install" } },
        taskId,
        (type, data) => {
          if (type === "step") {
            event("step", { stepId: "dep-" + dep.id + "-" + (data.stepId || "step"), title: data.title, state: data.state });
            return;
          }
          if (type === "kv" || type === "table") {
            return;
          }
          event(type, data);
        },
        chain,
      );
      event("step", { stepId: "dep-" + dep.id, title: "dependency " + dep.ref, state: "done" });
    } catch (error) {
      const code = (error && error.code) || "ERR_INTERNAL";
      const exitCode = code === "ERR_DEP_UNSATISFIED" ? 24 : (error && error.exitCode) || 1;
      throw new PkgError(code, "dependency " + dep.ref + " failed: " + ((error && error.message) || String(error)), exitCode);
    }
  }
}

async function install(message, options) {
  const id = message.id;
  const opts = options || {};
  // The audit task can differ from the kernel task: a batch (pd upgrade) must roll back one package
  // without breaking the grant/prompt routing that the kernel still keys by the outer task id.
  const taskId = opts.taskId || id;
  const io = opts.io || commandIo(message);
  const argv = (message.data && message.data.argv) || [];
  const { flags, positional } = parseFlags(argv);
  const event = (type, data) => io.event(type, data);
  const done = (payload) => io.done(payload);
  const fail = (error) => io.fail(error);

  const target = positional[0];
  // Relative paths come from the caller's shell, not from this process (spec 3.5 context snapshot).
  const clientCwd = (message.data && message.data.ctx && message.data.ctx.cwd) || process.cwd();
  const resolveFrom = (value) => (path.isAbsolute(String(value)) ? path.resolve(String(value)) : path.resolve(clientCwd, String(value)));
  if (!target) {
    fail(new PkgError("ERR_BAD_ARGS", "install needs a target: package file, directory or URL", 2));
    return;
  }

  let grantPath = null;
  let scoopStaging = null;
  const replacedOld = [];
  // Declared outside try/catch so the failure path can restore replaced packages with it.
  let depChain = [];
  const dropScoopStaging = () => {
    const staging = scoopStaging;
    scoopStaging = null;
    if (staging) {
      try { fs.rmSync(staging, { recursive: true, force: true }); } catch (error) { /* best effort */ }
    }
  };
  const cleanupGrant = async () => {
    if (!grantPath) {
      return;
    }
    const staging = grantPath;
    grantPath = null;
    try {
      await callCapability("storage.adopt", "abort", { task: taskId, stagingPath: staging });
    } catch (error) {
      event("log", { level: "warn", msg: "grant cleanup failed: " + (error.message || String(error)) });
    }
  };

  try {
    throwIfCancelled(id);
    event("step", { stepId: "resolve", title: "resolve", state: "running" });

    const fromDir = flags["from-dir"];
    let source = String(target);
    if (fromDir && !path.isAbsolute(source)) {
      source = path.join(resolveFrom(String(fromDir)), source);
    }

    let kind;
    let archive = null;
    let dirRoot = null;
    let label = source;
    let resolvedPackage = null;

    if (/^https?:\/\//i.test(source)) {
      if (!flags.sha256) {
        throw new PkgError("ERR_UNSUPPORTED", "installing from a URL needs --sha256 (source index is not implemented yet)", 11);
      }
      if (flags["dry-run"] === true) {
        event("kv", { title: "install plan", items: { url: source, sha256: String(flags.sha256) } });
        done({ summary: "dry-run: would download and install " + source, metrics: { dryRun: true, url: source } });
        return;
      }
      const blob = await callCapability("download.fetch", "fetch", { url: source, sha256: String(flags.sha256), offline: flags.offline === true });
      kind = "archive";
      archive = blob.path;
    } else {
      const full = resolveFrom(source);
      if (fs.existsSync(full) && fs.statSync(full).isDirectory()) {
        kind = "dir";
        dirRoot = full;
      } else if (fs.existsSync(full)) {
        kind = "archive";
        archive = full;
      } else if (String(target).toLowerCase().endsWith(".pdpkg")) {
        throw new PkgError("ERR_PKG_NOT_FOUND", "no such package file: " + full, 20);
      } else {
        event("step", { stepId: "source", title: "resolve source", state: "running" });
        const resolved = await sources().resolve(String(target), flags);
        resolvedPackage = {
          id: resolved.id,
          version: resolved.version,
          source: resolved.source.name,
          stale: resolved.stale === true,
          sha256: resolved.entry.sha256,
        };
        label = resolved.source.name + ":" + resolved.id + "@" + resolved.version;
        event("step", { stepId: "source", title: "resolve source", state: "done" });

        if (flags["dry-run"] === true) {
          const planItems = { package: resolved.id, version: resolved.version, source: resolved.source.name, url: resolved.entry.url };
          const planned = Array.isArray(resolved.entry.requires) ? resolved.entry.requires : [];
          if (flags["no-deps"] === true && planned.length > 0) {
            planItems.dependencies = planned.map((dep) => (dep && dep.id) || "?").join(", ") + " (skipped by --no-deps)";
          } else if (planned.length > 0) {
            planItems.dependencies = planned.map((dep) => (dep && dep.id) + ((dep && dep.range) ? dep.range : "")).join(", ") + " (installed first)";
          }
          event("kv", { title: "install plan", items: planItems });
          done({ summary: "dry-run: would install " + resolved.id + " " + resolved.version + " from " + resolved.source.name, metrics: { dryRun: true, package: resolved.id, version: resolved.version, source: resolved.source.name } });
          return;
        }

        event("step", { stepId: "fetch", title: "download", state: "running" });
        const blob = await sources().materialize(resolved, flags);
        event("step", { stepId: "fetch", title: "download", state: "done" });
        if (resolved.entry && resolved.entry.scoop) {
          scoopStaging = await prepareScoopStaging({ resolved: resolved, blob: blob, event: event });
          kind = "dir";
          dirRoot = scoopStaging;
        } else {
          kind = "archive";
        }
        archive = blob.path;
      }
    }

    throwIfCancelled(id);
    let manifest;
    let pkgRoot = null;
    if (kind === "dir") {
      manifest = readManifest(path.join(dirRoot, "package.json"));
    } else {
      const grant = await requestGrant(id, "pdpkg");
      grantPath = grant.path;
      const extracted = await callCapability("unpack.extract", "extract", { archive, dest: path.join(grant.path, "pkg"), kind: "zip" });
      pkgRoot = extracted.dest;
      manifest = readManifest(path.join(pkgRoot, "package.json"));
    }

    event("step", { stepId: "resolve", title: "resolve", state: "done" });

    const pkgId = validPackageId(flags.as || manifest.id);
    const version = validVersion(manifest.version);
    const manager = sources();
    const depPlan = await planDependencies({ manifest: manifest, pkgId: pkgId, event: event, flags: flags, manager: manager });
    depChain = (opts.chain || []).concat([pkgId + "@" + version]);

    if (resolvedPackage) {
      if (!flags.as && String(manifest.id) !== resolvedPackage.id) {
        throw new PkgError("ERR_PKG_INVALID", "index resolved " + resolvedPackage.id + " but the .pdpkg says " + manifest.id, 3);
      }
      if (String(manifest.version) !== resolvedPackage.version) {
        throw new PkgError("ERR_PKG_INVALID", "index resolved " + resolvedPackage.id + "@" + resolvedPackage.version +
          " but the .pdpkg says " + manifest.version, 3);
      }
    }

    // A local archive with --sha256 must actually match; the flag used to be silently ignored.
    if (kind === "archive" && flags.sha256 && !resolvedPackage) {
      const expected = String(flags.sha256).toLowerCase();
      if (!/^[0-9a-f]{64}$/.test(expected)) {
        throw new PkgError("ERR_BAD_ARGS", "--sha256 must be a 64-character hex digest", 2);
      }
      const actual = crypto.createHash("sha256").update(fs.readFileSync(archive)).digest("hex");
      if (actual !== expected) {
        throw new PkgError("ERR_HASH", "sha256 mismatch for " + archive + ": expected " + expected + ", got " + actual, 3);
      }
    }
    if (manifest.platform && String(manifest.platform).toLowerCase() !== "win-x64") {
      throw new PkgError("ERR_PKG_PLATFORM", "package platform " + manifest.platform + " does not match win-x64", 11);
    }

    if (flags["dry-run"] === true) {
      const items = { package: pkgId, version: version, source: label };
      if (depPlan.length > 0) {
        items.dependencies = depPlan.map((dep) => dep.ref + " (" + dep.status + ")").join(", ");
      }
      event("kv", { title: "install plan", items: items });
      await cleanupGrant();
      dropScoopStaging();
      done({ summary: "dry-run: would install " + pkgId + " " + version, metrics: { dryRun: true } });
      return;
    }

    throwIfCancelled(id);

    // Conflicts are checked before anything is written: a package we replace is removed
    // later on purpose, so it must not count as a conflict here.
    if (flags.force !== true) {
      const installed = await listInstalled();
      const byId = new Set(installed.map((item) => item.id));
      const replaced = new Set(Array.isArray(manifest.replaces) ? manifest.replaces.filter((name) => typeof name === "string") : []);
      const mine = Array.isArray(manifest.conflicts)
        ? manifest.conflicts.filter((name) => typeof name === "string" && !replaced.has(name)) : [];
      const hitMine = mine.find((name) => byId.has(name));
      if (hitMine) {
        throw new PkgError("ERR_DEP_UNSATISFIED", pkgId + " conflicts with installed " + hitMine + "; remove it first or install with --force", 24);
      }
      const hitTheirs = installed.find((item) => !replaced.has(item.id) && (item.conflicts || []).includes(pkgId));
      if (hitTheirs) {
        throw new PkgError("ERR_DEP_UNSATISFIED", "installed " + hitTheirs.id + " conflicts with " + pkgId + "; remove it first or install with --force", 24);
      }
    }

    await installDependencyPlan({ plan: depPlan, chain: depChain, taskId: taskId, id: id, ctx: (message.data && message.data.ctx) || {}, flags: flags, event: event });

    // Packages this one replaces must go first (they cannot coexist); keep their data.
    // On later failure each removed package is reinstalled best-effort below.
    for (const replacedId of (Array.isArray(manifest.replaces) ? manifest.replaces : [])) {
      if (typeof replacedId !== "string" || !/^[a-z][a-z0-9-]{0,63}$/.test(replacedId) || replacedId === pkgId) {
        continue;
      }
      const oldRecord = await installedRecord(replacedId);
      if (!oldRecord) {
        event("log", { level: "info", msg: "replaces " + replacedId + ": not installed, nothing to remove" });
        continue;
      }
      event("step", { stepId: "replace-" + replacedId, title: "replace " + replacedId, state: "running" });
      await callCapability("storage.uninstall", "run", { task: taskId, pkg: replacedId, purgeData: false, skipGeneration: true });
      event("step", { stepId: "replace-" + replacedId, title: "replace " + replacedId, state: "done" });
      replacedOld.push({ id: replacedId, version: String(oldRecord.version) });
    }

    let contentRoot;
    if (kind === "dir") {
      // A user directory is not inside staging, so copy it into a grant before adopting.
      const grant = await requestGrant(id, "dirpkg");
      grantPath = grant.path;
      const copy = path.join(grant.path, "content");
      fs.cpSync(dirRoot, copy, { recursive: true });
      const filesDir = path.join(copy, "files");
      contentRoot = fs.existsSync(filesDir) ? filesDir : copy;
    } else {
      const filesDir = path.join(pkgRoot, "files");
      if (fs.existsSync(filesDir)) {
        contentRoot = filesDir;
      } else if (manifest.source && manifest.source.url) {
        if (!/^https?:\/\//i.test(String(manifest.source.url))) {
          throw new PkgError("ERR_UNSUPPORTED", "package source.url must be http(s)", 11);
        }
        event("step", { stepId: "fetch", title: "fetch", state: "running" });
        const blob = await callCapability("download.fetch", "fetch", {
          url: manifest.source.url,
          mirrors: manifest.source.mirrors || [],
          sha256: manifest.source.sha256,
          offline: flags.offline === true,
        });
        const extracted = await callCapability("unpack.extract", "extract", {
          archive: blob.path,
          dest: path.join(grantPath, "content"),
          kind: (manifest.unpack && manifest.unpack.kind) || "auto",
          strip: (manifest.unpack && manifest.unpack.strip) || 0,
          extractDir: (manifest.unpack && manifest.unpack.extractDir) || "",
        });
        contentRoot = extracted.dest;
        event("step", { stepId: "fetch", title: "fetch", state: "done" });
      } else {
        throw new PkgError("ERR_PKG_INVALID", "package has neither files/ nor source.url", 3);
      }
    }

    const settings = readSettings();
    const fromNetwork = resolvedPackage !== null || /^https?:\/\//i.test(String(target));
    const scriptResult = await runScript({
      manifest: manifest,
      contentRoot: contentRoot,
      scriptRoot: kind === "dir" ? dirRoot : pkgRoot,
      pkgId: pkgId,
      version: version,
      task: taskId,
      flags: flags,
      settings: settings,
      root: ROOT,
      fromNetwork: fromNetwork,
      event: event,
      call: (capability, op, args) => callCapability(capability, op, args),
    });

    event("step", { stepId: "assert", title: "assert", state: "running" });
    // Command assertions execute code: they must obey the same policy as package scripts,
    // otherwise --no-scripts / allowScripts=never could still run arbitrary commands.
    const assertCommandsAllowed = allowedByPolicy(flags, settings, fromNetwork);
    await assertPackage(contentRoot, manifest.asserts, {
      cwd: contentRoot,
      pathPrefix: contentRoot,
      allowCommands: assertCommandsAllowed,
    });
    event("step", { stepId: "assert", title: "assert", state: "done" });

    throwIfCancelled(id);
    const wiring = {
      path: manifest.path || [],
      env: manifest.env || {},
      registry: manifest.registry || [],
      launch: manifest.launch || [],
      data: manifest.data || [],
      persist: normalizePersist(manifest.persist),
    };

    event("step", { stepId: "adopt", title: "adopt", state: "running" });
    const commit = await callCapability("storage.adopt", "commit", {
      task: taskId,
      pkg: pkgId,
      version,
      stagingPath: contentRoot,
      contentHash: (manifest.hashes && manifest.hashes.archive) || (manifest.source && manifest.source.sha256) || undefined,
      origin: scoopStaging || kind !== "dir" ? "archive" : "local",
      wiring,
    });
    event("step", { stepId: "adopt", title: "adopt", state: "done" });

    if (flags.pin === true) {
      // Pinned packages are skipped by "pd upgrade --all"; an explicit upgrade still works.
      const pinned = await callCapability("storage.record", "get", { pkg: pkgId });
      pinned.pinned = true;
      await callCapability("storage.record", "write", { record: pinned });
    }

    // Dependency metadata lives on the record so install/remove checks do not need
    // to re-read the store. Written whenever the manifest carries any of them
    // (including empty lists, so upgrades clear stale values); older records lack them.
    if (Array.isArray(manifest.provides) || Array.isArray(manifest.requires) || Array.isArray(manifest.conflicts) ||
        Array.isArray(manifest.asserts) || Array.isArray(manifest.selftest)) {
      const record = await callCapability("storage.record", "get", { pkg: pkgId });
      record.provides = Array.isArray(manifest.provides)
        ? manifest.provides.filter((name) => typeof name === "string" && /^[a-z][a-z0-9-]{0,63}$/.test(name)) : [];
      record.requires = Array.isArray(manifest.requires)
        ? manifest.requires.filter((dep) => dep && typeof dep.id === "string").map((dep) => ({ id: dep.id, range: typeof dep.range === "string" ? dep.range : "" })) : [];
      record.conflicts = Array.isArray(manifest.conflicts)
        ? manifest.conflicts.filter((name) => typeof name === "string" && /^[a-z][a-z0-9-]{0,63}$/.test(name)) : [];
      // The installed tree may not carry package.json (directory installs adopt files/ only), so
      // keep the assertions on the record: pd selftest must work without re-reading the store.
      record.asserts = Array.isArray(manifest.asserts) ? manifest.asserts : [];
      record.selftest = Array.isArray(manifest.selftest) ? manifest.selftest : [];
      await callCapability("storage.record", "write", { record: record });
    }

    for (const pending of scriptResult.pendingLinks || []) {
      await callCapability("storage.wire", "link.create", {
        task: taskId,
        source: path.join(commit.storePath, pending.relative),
        target: pending.target,
      });
    }

    event("step", { stepId: "wire", title: "wire", state: "running" });
    const current = await callCapability("storage.current", "set", { task: taskId, pkg: pkgId, storePath: commit.storePath });
    const wired = await callCapability("storage.wire", "apply", { task: taskId, only: pkgId });
    event("step", { stepId: "wire", title: "wire", state: "done" });

    try {
      await callCapability("storage.ledger", "commit", { task: taskId });
    } catch (error) {
      event("log", { level: "warn", msg: "ledger commit failed: " + (error.message || String(error)) });
    }
    await cleanupGrant();
    dropScoopStaging();

    done({
      summary: "installed " + pkgId + " " + version,
      metrics: {
        package: pkgId,
        version,
        storePath: commit.storePath,
        treeHash: commit.treeHash,
        link: current.link,
        existed: commit.existed === true,
        wiringChanges: wired.changed,
        source: resolvedPackage ? resolvedPackage.source : null,
        indexStale: resolvedPackage ? resolvedPackage.stale : false,
        task: taskId,
      },
    });
  } catch (error) {
    await rollbackTask(taskId, event);
    await cleanupGrant();
    dropScoopStaging();
    for (const old of replacedOld) {
      try {
        event("log", { level: "warn", msg: "restoring replaced package " + old.id + " " + old.version });
        await runInstall(
          { id: id, data: { argv: [old.id + "@" + old.version], ctx: (message.data && message.data.ctx) || {}, cmd: "install" } },
          taskId,
          (type, data) => { if (type === "log") { event("log", data); } },
          depChain,
        );
      } catch (restoreError) {
        event("log", { level: "error", msg: "could not restore " + old.id + ": " + ((restoreError && restoreError.message) || String(restoreError)) });
      }
    }
    // A cancelled task already received its terminal done(130) from the kernel; sending another one
    // would just be dropped, so stay quiet in that case.
    if (!isCancelled(id)) {
      fail(error);
    }
  }
}

function readJsonSafe(file) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch (error) {
    return null;
  }
}

// Reads the process integrity level (Low/Medium/High/System) from whoami /groups. Best effort:
// an unreadable result only leaves a generic detail, never a false doctor issue.
async function readIntegrityLevel() {
  try {
    const systemRoot = process.env.SystemRoot || process.env.windir || "C:\\Windows";
    const whoami = path.join(systemRoot, "System32", "whoami.exe");
    const result = await runCapturedCommand({
      call: callCapability,
      command: whoami,
      args: ["/groups"],
      cwd: ROOT,
      env: assertionEnv(ROOT),
      timeoutMs: 20000,
    });
    const flat = String(result.output || "").replace(/\u0000/g, "");
    const match = /S-1-16-(\d+)/.exec(flat);
    if (!match) {
      return { label: null };
    }
    const labels = { "4096": "低", "8192": "中", "12288": "高", "16384": "系统" };
    return { label: labels[match[1]] || "未知" };
  } catch (error) {
    return { label: null };
  }
}

function expandWiringValue(record, value, stableRoot) {
  const home = path.join(stableRoot, "home", String(record.id));
  let text = String(value === undefined || value === null ? "" : value)
    .replace(/\{\{ROOT\}\}/g, stableRoot)
    .replace(/\{\{STICK\}\}/g, ROOT)
    .replace(/\{\{HOME\}\}/g, home)
    .replace(/\{\{PERSIST\}\}/g, home)
    .replace(/\{\{ID\}\}/g, String(record.id))
    .replace(/\{\{VERSION\}\}/g, String(record.version || ""));
  for (const [name, directory] of [["APPDATA", process.env.APPDATA], ["LOCALAPPDATA", process.env.LOCALAPPDATA], ["USERPROFILE", process.env.USERPROFILE]]) {
    text = text.split("{{" + name + "}}").join(directory || "");
  }
  return text;
}

function samePathValue(left, right) {
  const a = String(left || "");
  const b = String(right || "");
  return process.platform === "win32" ? a.toLowerCase() === b.toLowerCase() : a === b;
}

// Machine-side wiring vs the records: PATH entries, env vars, registry values and home junctions.
// Read-only (storage.wire *.get/list); pd doctor --fix repairs them with one storage wire apply.
async function collectWiringChecks(records, stableRoot) {
  const rows = [];
  let userPath = null;
  let pathError = "";
  try {
    userPath = (await callCapability("storage.wire", "path.get", {})).value;
  } catch (error) {
    userPath = null;
    pathError = error.code ? error.code + ": " + error.message : String(error);
  }
  if (userPath === null && pathError) {
    rows.push(["PATH 接线", false, "无法读取用户 PATH: " + pathError]);
  }
  const actualPath = String(userPath || "").split(";").map((item) => item.trim()).filter(Boolean);
  const currentRoot = path.join(stableRoot, "current") + path.sep;
  const expectedPath = [];
  for (const record of records) {
    for (const relative of (record.wiring && record.wiring.path) || []) {
      expectedPath.push(path.join(stableRoot, "current", String(record.id), String(relative)));
    }
  }
  const missingPath = expectedPath.filter((entry) => !actualPath.some((item) => samePathValue(item, entry)));
  const stalePath = actualPath.filter((entry) =>
    entry.toLowerCase().startsWith(currentRoot.toLowerCase()) &&
    !expectedPath.some((item) => samePathValue(item, entry)));
  rows.push(["PATH 接线", missingPath.length === 0 && stalePath.length === 0,
    missingPath.length === 0 && stalePath.length === 0
      ? expectedPath.length + " 条记录在 PATH 中"
      : "缺少: " + (missingPath.slice(0, 2).join(", ") || "无") +
        (stalePath.length > 0 ? "；失效: " + stalePath.slice(0, 2).join(", ") : "")]);

  let userEnv = {};
  try {
    userEnv = (await callCapability("storage.wire", "env.list", {})).env || {};
  } catch (error) {
    userEnv = {};
  }
  const envProblems = [];
  let envCount = 0;
  for (const record of records) {
    for (const [name, value] of Object.entries((record.wiring && record.wiring.env) || {})) {
      const expected = expandWiringValue(record, value, stableRoot);
      if (expected.includes("{{")) {
        continue;
      }
      envCount += 1;
      const actual = userEnv[name];
      if (actual === undefined || actual === null) {
        envProblems.push(name + "(缺失)");
      } else if (String(actual) !== expected) {
        envProblems.push(name + "(值不符)");
      }
    }
  }
  rows.push(["环境变量", envProblems.length === 0,
    envProblems.length === 0 ? envCount + " 个变量一致" : envProblems.slice(0, 3).join(", ")]);

  const registryProblems = [];
  let registryCount = 0;
  for (const record of records) {
    for (const entry of (record.wiring && record.wiring.registry) || []) {
      const expected = expandWiringValue(record, entry.value, stableRoot);
      if (expected.includes("{{")) {
        continue;
      }
      registryCount += 1;
      let actual = null;
      try {
        actual = (await callCapability("storage.wire", "registry.get", {
          hive: String(entry.hive || "HKCU"),
          key: String(entry.key || ""),
          name: String(entry.name || ""),
        })).value;
      } catch (error) {
        actual = null;
      }
      if (!actual || String(actual.value) !== expected) {
        registryProblems.push(String(entry.name || entry.key) + "(缺失或值不符)");
      }
    }
  }
  rows.push(["注册表接线", registryProblems.length === 0,
    registryProblems.length === 0 ? registryCount + " 项一致" : registryProblems.slice(0, 3).join(", ")]);

  let selfRootHome = false;
  try {
    selfRootHome = path.resolve(fs.realpathSync(stableRoot)).toLowerCase() ===
      path.resolve(fs.realpathSync(ROOT)).toLowerCase();
  } catch (error) {
    selfRootHome = path.resolve(stableRoot).toLowerCase() === path.resolve(ROOT).toLowerCase();
  }
  const homeProblems = [];
  let homeCount = 0;
  for (const record of records) {
    const wiring = record.wiring || {};
    const text = JSON.stringify(wiring.data || []) + JSON.stringify(wiring.env || {}) + JSON.stringify(wiring.path || {});
    if ((wiring.data || []).length === 0 && !text.includes("{{HOME}}") && !text.includes("{{PERSIST}}")) {
      continue;
    }
    homeCount += 1;
    if (selfRootHome) {
      // C:\Dev points at this stick root: <stableRoot>\home\<pkg> IS the stick directory, so
      // there is no separate junction to validate.
      continue;
    }
    let info = null;
    try {
      info = await callCapability("storage.wire", "home.get", { pkg: String(record.id) });
    } catch (error) {
      info = null;
    }
    if (!info || !info.source) {
      homeProblems.push(String(record.id) + "(联接缺失)");
    } else if (!samePathValue(info.source, info.stickPath)) {
      homeProblems.push(String(record.id) + "(指向错误)");
    }
  }
  rows.push(["家目录联接", homeProblems.length === 0,
    homeProblems.length === 0
      ? homeCount + " 个包" + (selfRootHome ? "（稳定根=盘根，无独立联接）" : "")
      : homeProblems.join("; ")]);
  return { rows: rows, stalePath: stalePath };
}// `pd doctor`: read-only health report over the stick, with an optional repair pass.
async function doctorCommand(message) {
  const io = commandIo(message);
  const { flags } = commandContext(message);
  try {
    const root = ROOT;
    const settings = readSettings();
    const stableRoot = String((settings.root && settings.root.stableRoot) || "C:\\Dev");
    const rows = [];
    const issues = { count: 0 };
    const add = (check, ok, detail) => {
      if (!ok) {
        issues.count += 1;
      }
      rows.push([check, ok ? "ok" : "问题", detail]);
    };

    // (a) stick marker
    const stick = readJsonSafe(path.join(root, "state", "stick.json"));
    add("盘标记", Boolean(stick && stick.stickId), stick && stick.stickId ? "stickId " + stick.stickId : "state/stick.json 缺失或没有 stickId");

    // (b) bundled runtime
    const runtime = path.join(root, "app", "runtime");
    const dotnetHost = path.join(runtime, "dotnet.exe");
    const nodeHost = path.join(runtime, "node", "node.exe");
    let clrVersions = [];
    try {
      const clrRoot = path.join(runtime, "shared", "Microsoft.NETCore.App");
      clrVersions = fs.readdirSync(clrRoot).filter((name) => fs.statSync(path.join(clrRoot, name)).isDirectory());
    } catch (error) {
      clrVersions = [];
    }
    add("随盘 .NET", fs.existsSync(dotnetHost) && clrVersions.length > 0,
      clrVersions.length > 0 ? "NET " + clrVersions.sort().pop() : "app/runtime 缺少 dotnet.exe 或 shared 运行时");
    add("随盘 Node", fs.existsSync(nodeHost), fs.existsSync(nodeHost) ? nodeHost : "app/runtime/node/node.exe 缺失");

    // (c) built-in extensions vs the pristine baseline
    const baselineRoot = path.join(root, "app", "baseline", "extensions");
    const installedRoot = path.join(root, "app", "extensions");
    let baselineNames = [];
    try {
      baselineNames = fs.readdirSync(baselineRoot).filter((name) => fs.statSync(path.join(baselineRoot, name)).isDirectory());
    } catch (error) {
      baselineNames = [];
    }
    const damaged = [];
    for (const name of baselineNames) {
      const want = readJsonSafe(path.join(baselineRoot, name, "ext.json"));
      const got = readJsonSafe(path.join(installedRoot, name, "ext.json"));
      if (!got) {
        damaged.push(name + "(缺失)");
        continue;
      }
      if (want && String(want.version) !== String(got.version)) {
        damaged.push(name + "(" + got.version + " != baseline " + want.version + ")");
      }
    }
    add("内置扩展", baselineNames.length > 0 && damaged.length === 0,
      baselineNames.length === 0 ? "app/baseline/extensions 不存在（无法自愈）"
        : damaged.length > 0 ? damaged.join("; ")
        : baselineNames.length + " 个与 baseline 一致");

    // (d) records vs store and the stable-root current junctions
    const records = ((await callCapability("storage.record", "list", {})).items) || [];
    const badRecords = [];
    for (const item of records) {
      let record = null;
      try {
        record = await callCapability("storage.record", "get", { pkg: item.id });
      } catch (error) {
        badRecords.push(item.id + "(记录不可读)");
        continue;
      }
      if (!record.storePath || !fs.existsSync(record.storePath)) {
        badRecords.push(item.id + "(store 缺失)");
        continue;
      }
      if (!fs.existsSync(path.join(stableRoot, "current", String(item.id)))) {
        badRecords.push(item.id + "(current 缺失)");
      }
    }
    add("记录与 store", badRecords.length === 0,
      badRecords.length > 0 ? badRecords.join("; ") : records.length + " 个包一致");

    // (e) leftover intents
    let intents = [];
    try {
      intents = fs.readdirSync(path.join(root, "state", "intent")).filter((name) => name.endsWith(".json"));
    } catch (error) {
      intents = [];
    }
    add("残留 intent", intents.length === 0,
      intents.length > 0 ? intents.length + " 个待恢复: " + intents.slice(0, 3).join(", ") : "无");

    // (f) settings parse
    const settingsDoc = readJsonSafe(path.join(root, "config", "settings.json"));
    add("配置", settingsDoc !== null, settingsDoc !== null ? "config/settings.json 可解析" : "config/settings.json 无法解析");

    // (g) machine-side wiring vs the records: PATH, env vars, registry values and home junctions.
    const fullRecords = [];
    for (const item of records) {
      try {
        fullRecords.push(await callCapability("storage.record", "get", { pkg: item.id }));
      } catch (error) {
        // A record that cannot be read is already reported by the records check.
      }
    }
    const wiringResult = await collectWiringChecks(fullRecords, stableRoot);
    const wiringRows = wiringResult.rows;
    for (const [check, ok, detail] of wiringRows) {
      add(check, ok, detail);
    }

    // (h) restricted / read-only session: probe a write, then read the integrity level. A Low-IL
    // session can often still write the stick but breaks toolchains that need admin paths.
    let sessionOk = true;
    let sessionDetail = "当前会话可写";
    try {
      const probeDir = path.join(root, "state", "tmp");
      fs.mkdirSync(probeDir, { recursive: true });
      const probe = path.join(probeDir, "doctor-probe-" + process.pid + "-" + Date.now().toString(36));
      fs.writeFileSync(probe, "probe");
      fs.rmSync(probe, { force: true });
    } catch (error) {
      sessionOk = false;
      sessionDetail = "当前会话不可写（受限/只读会话，错误 " + (error.code || error.message) + "）：请用非受限会话重新运行";
    }

    if (sessionOk) {
      const integrity = await readIntegrityLevel();
      if (integrity.label) {
        sessionDetail = "完整性级别: " + integrity.label;
        if (integrity.label === "低") {
          sessionOk = false;
          sessionDetail = "受限会话（Low IL）：部分工具链/提权操作会失败，请用非受限会话重新运行";
        }
      }
    }
    add("受限会话", sessionOk, sessionDetail);

    // (g) stable root: the machine-side anchor for PATH/env wiring. It must point at THIS
    // stick root; pointing at another root (for example an old V1 stick) silently writes PATH
    // entries into the wrong place.
    const fakeMode = fs.existsSync(path.join(root, "state", "wire", "fake-mode"));
    let stableOk = false;
    let stableDetail = "";
    let stableTarget = "";
    try {
      const stat = fs.lstatSync(stableRoot);
      if (stat.isSymbolicLink()) {
        try { stableTarget = fs.realpathSync(stableRoot); } catch (error) { stableTarget = fs.readlinkSync(stableRoot); }
      }
      const normalize = (value) => process.platform === "win32"
        ? path.resolve(String(value)).toLowerCase()
        : path.resolve(String(value));
      const pointsHere = stableTarget.length > 0 && normalize(stableTarget) === normalize(root);
      if (fakeMode) {
        stableOk = true;
        stableDetail = "fake 模式（跳过稳定根目标校验）";
      } else if (!stableTarget) {
        stableDetail = "普通目录（未指向盘根）";
      } else if (!pointsHere) {
        stableDetail = "联接指向 " + stableTarget + "，应为本盘 " + path.resolve(root);
      } else {
        stableOk = true;
        stableDetail = "联接 -> " + stableTarget;
      }
    } catch (error) {
      stableOk = false;
      stableDetail = fakeMode ? "fake 模式（跳过稳定根目标校验）" : "稳定根不存在: " + stableRoot;
      stableOk = fakeMode;
    }
    add("稳定根", stableOk, stableDetail);

    // (h) current links: every record must have a link that resolves to its store
    const brokenLinks = [];
    for (const item of records) {
      try {
        const full = await callCapability("storage.record", "get", { pkg: item.id });
        const target = path.join(stableRoot, "current", String(item.id));
        if (!fs.existsSync(target)) {
          brokenLinks.push(item.id + "(current 缺失)");
          continue;
        }
        const actual = fs.realpathSync(target);
        const expected = fs.realpathSync(String(full.storePath || ""));
        if (process.platform === "win32" ? actual.toLowerCase() !== expected.toLowerCase() : actual !== expected) {
          brokenLinks.push(item.id + "(指向错误)");
        }
      } catch (error) {
        brokenLinks.push(item.id + "(" + (error.code || "ERR_IO") + ")");
      }
    }
    add("current 接线", brokenLinks.length === 0,
      brokenLinks.length > 0 ? brokenLinks.join("; ") : records.length + " 个包联接正常");

    if (flags.deep === true) {
      // (i) blob cache: every blob must exist and be non-empty
      let blobCount = 0;
      let blobBytes = 0;
      const emptyBlobs = [];
      try {
        const walk = (directory) => {
          for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
            const full = path.join(directory, entry.name);
            if (entry.isDirectory()) {
              walk(full);
            } else {
              const stat = fs.statSync(full);
              blobCount += 1;
              blobBytes += stat.size;
              if (stat.size === 0) {
                emptyBlobs.push(path.relative(CACHE, full));
              }
            }
          }
        };
        walk(CACHE);
      } catch (error) {
        blobCount = 0;
      }
      add("blob 缓存", emptyBlobs.length === 0,
        blobCount + " 个文件 / " + Math.round(blobBytes / 1024) + " KB" +
        (emptyBlobs.length > 0 ? "，空文件: " + emptyBlobs.slice(0, 3).join(", ") : ""));

      // (j) shadowed source candidates: the same id offered by more than one enabled source
      const owners = Object.create(null);
      let enabledSources = [];
      try {
        const manager = sources();
        const loaded = await manager.load();
        enabledSources = (loaded.sources || loaded || []).filter((source) => source && source.enabled !== false);
        for (const source of enabledSources) {
          try {
            const loadedIndex = await manager.loadIndex(source, { offline: true });
            const index = loadedIndex.index || loadedIndex;
            for (const id of Object.keys(index.packages || {})) {
              (owners[id] = owners[id] || []).push(source.name);
            }
          } catch (error) {
            // unreachable sources are reported by pd outdated/source, not by doctor
          }
        }
      } catch (error) {
        enabledSources = [];
      }
      const shadowed = Object.entries(owners).filter(([, names]) => names.length > 1)
        .map(([id, names]) => id + "(" + names.join("/") + ")");
      add("源冲突", true,
        shadowed.length === 0
          ? enabledSources.length + " 个启用源，无重复包"
          : shadowed.length + " 个包被遮蔽: " + shadowed.slice(0, 3).join("; "));

      // (k) filesystem: junctions need NTFS
      let filesystem = "未知";
      let ntfsOk = false;
      const systemRoot = process.env.SystemRoot || process.env.windir || "C:\\Windows";
      const volumeRoot = path.parse(ROOT).root || "";
      const driveMatch = /^[A-Za-z]:$/.exec(volumeRoot.replace(/[\\/]+$/, ""));
      const drive = driveMatch ? driveMatch[0] : null;
      if (!drive) {
        filesystem = "非盘符根，无法探测（NTFS 联接需要盘符）";
      } else {
        // Keep each probe in its own try: PowerShell being unavailable must not skip fsutil.
        try {
          const powershell = path.join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
          const result = await runCapturedCommand({
            call: callCapability,
            command: powershell,
            args: ["-NoProfile", "-NonInteractive", "-Command", "(Get-Volume -DriveLetter " + drive.charAt(0) + ").FileSystem"],
            cwd: ROOT,
            env: assertionEnv(ROOT),
            timeoutMs: 20000,
          });
          const flat = String(result.output || "").replace(/\u0000/g, "");
          ntfsOk = /\bNTFS\b/i.test(flat);
          filesystem = ntfsOk ? "NTFS" : "未知或非 NTFS";
        } catch (error) {
          filesystem = "未知";
        }
        if (!ntfsOk) {
          // fsutil is native and does not depend on PowerShell language mode or PATH.
          try {
            const fsutil = path.join(systemRoot, "System32", "fsutil.exe");
            const fallback = await runCapturedCommand({
              call: callCapability,
              command: fsutil,
              args: ["fsinfo", "volumeinfo", drive],
              cwd: ROOT,
              env: assertionEnv(ROOT),
              timeoutMs: 15000,
            });
            if (/\bNTFS\b/i.test(String(fallback.output || "").replace(/\u0000/g, ""))) {
              ntfsOk = true;
              filesystem = "NTFS";
            }
          } catch (error) {
            // keep unknown; doctor reports it as a problem either way
          }
        }
      }
      add("文件系统", ntfsOk, filesystem + (ntfsOk ? "" : "（v2 的联接需要 NTFS）"));
    }

    let fixed = 0;
    if (flags.fix === true) {
      for (const name of baselineNames) {
        const target = path.join(installedRoot, name, "ext.json");
        const want = readJsonSafe(path.join(baselineRoot, name, "ext.json"));
        const got = readJsonSafe(target);
        const stale = !got || (want && String(want.version) !== String(got.version));
        if (!stale) {
          continue;
        }
        try {
          fs.cpSync(path.join(baselineRoot, name), path.join(installedRoot, name), { recursive: true, force: true });
          fixed += 1;
        } catch (error) {
          io.event("log", { level: "warn", msg: "restore " + name + " failed: " + (error.message || String(error)) });
        }
      }

      for (const name of intents) {
        const file = path.join(root, "state", "intent", name);
        const document = readJsonSafe(file);
        if (document !== null && document.phase !== "done") {
          continue;
        }
        try {
          fs.rmSync(file, { force: true });
          fixed += 1;
        } catch (error) {
          io.event("log", { level: "warn", msg: "remove intent " + name + " failed: " + (error.message || String(error)) });
        }
      }

      // Stable root: re-point the machine junction at this stick root when it is missing or
      // points somewhere else. storage.wire uses the elevated helper, so on a real machine
      // this is the single UAC prompt of the repair; in fake-mode the check is skipped.
      if (!stableOk && !fakeMode) {
        try {
          await callCapability("storage.wire", "stableRoot", { task: message.id, target: stableRoot });
          fixed += 1;
          stableOk = true;
          stableDetail = "已重指 -> " + path.resolve(root);
          io.event("log", { level: "info", msg: "stable root re-pointed to " + root });
        } catch (error) {
          io.event("log", { level: "warn", msg: "stable root re-point failed: " + (error.message || String(error)) });
        }
      }

      // PATH/env/registry/home links are rebuilt by the storage wiring layer, which already owns
      // the before/after ledger and the elevated helper: one idempotent apply instead of N writes.
      if (wiringRows.some((row) => row[1] !== true)) {
        try {
          await callCapability("storage.wire", "apply", { task: message.id });
          fixed += 1;
        } catch (error) {
          io.event("log", { level: "warn", msg: "wire apply failed: " + (error.message || String(error)) });
        }
        // apply only adds what the records expect; entries added outside PortableDev are removed here.
        for (const stale of wiringResult.stalePath) {
          try {
            await callCapability("storage.wire", "path.remove", { task: message.id, dir: stale });
            fixed += 1;
          } catch (error) {
            io.event("log", { level: "warn", msg: "path.remove " + stale + " failed: " + (error.message || String(error)) });
          }
        }
      }
    }

    if (flags.fix === true) {
      // Reflect the post-fix state instead of reporting the problems we just repaired.
      const damagedAfter = [];
      for (const name of baselineNames) {
        const want = readJsonSafe(path.join(baselineRoot, name, "ext.json"));
        const got = readJsonSafe(path.join(installedRoot, name, "ext.json"));
        if (!got) {
          damagedAfter.push(name + "(缺失)");
        } else if (want && String(want.version) !== String(got.version)) {
          damagedAfter.push(name + "(" + got.version + " != baseline " + want.version + ")");
        }
      }
      let intentsAfter = [];
      try {
        intentsAfter = fs.readdirSync(path.join(root, "state", "intent")).filter((name) => name.endsWith(".json"));
      } catch (error) {
        intentsAfter = [];
      }
      const stableRow = rows.find((row) => row[0] === "稳定根");
      if (stableRow) {
        stableRow[1] = stableOk ? "ok" : "问题";
        stableRow[2] = stableDetail;
      }
      const extensionRow = rows.find((row) => row[0] === "内置扩展");
      if (extensionRow) {
        extensionRow[1] = damagedAfter.length === 0 ? "ok" : "问题";
        extensionRow[2] = damagedAfter.length === 0
          ? baselineNames.length + " 个与 baseline 一致" : damagedAfter.join("; ");
      }
      const intentRow = rows.find((row) => row[0] === "残留 intent");
      if (intentRow) {
        intentRow[1] = intentsAfter.length === 0 ? "ok" : "问题";
        intentRow[2] = intentsAfter.length === 0 ? "无" : intentsAfter.length + " 个待恢复";
      }
      const refreshedWiring = (await collectWiringChecks(fullRecords, stableRoot)).rows;
      for (const [check, ok, detail] of refreshedWiring) {
        const row = rows.find((item) => item[0] === check);
        if (row) {
          row[1] = ok ? "ok" : "问题";
          row[2] = detail;
        }
      }
      issues.count = rows.filter((row) => row[1] !== "ok").length;
    }

    io.event("table", {
      title: "doctor",
      columns: [
        { key: "check", title: "检查" },
        { key: "status", title: "状态" },
        { key: "detail", title: "说明" },
      ],
      rows: rows,
    });
    io.done({
      summary: issues.count === 0
        ? "体检通过" + (fixed > 0 ? "，已修 " + fixed + " 项" : "")
        : issues.count + " 个问题" + (fixed > 0 ? "，已修 " + fixed + " 项" : "") + "（--fix 可修扩展缺失与残留 intent）",
      metrics: { issues: issues.count, fixed: fixed, checks: rows.map((row) => ({ check: row[0], ok: row[1] === "ok", detail: row[2] })) },
    });
  } catch (error) {
    io.fail(error.code ? error : new PkgError("ERR_IO", error.message || String(error), 1));
  }
}
async function selftestCommand(message) {
  const io = commandIo(message);
  const { flags } = commandContext(message);
  const only = flags.only === undefined ? null : String(flags.only);
  const full = flags.full === true;
  try {
    const settings = readSettings();
    const stableRoot = String((settings.root && settings.root.stableRoot) || "C:\\\\Dev");
    const items = ((await callCapability("storage.record", "list", {})).items) || [];
    let selected = items;
    if (only !== null) {
      selected = items.filter((item) => item.id === only);
      if (selected.length === 0) {
        io.fail(new PkgError("ERR_PKG_NOT_FOUND", "not installed: " + only, 20));
        return;
      }
    }

    const rows = [];
    let passed = 0;
    let failed = 0;
    for (const item of selected) {
      const record = await callCapability("storage.record", "get", { pkg: item.id });
      const root = String(record.storePath || "");
      // Assertions live on the record (written at install time); older records fall back to the
      // package.json inside the store when it exists.
      const manifest = readJsonSafe(path.join(root, "package.json")) || {};
      const storedAsserts = Array.isArray(record.asserts) ? record.asserts : manifest.asserts;
      const asserts = Array.isArray(storedAsserts) ? storedAsserts.slice() : [];
      const storedSelftest = Array.isArray(record.selftest) ? record.selftest : manifest.selftest;
      if (full && Array.isArray(storedSelftest)) {
        for (const entry of storedSelftest) {
          if (entry && typeof entry === "object") {
            asserts.push(Object.assign({ kind: "command" }, entry));
          }
        }
      }
      if (asserts.length === 0) {
        rows.push([item.id, "(未声明断言)", "跳过", "package.json 没有 asserts"]);
        continue;
      }

      const results = await evaluateAsserts(root, asserts, {
        cwd: root,
        pathPrefix: path.join(stableRoot, "current", String(item.id)),
      });
      for (const result of results) {
        rows.push([item.id, result.name, result.ok ? "通过" : "失败", result.detail]);
        if (result.ok) {
          passed += 1;
        } else {
          failed += 1;
        }
      }
    }

    io.event("table", {
      title: "selftest",
      columns: [
        { key: "package", title: "包" },
        { key: "check", title: "检查" },
        { key: "result", title: "结果" },
        { key: "detail", title: "说明" },
      ],
      rows: rows,
    });
    const summary = passed + " passed, " + failed + " failed";
    if (failed > 0) {
      io.fail(new PkgError("ERR_ASSERT", summary, 12));
      return;
    }
    io.done({ summary: summary, metrics: { passed: passed, failed: failed, packages: selected.length } });
  } catch (error) {
    io.fail(error.code ? error : new PkgError("ERR_IO", error.message || String(error), 1));
  }
}function commandIo(message) {
  const id = message.id;
  let seq = 0;
  return {
    id: id,
    event: (type, data) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type }, data) }),
    done: (payload) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) }),
    fail: (error) => send({
      v: 1, kind: "evt", id, seq: ++seq,
      data: {
        type: "done", ok: false,
        exitCode: error.exitCode || exitForCode(error.code),
        error: { code: error.code || "ERR_INTERNAL", message: error.message || String(error) },
      },
    }),
  };
}

function commandContext(message) {
  const argv = (message.data && message.data.argv) || [];
  const parsed = parseFlags(argv);
  const clientCwd = (message.data && message.data.ctx && message.data.ctx.cwd) || process.cwd();
  const resolveFrom = (value) => (path.isAbsolute(String(value)) ? path.resolve(String(value)) : path.resolve(clientCwd, String(value)));
  return { argv: argv, flags: parsed.flags, positional: parsed.positional, resolveFrom: resolveFrom };
}

function cacheState(source, manager) {
  if (source.kind === "dir" || source.kind === "builtin") {
    return "live";
  }
  const cache = manager.cacheFor(source);
  if (!fs.existsSync(cache.index)) {
    return "missing";
  }
  try {
    const meta = JSON.parse(fs.readFileSync(cache.meta, "utf8"));
    const age = Date.now() - Date.parse(meta.fetchedAt || 0);
    return age < manager.ttlMillis(source) ? "fresh" : "stale";
  } catch (error) {
    return "cached";
  }
}

async function sourceCommand(message) {
  const io = commandIo(message);
  const { flags, positional, resolveFrom } = commandContext(message);
  const manager = sources();
  const sub = positional[0] || "list";
  try {
    const document = manager.load();
    if (sub === "list") {
      const rows = document.sources
        .slice()
        .sort((left, right) => (left.priority || 500) - (right.priority || 500))
        .map((source) => [source.name, source.kind, String(source.priority || 500), source.enabled === false ? "no" : "yes", cacheState(source, manager)]);
      await io.event("table", {
        title: "sources",
        columns: [
          { key: "name", title: "源" },
          { key: "kind", title: "类型" },
          { key: "priority", title: "优先级" },
          { key: "enabled", title: "启用" },
          { key: "cache", title: "索引" },
        ],
        rows: rows,
      });
      io.done({ summary: rows.length + " sources", metrics: { sources: rows.length } });
      return;
    }

    if (sub === "add") {
      const name = String(positional[1] || "");
      const target = positional[2] ? String(positional[2]) : "";
      if (!/^[a-z][a-z0-9-]{0,63}$/.test(name)) {
        throw new PkgError("ERR_BAD_ARGS", "source name must match [a-z][a-z0-9-]*", 2);
      }
      if (document.sources.some((source) => source.name === name)) {
        throw new PkgError("ERR_BAD_ARGS", "source already exists: " + name, 2);
      }
      const kind = String(flags.kind || (/^https?:\/\//i.test(target) ? "http" : /^(gh:|github:)/i.test(target) ? "git" : "dir"));
      if (kind === "builtin") {
        throw new PkgError("ERR_UNSUPPORTED", "builtin sources ship with the app and cannot be added by hand", 11);
      }
      const source = { name: name, kind: kind, priority: Number(flags.priority) || 500, enabled: true };
      if (kind === "dir") {
        if (!target) {
          throw new PkgError("ERR_BAD_ARGS", "source add <name> <path> needs a directory for kind=dir", 2);
        }
        const directory = resolveFrom(target);
        if (!fs.existsSync(directory)) {
          throw new PkgError("ERR_SOURCE_UNREACHABLE", "source directory does not exist: " + directory, 15);
        }
        source.path = directory;
      } else if (kind === "http") {
        if (!/^https?:\/\//i.test(target)) {
          throw new PkgError("ERR_BAD_ARGS", "kind=http needs an http(s) index url", 2);
        }
        source.url = target;
      } else if (kind === "git") {
        const repo = target.replace(/^(gh:|github:)/i, "");
        source.repo = repo;
        source.url = "https://raw.githubusercontent.com/" + repo + "/HEAD/index.json";
      } else if (kind === "scoop") {
        const repo = String(target || "").replace(/^(gh:|github:)/i, "");
        if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)) {
          throw new PkgError("ERR_BAD_ARGS", "kind=scoop needs gh:owner/repo, got: " + String(target || ""), 2);
        }
        source.repo = repo;
        source.url = "https://github.com/" + repo;
        if (flags.branch) {
          source.branch = String(flags.branch);
        }
        if (flags.subdir) {
          source.subdir = String(flags.subdir);
        }
        if (flags["tarball-url"]) {
          source.tarballUrl = String(flags["tarball-url"]);
        }
      } else {
        throw new PkgError("ERR_UNSUPPORTED", "source kind " + kind + " is not implemented yet (dir/http/git/scoop)", 11);
      }
      if (flags.ttl) {
        source.ttl = String(flags.ttl);
      }
      document.sources.push(source);
      manager.save(document);
      io.done({ summary: "added source " + name, metrics: { name: name, kind: kind, priority: source.priority } });
      return;
    }

    if (sub === "remove") {
      const name = String(positional[1] || "");
      const index = document.sources.findIndex((source) => source.name === name);
      if (index < 0) {
        throw new PkgError("ERR_PKG_NOT_FOUND", "source not found: " + name, 20);
      }
      document.sources.splice(index, 1);
      manager.save(document);
      fs.rmSync(manager.cacheFor({ name: name }).dir, { recursive: true, force: true });
      io.done({ summary: "removed source " + name, metrics: { name: name } });
      return;
    }

    if (sub === "enable" || sub === "disable") {
      const name = String(positional[1] || "");
      const source = document.sources.find((candidate) => candidate.name === name);
      if (!source) {
        throw new PkgError("ERR_PKG_NOT_FOUND", "source not found: " + name, 20);
      }
      source.enabled = sub === "enable";
      manager.save(document);
      io.done({ summary: sub + "d source " + name, metrics: { name: name, enabled: source.enabled } });
      return;
    }

    if (sub === "refresh") {
      const names = positional[1] ? [String(positional[1])] : document.sources.filter((source) => source.enabled !== false).map((source) => source.name);
      let refreshed = 0;
      let stale = 0;
      const errors = [];
      const failures = [];
      for (const name of names) {
        const source = document.sources.find((candidate) => candidate.name === name);
        if (!source) {
          errors.push(name + ": source not found");
          failures.push(new PkgError("ERR_PKG_NOT_FOUND", "source not found: " + name, 20));
          continue;
        }
        io.event("step", { stepId: "refresh-" + name, title: "refresh " + name, state: "running" });
        try {
          const loaded = await manager.loadIndex(source, { refresh: true, offline: flags.offline === true });
          refreshed += 1;
          if (loaded.stale) {
            stale += 1;
          }
          io.event("step", { stepId: "refresh-" + name, title: "refresh " + name, state: "done" });
        } catch (error) {
          io.event("step", { stepId: "refresh-" + name, title: "refresh " + name, state: "failed" });
          errors.push(name + ": " + (error.message || String(error)));
          failures.push(error);
        }
      }
      if (refreshed === 0 && errors.length > 0) {
        // Single-source refresh must surface the real code (e.g. ERR_INDEX_INVALID), not a generic one.
        if (failures.length === 1 && failures[0] instanceof Error && failures[0].code) {
          throw failures[0];
        }
        throw new PkgError("ERR_SOURCE_UNREACHABLE", errors.join("; "), 15);
      }
      io.done({ summary: "refreshed " + refreshed + " sources" + (stale ? " (" + stale + " stale)" : ""), metrics: { refreshed: refreshed, stale: stale, errors: errors } });
      return;
    }

    throw new PkgError("ERR_BAD_ARGS", "unknown source subcommand: " + sub, 2);
  } catch (error) {
    io.fail(error);
  }
}

async function indexCommand(message) {
  const io = commandIo(message);
  const { flags, positional, resolveFrom } = commandContext(message);
  const manager = sources();
  const sub = positional[0] || "";
  try {
    if (sub === "build") {
      const directory = positional[1] ? resolveFrom(positional[1]) : "";
      if (!directory || !fs.existsSync(directory) || !fs.statSync(directory).isDirectory()) {
        throw new PkgError("ERR_BAD_ARGS", "index build needs an existing directory", 2);
      }
      const name = String(flags.name || path.basename(directory).toLowerCase().replace(/[^a-z0-9-]/g, "-") || "local");
      if (!/^[a-z][a-z0-9-]{0,63}$/.test(name)) {
        throw new PkgError("ERR_BAD_ARGS", "index name must match [a-z][a-z0-9-]*", 2);
      }
      const document = manager.indexFromDirectory(directory);
      document.name = name;
      document.priority = Number(flags.priority) || 500;
      document.generated = new Date().toISOString();
      document.generator = "pd 2.0.0";
      const out = flags.out ? resolveFrom(flags.out) : path.join(directory, "index.json");
      fs.writeFileSync(out, JSON.stringify(document, null, 2) + "\n");
      io.done({
        summary: "index " + name + ": " + Object.keys(document.packages).length + " packages -> " + out,
        metrics: { name: name, out: out, packages: Object.keys(document.packages).length },
      });
      return;
    }

    if (sub === "inspect") {
      const target = positional[1] ? String(positional[1]) : "";
      if (!target) {
        throw new PkgError("ERR_BAD_ARGS", "index inspect needs a file or url", 2);
      }
      let document;
      if (/^https?:\/\//i.test(target)) {
        const dest = path.join(ROOT, "state", "tmp", "index-inspect-" + process.pid + ".json");
        await callCapability("download.fetch", "get", { url: target, dest: dest, maxBytes: 32 * 1024 * 1024 });
        try {
          document = manager.validateIndex(JSON.parse(fs.readFileSync(dest, "utf8")));
        } finally {
          fs.rmSync(dest, { force: true });
        }
      } else {
        const file = resolveFrom(target);
        if (!fs.existsSync(file)) {
          throw new PkgError("ERR_PKG_NOT_FOUND", "index file does not exist: " + file, 20);
        }
        document = manager.validateIndex(JSON.parse(fs.readFileSync(file, "utf8")));
      }
      const rows = Object.entries(document.packages).map(([id, entry]) => [id, entry.latest, String(Object.keys(entry.versions || {}).length), entry.title || ""]);
      await io.event("table", {
        title: "index " + document.name,
        columns: [
          { key: "package", title: "包" },
          { key: "latest", title: "最新" },
          { key: "versions", title: "版本数" },
          { key: "title", title: "名称" },
        ],
        rows: rows,
      });
      io.done({ summary: "index " + document.name + ": " + rows.length + " packages", metrics: { name: document.name, packages: rows.length } });
      return;
    }

    throw new PkgError("ERR_BAD_ARGS", "unknown index subcommand: " + sub, 2);
  } catch (error) {
    io.fail(error);
  }
}

async function searchCommand(message) {
  const io = commandIo(message);
  const { flags, positional } = commandContext(message);
  const manager = sources();
  const term = String(positional[0] || "").toLowerCase();
  if (!term) {
    io.fail(new PkgError("ERR_BAD_ARGS", "search needs a term", 2));
    return;
  }
  try {
    const document = manager.load();
    let sourcesToSearch = document.sources.filter((source) => source.enabled !== false);
    if (flags.source) {
      sourcesToSearch = sourcesToSearch.filter((source) => source.name === String(flags.source));
      if (sourcesToSearch.length === 0) {
        throw new PkgError("ERR_PKG_NOT_FOUND", "source not found: " + flags.source, 20);
      }
    }
    sourcesToSearch = sourcesToSearch.slice().sort((left, right) => (left.priority || 500) - (right.priority || 500));

    let installed = null;
    if (flags.installed === true) {
      const records = await callCapability("storage.record", "list", {});
      installed = new Set((records.items || []).map((item) => item.id));
    }

    const results = [];
    const skipped = [];
    for (const source of sourcesToSearch) {
      let loaded;
      try {
        loaded = await manager.loadIndex(source, { offline: true });
      } catch (error) {
        skipped.push(source.name);
        continue;
      }
      for (const [id, entry] of Object.entries(loaded.index.packages)) {
        if (installed && !installed.has(id)) {
          continue;
        }
        const title = String(entry.title || "");
        const haystack = (id + " " + title).toLowerCase();
        if (!haystack.includes(term)) {
          continue;
        }
        const score = id.toLowerCase() === term ? 3 : id.toLowerCase().startsWith(term) ? 2 : 1;
        results.push({ id: id, latest: entry.latest, title: title, source: source.name, priority: source.priority || 500, score: score });
      }
    }
    results.sort((left, right) => right.score - left.score || left.priority - right.priority || left.id.localeCompare(right.id));
    const limit = Number(flags.limit) > 0 ? Number(flags.limit) : 50;
    const rows = results.slice(0, limit).map((result) => [result.id, result.latest, result.source, result.title]);
    try {
      await io.event("table", {
        title: "search " + term,
        columns: [
          { key: "package", title: "包" },
          { key: "latest", title: "最新" },
          { key: "source", title: "源" },
          { key: "title", title: "名称" },
        ],
        rows: rows,
      });
    } catch (error) {
      // table rendering is best effort
    }
    io.done({
      summary: rows.length + " matches" + (skipped.length ? " (offline: " + skipped.join(",") + ")" : ""),
      metrics: { matches: rows.length, skippedSources: skipped },
    });
  } catch (error) {
    io.fail(error);
  }
}

async function fetchCommand(message) {
  const io = commandIo(message);
  const { flags, positional } = commandContext(message);
  const manager = sources();
  const target = String(positional[0] || "");
  if (!target) {
    io.fail(new PkgError("ERR_BAD_ARGS", "fetch needs a package reference like name@version", 2));
    return;
  }
  try {
    io.event("step", { stepId: "source", title: "resolve source", state: "running" });
    const resolved = await manager.resolve(target, flags);
    io.event("step", { stepId: "source", title: "resolve source", state: "done" });
    io.event("step", { stepId: "fetch", title: "download", state: "running" });
    const blob = await manager.materialize(resolved, flags);
    io.event("step", { stepId: "fetch", title: "download", state: "done" });
    io.done({
      summary: "fetched " + resolved.id + " " + resolved.version,
      metrics: {
        package: resolved.id,
        version: resolved.version,
        source: resolved.source.name,
        path: blob.path,
        cached: blob.cached === true,
        sha256: blob.sha256,
        size: blob.size,
      },
    });
  } catch (error) {
    io.fail(error);
  }
}

// Runs install() as a sub-step of a batch while forwarding its progress to the outer task.
function runInstall(innerMessage, taskId, onEvent, chain) {
  return new Promise((resolve, reject) => {
    const io = {
      event: (type, data) => {
        if (onEvent) {
          onEvent(type, data);
        }
      },
      done: (payload) => resolve(payload || {}),
      fail: (error) => reject(error),
    };
    try {
      const pending = install(innerMessage, { taskId: taskId, io: io, chain: chain });
      if (pending && typeof pending.then === "function") {
        pending.catch(reject);
      }
    } catch (error) {
      reject(error);
    }
  });
}

function sortedRecords(items) {
  return (items || []).slice().sort((left, right) => String(left.id).localeCompare(String(right.id)));
}

async function upgradeCommand(message) {
  const io = commandIo(message);
  const { flags, positional } = commandContext(message);
  const manager = sources();
  try {
    const installed = sortedRecords((await callCapability("storage.record", "list", {})).items);
    if (installed.length === 0) {
      if (positional.length > 0) {
        io.fail(new PkgError("ERR_PKG_NOT_FOUND", positional[0] + " is not installed", 20));
        return;
      }
      io.done({ summary: "no packages installed", metrics: { upgraded: 0, skipped: 0, failed: 0, packages: [] } });
      return;
    }

    const missing = [];
    let selected;
    if (flags.all === true) {
      selected = installed;
    } else if (positional.length > 0) {
      selected = [];
      for (const name of [...new Set(positional)]) {
        const pkg = validPackageId(name);
        const found = installed.find((record) => record.id === pkg);
        if (!found) {
          missing.push(pkg);
          continue;
        }
        selected.push(found);
      }
      if (selected.length === 0) {
        io.fail(new PkgError("ERR_PKG_NOT_FOUND", "not installed: " + missing.join(","), 20));
        return;
      }
    } else {
      io.fail(new PkgError("ERR_BAD_ARGS", "upgrade needs package names or --all", 2));
      return;
    }

    const to = flags.to === undefined ? null : validVersion(flags.to);
    if (to !== null && flags.all === true) {
      io.fail(new PkgError("ERR_BAD_ARGS", "--to needs explicit package names", 2));
      return;
    }

    const plan = [];
    for (const record of selected) {
      if (flags.all === true && record.pinned === true && to === null) {
        plan.push({ record: record, resolved: null, action: "pinned" });
        continue;
      }
      const targetRef = to === null ? record.id : record.id + "@" + to;
      try {
        const resolved = await manager.resolve(targetRef, flags);
        const order = compareVersions(resolved.version, record.version);
        // --to is an explicit pin: it may upgrade, downgrade or reinstall the same version.
        const wanted = to !== null ? true : order > 0;
        plan.push({ record: record, resolved: resolved, action: wanted ? "upgrade" : "skip" });
      } catch (error) {
        plan.push({ record: record, resolved: null, error: error, action: "fail" });
      }
    }

    if (flags["dry-run"] === true) {
      const rows = plan.map((item) => [
        item.record.id,
        item.record.version,
        item.resolved ? item.resolved.version : "-",
        item.error ? "error: " + (item.error.code || "ERR_INTERNAL") : item.action,
      ]);
      try {
        await io.event("table", {
          title: "upgrade plan",
          columns: [
            { key: "package", title: "包" },
            { key: "current", title: "当前" },
            { key: "target", title: "目标" },
            { key: "action", title: "动作" },
          ],
          rows: rows,
        });
      } catch (error) {
        // table rendering is best effort
      }
      const planError = plan.find((item) => item.action === "fail");
      if (planError) {
        const head = planError.error || {};
        io.fail(new PkgError(head.code || "ERR_INTERNAL",
          "dry-run failed for " + planError.record.id + ": " + (head.message || String(head)),
          exitForCode(head.code)));
        return;
      }
      if (missing.length > 0) {
        io.fail(new PkgError("ERR_PKG_NOT_FOUND", "not installed: " + missing.join(","), 20));
        return;
      }
      io.done({
        summary: "dry-run: " + plan.filter((item) => item.action === "upgrade").length + " upgrade(s), " +
          plan.filter((item) => item.action === "pinned").length + " pinned, " +
          plan.filter((item) => item.action === "skip").length + " up to date, " +
          plan.filter((item) => item.action === "fail").length + " error(s)",
        metrics: {
          dryRun: true,
          packages: plan.map((item) => ({
            package: item.record.id,
            current: item.record.version,
            target: item.resolved ? item.resolved.version : null,
            action: item.action,
            error: item.error ? { code: item.error.code || "ERR_INTERNAL", message: item.error.message || String(item.error) } : null,
          })),
        },
      });
      return;
    }

    const upgraded = [];
    const skipped = [];
    const failed = [];
    const atomic = flags.atomic === true;
    const upgradedTasks = [];
    const ctx = (message.data && message.data.ctx) || {};
    let cancelled = false;
    if (atomic) {
      // --atomic is all-or-nothing: refuse the whole batch before changing anything.
      const planError = plan.find((item) => item.action === "fail");
      if (planError) {
        const head = planError.error || {};
        io.fail(new PkgError(head.code || "ERR_INTERNAL",
          "atomic batch aborted before changing anything: " + planError.record.id + ": " +
          (head.message || String(planError.error)), exitForCode(head.code)));
        return;
      }
    }
    for (const item of plan) {
      if (isCancelled(message.id)) {
        cancelled = true;
        break;
      }
      if (item.action === "fail") {
        failed.push({ package: item.record.id, code: (item.error && item.error.code) || "ERR_INTERNAL", message: (item.error && item.error.message) || String(item.error) });
        continue;
      }
      if (item.action === "pinned") {
        skipped.push(item.record.id);
        io.event("log", { level: "info", msg: item.record.id + " is pinned; skipped by --all" });
        continue;
      }
      if (item.action === "skip") {
        skipped.push(item.record.id);
        io.event("log", { level: "info", msg: item.record.id + " " + item.record.version + " is up to date" });
        continue;
      }

      const pkg = item.record.id;
      // One audit task per package: uninstall and install share it so a failed upgrade can roll back
      // to the previous version without touching other packages in the batch.
      const taskId = String(message.id) + "-up-" + pkg;
      io.event("step", { stepId: "up-" + pkg, title: "upgrade " + pkg, state: "running" });
      try {
        await callCapability("storage.uninstall", "run", {
          task: taskId, pkg: pkg, purgeData: false, skipGeneration: true,
        });
        const argv = [pkg + "@" + item.resolved.version, "--source", item.resolved.source.name];
        for (const name of ["no-scripts", "allow-scripts", "force", "no-deps", "channel", "prerelease", "refresh", "offline"]) {
          const value = flags[name];
          if (value === true) {
            argv.push("--" + name);
          } else if (typeof value === "string" && value.length > 0) {
            argv.push("--" + name, value);
          }
        }
        if (item.record.pinned === true) {
          argv.push("--pin");
        }
        throwIfCancelled(message.id);
        const metrics = await runInstall(
          { id: message.id, data: { argv: argv, ctx: ctx, cmd: "install" } },
          taskId,
          (type, data) => {
            if (type === "step") {
              io.event("step", { stepId: "up-" + pkg + "-" + (data.stepId || "step"), title: data.title, state: data.state });
              return;
            }
            if (type === "kv" || type === "table") {
              return;
            }
            io.event(type, data);
          },
        );
        upgraded.push({ package: pkg, from: item.record.version, to: item.resolved.version, storePath: metrics.storePath || null, task: taskId });
        upgradedTasks.push(taskId);
        io.event("step", { stepId: "up-" + pkg, title: "upgrade " + pkg, state: "done" });
      } catch (error) {
        const rollback = await rollbackTask(taskId, io.event);
        const suffix = rollback.failed > 0 ? " (rollback partial: " + rollback.failed + " step(s))" : "";
        failed.push({ package: pkg, code: error.code || "ERR_INTERNAL", message: (error.message || String(error)) + suffix, task: taskId });
        io.event("step", { stepId: "up-" + pkg, title: "upgrade " + pkg, state: "failed" });
        io.event("log", { level: "error", msg: "upgrade failed for " + pkg + ": " + (error.message || String(error)) + suffix +
          "; partial store directories are left for pd gc" });
        if (atomic) {
          // --atomic: the batch is all-or-nothing, so undo the upgrades that already landed.
          let restored = 0;
          for (const prior of upgradedTasks.slice().reverse()) {
            const undo = await rollbackTask(prior, io.event);
            if ((undo.failed || 0) === 0) {
              restored += 1;
            }
          }
          upgraded.length = 0;
          failed[failed.length - 1].message += "; atomic: rolled back " + restored + " package(s)";
          io.event("log", { level: "warn", msg: "atomic batch aborted; rolled back " + restored + " upgrade(s)" });
          break;
        }
        if (isCancelled(message.id)) {
          cancelled = true;
          break;
        }
      }
    }
    if (cancelled) {
      if (atomic) {
        let restored = 0;
        for (const prior of upgradedTasks.slice().reverse()) {
          const undo = await rollbackTask(prior, io.event);
          if ((undo.failed || 0) === 0) {
            restored += 1;
          }
        }
        io.event("log", { level: "warn", msg: "atomic batch cancelled; rolled back " + restored + " upgrade(s)" });
      }
      return;
    }

    const summary = upgraded.length + " upgraded, " + skipped.length + " skipped, " + failed.length + " failed" +
      (missing.length > 0 ? ", not installed: " + missing.join(",") : "");
    if (failed.length > 0) {
      const head = failed[0];
      io.fail(new PkgError(head.code, summary + ": " + failed.map((item) => item.package + " (" + item.message + ")").join("; "), exitForCode(head.code)));
      return;
    }
    if (missing.length > 0) {
      io.fail(new PkgError("ERR_PKG_NOT_FOUND", summary, 20));
      return;
    }
    io.done({
      summary: summary,
      metrics: { upgraded: upgraded.length, skipped: skipped.length, failed: 0, packages: upgraded, skippedPackages: skipped },
    });
  } catch (error) {
    io.fail(error);
  }
}

async function removeCommand(message) {
  const io = commandIo(message);
  const { flags, positional } = commandContext(message);
  if (flags.purge === true && flags["keep-data"] === true) {
    io.fail(new PkgError("ERR_BAD_ARGS", "--purge and --keep-data are mutually exclusive", 2));
    return;
  }
  if (positional.length === 0) {
    io.fail(new PkgError("ERR_BAD_ARGS", "remove needs at least one package name", 2));
    return;
  }
  const dryRun = flags["dry-run"] === true;
  const purgeData = flags.purge === true;
  try {
    const targets = [];
    const missing = [];
    for (const name of positional) {
      const pkg = validPackageId(name);
      try {
        const record = await callCapability("storage.record", "get", { pkg: pkg });
        targets.push({ pkg: pkg, record: record });
      } catch (error) {
        if (error.code === "ERR_PKG_NOT_FOUND") {
          missing.push(pkg);
        } else {
          throw error;
        }
      }
    }

    if (flags.force !== true) {
      const installed = await listInstalled();
      const removing = new Set(targets.map((item) => item.pkg));
      const manager = sources();
      const blocked = [];
      for (const target of targets) {
        const provides = Array.isArray(target.record.provides)
          ? target.record.provides.filter((name) => typeof name === "string") : [];
        const dependents = installed.filter((item) => {
          if (removing.has(item.id) || item.id === target.pkg) {
            return false;
          }
          return (item.requires || []).some((dep) => {
            if (!dep) {
              return false;
            }
            if (dep.id === target.pkg) {
              return true;
            }
            if (!provides.includes(dep.id)) {
              return false;
            }
            // Another installed package may still provide the virtual name: only block
            // when this removal leaves no provider satisfying the declared range.
            let constraint = null;
            try { constraint = manager.parseConstraint(dep.range || ""); } catch (error) { constraint = null; }
            const remaining = installed.filter((other) =>
              !removing.has(other.id) && other.id !== target.pkg &&
              Array.isArray(other.provides) && other.provides.includes(dep.id) &&
              (constraint === null || manager.satisfies(String(other.version), constraint)));
            return remaining.length === 0;
          });
        });
        if (dependents.length > 0) {
          blocked.push(target.pkg + " (required by " + dependents.map((item) => item.id).join(", ") + ")");
        }
      }
      if (blocked.length > 0) {
        throw new PkgError("ERR_DEP_UNSATISFIED", "cannot remove " + blocked.join("; ") + "; remove the dependents first or use --force", 24);
      }
    }

    if (dryRun && targets.length === 0 && missing.length > 0) {
      io.fail(new PkgError("ERR_PKG_NOT_FOUND", "not installed: " + missing.join(","), 20));
      return;
    }
    if (dryRun) {
      const rows = targets.map((item) => [item.pkg, item.record.version, purgeData ? "删除数据" : "保留数据", "would remove"]);
      try {
        await io.event("table", {
          title: "remove plan",
          columns: [
            { key: "package", title: "包" },
            { key: "version", title: "版本" },
            { key: "data", title: "家目录" },
            { key: "action", title: "动作" },
          ],
          rows: rows,
        });
      } catch (error) {
        // table rendering is best effort
      }
      if (missing.length > 0) {
        io.fail(new PkgError("ERR_PKG_NOT_FOUND", "not installed: " + missing.join(",") +
          (targets.length ? " (existing packages are only reported in dry-run)" : ""), 20));
        return;
      }
      io.done({
        summary: "dry-run: would remove " + targets.length + " package(s)" + (missing.length ? ", missing: " + missing.join(",") : "") +
          (purgeData ? " (purge data)" : " (keep data)"),
        metrics: {
          dryRun: true,
          purgeData: purgeData,
          packages: targets.map((item) => ({ package: item.pkg, version: item.record.version })),
          missing: missing,
        },
      });
      return;
    }

    const removed = [];
    const failed = [];
    const atomic = flags.atomic === true;
    const removedTasks = [];
    let cancelled = false;
    for (const item of targets) {
      if (isCancelled(message.id)) {
        cancelled = true;
        break;
      }
      const taskId = String(message.id) + "-rm-" + item.pkg;
      io.event("step", { stepId: "rm-" + item.pkg, title: "remove " + item.pkg, state: "running" });
      try {
        const result = await callCapability("storage.uninstall", "run", {
          task: taskId, pkg: item.pkg, purgeData: purgeData, skipGeneration: false,
        });
        try {
          await callCapability("storage.ledger", "commit", { task: taskId });
        } catch (ledgerError) {
          io.event("log", { level: "warn", msg: "ledger commit failed: " + (ledgerError.message || String(ledgerError)) });
        }
        removed.push({ package: item.pkg, version: item.record.version, homePurged: result.homePurged === true, homeTrash: result.homeTrash || null, task: taskId });
        removedTasks.push(taskId);
        io.event("step", { stepId: "rm-" + item.pkg, title: "remove " + item.pkg, state: "done" });
      } catch (error) {
        const rollback = await rollbackTask(taskId, io.event);
        const suffix = rollback.failed > 0 ? " (rollback partial: " + rollback.failed + " step(s))" : "";
        failed.push({ package: item.pkg, code: error.code || "ERR_INTERNAL", message: (error.message || String(error)) + suffix, task: taskId });
        io.event("step", { stepId: "rm-" + item.pkg, title: "remove " + item.pkg, state: "failed" });
        if (atomic) {
          // --atomic: restore the packages that were already removed before failing the batch.
          let restored = 0;
          for (const prior of removedTasks.slice().reverse()) {
            const undo = await rollbackTask(prior, io.event);
            if ((undo.failed || 0) === 0) {
              restored += 1;
            }
          }
          removed.length = 0;
          failed[failed.length - 1].message += "; atomic: restored " + restored + " package(s)";
          io.event("log", { level: "warn", msg: "atomic batch aborted; restored " + restored + " removal(s)" });
          break;
        }
        if (isCancelled(message.id)) {
          cancelled = true;
          break;
        }
      }
    }
    if (cancelled) {
      if (atomic) {
        let restored = 0;
        for (const prior of removedTasks.slice().reverse()) {
          const undo = await rollbackTask(prior, io.event);
          if ((undo.failed || 0) === 0) {
            restored += 1;
          }
        }
        io.event("log", { level: "warn", msg: "atomic batch cancelled; restored " + restored + " removal(s)" });
      }
      return;
    }

    const parts = [];
    if (removed.length > 0) {
      parts.push("removed " + removed.map((item) => item.package).join(","));
    }
    if (missing.length > 0) {
      parts.push("not installed: " + missing.join(","));
    }
    if (failed.length > 0) {
      parts.push("failed: " + failed.map((item) => item.package + " (" + item.message + ")").join("; "));
    }
    if (failed.length > 0) {
      io.fail(new PkgError(failed[0].code, parts.join("; "), exitForCode(failed[0].code)));
      return;
    }
    if (missing.length > 0) {
      io.fail(new PkgError("ERR_PKG_NOT_FOUND", parts.join("; "), 20));
      return;
    }
    io.done({
      summary: parts.join("; "),
      metrics: { removed: removed, missing: missing, purgeData: purgeData },
    });
  } catch (error) {
    io.fail(error);
  }
}

async function infoCommand(message) {
  const io = commandIo(message);
  const { positional } = commandContext(message);
  const id = String(positional[0] || "").trim();
  try {
    if (!id) {
      throw new PkgError("ERR_BAD_ARGS", "info needs a package name", 2);
    }
    const record = await callCapability("storage.record", "get", { pkg: id });
    const wiring = record.wiring || {};
    io.event("kv", {
      title: "package",
      items: {
        id: record.id,
        version: record.version,
        origin: record.origin || "local",
        storePath: record.storePath || "",
        pinned: record.pinned === true,
        source: record.source || "",
        installedAt: record.installedAt || record.recoveredAt || "",
      },
    });
    const pathEntries = (wiring.path || []).map((item) => typeof item === "string" ? item : String(item.relative || item.path || item));
    const envEntries = Object.entries(wiring.env || {});
    const registryEntries = wiring.registry || [];
    const launchEntries = wiring.launch || [];
    const rows = [];
    for (const entry of pathEntries) {
      rows.push(["path", entry, ""]);
    }
    for (const [name, value] of envEntries) {
      rows.push(["env", name, String(value)]);
    }
    for (const entry of registryEntries) {
      rows.push(["registry", (entry.hive || "HKCU") + "\\" + String(entry.key || ""), String(entry.name || "")]);
    }
    for (const entry of launchEntries) {
      rows.push(["launcher", String(entry.name || ""), String(entry.exe || "")]);
    }
    if (rows.length > 0) {
      io.event("table", {
        title: "wiring",
        columns: [
          { key: "kind", title: "类型" },
          { key: "target", title: "落点" },
          { key: "value", title: "值" },
        ],
        rows: rows,
      });
    }
    const asserts = Array.isArray(record.asserts) ? record.asserts : [];
    if (asserts.length > 0) {
      io.event("table", {
        title: "asserts",
        columns: [
          { key: "kind", title: "类型" },
          { key: "detail", title: "内容" },
        ],
        rows: asserts.map((item) => [String(item.kind || ""), String(item.path || item.run || "")]),
      });
    }
    io.done({
      summary: record.id + " " + record.version,
      metrics: { package: record.id, version: record.version, wiring: rows.length, asserts: asserts.length },
    });
  } catch (error) {
    io.fail(error.code ? error : new PkgError("ERR_IO", error.message || String(error), 1));
  }
}

async function outdatedCommand(message) {
  const io = commandIo(message);
  const { flags } = commandContext(message);
  try {
    const records = sortedRecords((await callCapability("storage.record", "list", {})).items);
    const manager = sources();
    const rows = [];
    let skipped = 0;
    for (const record of records) {
      try {
        const resolved = await manager.resolve(record.id, flags.offline === true ? { offline: true } : {});
        if (compareVersions(resolved.version, record.version) > 0) {
          rows.push([record.id, record.version, resolved.version, resolved.source.name]);
        }
      } catch (error) {
        skipped += 1;
        io.event("log", { level: "debug", msg: record.id + ": " + (error.message || String(error)) });
      }
    }
    io.event("table", {
      title: "outdated",
      columns: [
        { key: "package", title: "包" },
        { key: "installed", title: "已装" },
        { key: "latest", title: "最新" },
        { key: "source", title: "源" },
      ],
      rows: rows,
    });
    io.done({
      summary: rows.length + " 个可升级" + (skipped > 0 ? "，" + skipped + " 个无法解析" : ""),
      metrics: { outdated: rows.length, skipped: skipped, packages: rows.map((row) => row[0]) },
    });
  } catch (error) {
    io.fail(error.code ? error : new PkgError("ERR_IO", error.message || String(error), 1));
  }
}

async function envCommand(message) {
  const io = commandIo(message);
  try {
    const settings = readSettings();
    const stableRoot = String((settings.root && settings.root.stableRoot) || "C:\\\\Dev");
    const records = sortedRecords((await callCapability("storage.record", "list", {})).items);
    const pathRows = [];
    for (const record of records) {
      const full = await callCapability("storage.record", "get", { pkg: record.id });
      for (const relative of (full.wiring && full.wiring.path) || []) {
        const target = typeof relative === "string" ? relative : String(relative.relative || relative.path || relative);
        pathRows.push([record.id, path.join(stableRoot, "current", String(record.id), target)]);
      }
    }
    const currentPath = String(process.env.PATH || "").split(";").filter(Boolean);
    io.event("table", {
      title: "package PATH",
      columns: [
        { key: "package", title: "包" },
        { key: "path", title: "目录" },
      ],
      rows: pathRows,
    });
    io.event("table", {
      title: "process PATH",
      columns: [{ key: "index", title: "#" }, { key: "entry", title: "条目" }],
      rows: currentPath.map((entry, index) => [String(index), entry]),
    });
    const envRows = [];
    for (const record of records) {
      const full = await callCapability("storage.record", "get", { pkg: record.id });
      for (const [name, value] of Object.entries((full.wiring && full.wiring.env) || {})) {
        envRows.push([record.id, name, String(value)]);
      }
    }
    io.event("table", {
      title: "package env",
      columns: [
        { key: "package", title: "包" },
        { key: "name", title: "变量" },
        { key: "value", title: "值" },
      ],
      rows: envRows,
    });
    io.done({
      summary: pathRows.length + " 个 PATH 条目，" + envRows.length + " 个环境变量（pd shell --profile 会把后者注入会话）",
      metrics: { path: pathRows.length, env: envRows.length, stableRoot: stableRoot },
    });
  } catch (error) {
    io.fail(error.code ? error : new PkgError("ERR_IO", error.message || String(error), 1));
  }
}async function listCommand(message) {
  const io = commandIo(message);
  const { flags } = commandContext(message);
  try {
    const records = sortedRecords((await callCapability("storage.record", "list", {})).items);
    if (records.length === 0) {
      io.done({ summary: "no packages installed", metrics: { packages: [] } });
      return;
    }

    const latest = {};
    const latestError = {};
    if (flags.outdated === true) {
      for (const record of records) {
        try {
          const resolved = await sources().resolve(record.id, { offline: true });
          if (compareVersions(resolved.version, record.version) > 0) {
            latest[record.id] = resolved.version;
          }
        } catch (error) {
          latest[record.id] = null;
          latestError[record.id] = error.code || "ERR_IO";
        }
      }
    }

    const rows = [];
    const packages = [];
    for (const record of records) {
      const current = fs.existsSync(path.join(ROOT, "current", record.id));
      const newer = Object.prototype.hasOwnProperty.call(latest, record.id) ? latest[record.id] : undefined;
      rows.push([record.id, record.version, newer === undefined ? "-" : (newer === null ? "?" : newer), current ? "yes" : "missing"]);
      packages.push({ package: record.id, version: record.version, latest: newer === undefined ? null : newer,
        latestError: Object.prototype.hasOwnProperty.call(latestError, record.id) ? latestError[record.id] : null,
        pinned: record.pinned === true, current: current, storePath: record.storePath });
    }

    const outdatedCount = packages.filter((item) => item.latest !== null).length;
    try {
      await io.event("table", {
        title: "installed packages" + (flags.outdated === true ? " (outdated)" : ""),
        columns: [
          { key: "package", title: "包" },
          { key: "version", title: "版本" },
          { key: "latest", title: "最新" },
          { key: "current", title: "current" },
        ],
        rows: rows,
      });
    } catch (error) {
      // table rendering is best effort
    }
    io.done({
      summary: packages.length + " package(s)" + (flags.outdated === true ? ", " + outdatedCount + " outdated" : ""),
      metrics: { packages: packages, outdated: outdatedCount },
    });
  } catch (error) {
    io.fail(error);
  }
}

function expandRunText(text, context) {
  return String(text === undefined || text === null ? "" : text)
    .replace(/\{\{STICK\}\}/g, context.root)
    .replace(/\{\{ROOT\}\}/g, context.stableRoot)
    .replace(/\{\{HOME\}\}/g, path.join(context.root, "home", context.id))
    .replace(/\{\{PERSIST\}\}/g, path.join(context.root, "home", context.id))
    .replace(/\{\{ID\}\}/g, context.id)
    .replace(/\{\{VERSION\}\}/g, context.version || "");
}

// `pd run` executes an installed package program *in the client terminal*: pkgmgr resolves the
// record, the shell spawns the process with inherited stdio and reports the exit code back.
async function runPackage(message) {
  const io = commandIo(message);
  try {
    const argv = (message.data && message.data.argv) || [];
    const separator = argv.indexOf("--");
    const ownArgs = separator >= 0 ? argv.slice(0, separator) : argv;
    const parsed = parseFlags(ownArgs);
    const flags = parsed.flags;
    const target = parsed.positional[0];
    // After the package: explicit `--` separator, or just the remaining positionals.
    const childArgs = separator >= 0 ? argv.slice(separator + 1) : parsed.positional.slice(1);
    if (!target) {
      throw new PkgError("ERR_BAD_ARGS", "usage: pd run <包> --exe <store 内相对路径> [--cwd <目录>] [--detach] [-- args...]", 2);
    }

    const record = await callCapability("storage.record", "get", { pkg: String(target) });
    if (!record || !record.storePath) {
      throw new PkgError("ERR_PKG_INVALID", "record has no store path: " + target, 3);
    }
    const base = path.resolve(String(record.storePath));
    const settings = readSettings();
    const stableRoot = String((settings.root && settings.root.stableRoot) || "C:\\Dev");
    const expandRun = (text) => expandRunText(text, {
      root: ROOT,
      stableRoot: stableRoot,
      id: String(target),
      version: String(record.version || ""),
    });

    let relativeExe = "";
    let launchArgs = [];
    let launchEnv = {};
    let launchCwd = null;
    if (flags.exe) {
      relativeExe = String(flags.exe);
    } else {
      const declarations = (record.wiring && Array.isArray(record.wiring.launch) ? record.wiring.launch : [])
        .filter((entry) => entry && entry.exe);
      const wanted = flags.name ? String(flags.name).toLowerCase() : null;
      const candidates = wanted
        ? declarations.filter((entry) => String(entry.name || "").toLowerCase() === wanted)
        : declarations;
      if (candidates.length === 0) {
        throw new PkgError("ERR_BAD_ARGS",
          (wanted ? "no launch declaration named " + flags.name : "this package has no launch declaration") +
          "; pass --exe <store 内相对路径>", 2);
      }
      if (candidates.length > 1) {
        throw new PkgError("ERR_BAD_ARGS", "the package has several launch declarations; pick one with --name", 2);
      }
      const chosen = candidates[0];
      relativeExe = String(chosen.exe);
      launchArgs = Array.isArray(chosen.args) ? chosen.args.map((entry) => expandRun(String(entry))) : [];
      launchEnv = chosen.env && typeof chosen.env === "object" ? chosen.env : {};
      launchCwd = chosen.cwd ? expandRun(String(chosen.cwd)) : null;
    }

    let command = path.resolve(base, relativeExe);
    if (command !== base && !command.startsWith(base + path.sep)) {
      throw new PkgError("ERR_UNSUPPORTED", "the run target must stay inside the package store path", 11);
    }
    if (!fs.existsSync(command)) {
      throw new PkgError("ERR_PKG_INVALID", "run target does not exist: " + command, 3);
    }
    const cwd = flags.cwd ? path.resolve(base, String(flags.cwd)) : (launchCwd ? path.resolve(base, launchCwd) : base);
    if (cwd !== base && !cwd.startsWith(base + path.sep)) {
      throw new PkgError("ERR_UNSUPPORTED", "the run cwd must stay inside the package store path", 11);
    }

    // Launchers are usually .cmd/.bat; CreateProcess needs an executable, so wrap them in the shell.
    let commandArgs = launchArgs.concat(childArgs);
    let verbatimArgs = false;
    if (/\.(cmd|bat)$/i.test(command)) {
      const cmdLine = buildCmdLine(command, commandArgs);
      commandArgs = ["/d", "/s", "/c", cmdLine];
      command = process.env.ComSpec || "cmd.exe";
      verbatimArgs = true;
    }

    const environment = {};
    for (const [name, value] of Object.entries(process.env)) {
      if (name === "PD_PIPE" || name === "PD_INVOCATION_ID" || name === "PD_EXTENSION" || name === "PD_DOTNET") {
        continue;
      }
      if (value !== undefined) {
        environment[name] = value;
      }
    }
    environment.PATH = path.join(stableRoot, "current", String(target)) + ";" + (environment.PATH || "");
    for (const [name, value] of Object.entries(launchEnv)) {
      environment[name] = expandRun(String(value));
    }
    for (const name of Object.keys(environment)) {
      const upper = String(name).toUpperCase();
      if (upper === "PD_PIPE" || upper === "PD_INVOCATION_ID" || upper === "PD_EXTENSION" || upper === "PD_DOTNET" || upper === "PD_KERNEL_ABI" || upper.startsWith("PD_") || upper.startsWith("PD_INVOCATION")) {
        delete environment[name];
      }
    }

    io.event("step", { stepId: "run", title: "run", state: "running" });
    const reply = await execOnClient(message.id, {
      command: command,
      args: commandArgs,
      verbatimArgs: verbatimArgs,
      cwd: cwd,
      env: environment,
      detach: flags.detach === true,
    });
    if (reply && reply.error) {
      throw new PkgError(reply.error.code || "ERR_IO", reply.error.message || "the client could not start the program", 1);
    }
    const childExitCode = Number(reply && reply.exitCode !== undefined ? reply.exitCode : 1);
    io.event("step", { stepId: "run", title: "run", state: "done" });
    io.done({
      ok: childExitCode === 0,
      exitCode: childExitCode === 0 ? 0 : 1,
      summary: target + " exited with " + childExitCode,
      metrics: { childExitCode: childExitCode, package: target },
    });
  } catch (error) {
    io.fail(error);
  }
}

// `pd shell` opens a terminal with every installed package's current directory on PATH. Like
// `pd run` it goes through the client exec channel, so the child owns the caller's terminal.
async function shellPackage(message) {
  const io = commandIo(message);
  try {
    const argv = (message.data && message.data.argv) || [];
    const separator = argv.indexOf("--");
    const parsed = parseFlags(separator >= 0 ? argv.slice(0, separator) : argv);
    const flags = parsed.flags;
    const shellName = String(parsed.positional[0] || "cmd").toLowerCase();
    if (shellName !== "cmd") {
      throw new PkgError("ERR_UNSUPPORTED", "only the default cmd shell is supported yet", 11);
    }

    const records = sortedRecords((await callCapability("storage.record", "list", {})).items);
    const settings = readSettings();
    const stableRoot = String((settings.root && settings.root.stableRoot) || "C:\\Dev");
    const environment = {};
    for (const [name, value] of Object.entries(process.env)) {
      if (name === "PD_PIPE" || name === "PD_INVOCATION_ID" || name === "PD_EXTENSION" || name === "PD_DOTNET") {
        continue;
      }
      if (value !== undefined) {
        environment[name] = value;
      }
    }
    const managed = records.map((record) => path.join(stableRoot, "current", String(record.id)));
    environment.PATH = managed.concat([environment.PATH || ""]).join(";");
    const cwd = (message.data && message.data.ctx && message.data.ctx.cwd) || ROOT;

    // --profile additionally applies each package's declared env vars for this session only
    // (MSVC/Flutter need INCLUDE/LIB/... without writing HKCU\Environment). It is implemented
    // as a throwaway batch file the shell calls, then removed when the shell exits.
    let profilePath = null;
    let shellArgs = [];
    if (flags.profile === true) {
      const lines = ["@echo off"];
      for (const record of records) {
        let full;
        try {
          full = await callCapability("storage.record", "get", { pkg: record.id });
        } catch (error) {
          continue;
        }
        const wiring = (full && full.wiring) || {};
        for (const relative of wiring.path || []) {
          const dir = path.join(stableRoot, "current", String(record.id), String(relative));
          lines.push('set "PATH=' + dir + ';%PATH%"');
        }
        for (const [name, value] of Object.entries(wiring.env || {})) {
          if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) {
            continue;
          }
          lines.push('set "' + name + '=' + String(value).replace(/"/g, "") + '"');
        }
      }

      const profileDir = path.join(ROOT, "state", "tmp");
      fs.mkdirSync(profileDir, { recursive: true });
      profilePath = path.join(profileDir, "pd-profile-" + process.pid + "-" + Date.now().toString(36) + ".cmd");
      fs.writeFileSync(profilePath, lines.join("\r\n") + "\r\n", "utf8");
      // cmd.exe does not understand MSVCRT quoting, so send one verbatim command line:
      //   cmd.exe /d /s /k call "<profile>"
      shellArgs = ["/d", "/s", "/k", 'call "' + profilePath + '"'];
    }

    io.event("step", { stepId: "shell", title: "shell", state: "running" });
    let reply;
    try {
      reply = await execOnClient(message.id, {
        command: process.env.ComSpec || "cmd.exe",
        args: shellArgs,
        // The /k tail is already cmd-quoted; MSVCRT quoting would break it.
        verbatimArgs: flags.profile === true,
        cwd: cwd,
        env: environment,
        detach: false,
      });
    } finally {
      if (profilePath) {
        try { fs.rmSync(profilePath, { force: true }); } catch (error) { /* best effort */ }
      }
    }
    if (reply && reply.error) {
      throw new PkgError(reply.error.code || "ERR_IO", reply.error.message || "the shell could not be started", 1);
    }
    const childExitCode = Number(reply && reply.exitCode !== undefined ? reply.exitCode : 0);
    io.event("step", { stepId: "shell", title: "shell", state: "done" });
    io.done({
      ok: childExitCode === 0,
      exitCode: childExitCode === 0 ? 0 : 1,
      summary: "shell exited with " + childExitCode,
      metrics: { childExitCode: childExitCode },
    });
  } catch (error) {
    io.fail(error);
  }
}

function sendCommandFailure(message, error) {
  const code = (error && error.code) || "ERR_INTERNAL";
  send({
    v: 1, kind: "evt", id: message.id, seq: 1,
    data: {
      type: "done", ok: false,
      exitCode: (error && error.exitCode) || exitForCode(code),
      error: { code: code, message: (error && error.message) || String(error) },
    },
  });
}

// Parameter errors are thrown by parseFlags/commandContext before a command installs its own catch.
function runCommand(message, handler) {
  try {
    const pending = handler(message);
    if (pending && typeof pending.then === "function") {
      pending.catch((error) => sendCommandFailure(message, error));
    }
  } catch (error) {
    sendCommandFailure(message, error);
  }
}

connect({
  onMessage(message, outbound) {
    send = outbound;
    if (message.kind === "hello.ok") {
      ready(send, [{ name: "pkg.install", version: 1, mutates: true }]);
      return;
    }

    if (message.kind === "ctl") {
      const action = message.data && message.data.action;
      if (action === "cancel") {
        cancelledTasks.add(String(message.id));
        return;
      }
      if (action === "exec.reply") {
        const replyExecId = message.data && message.data.execId;
        const key = replyExecId ? String(message.id) + ":" + String(replyExecId) : String(message.id);
        const entry = pendingExecs.get(key);
        if (entry) {
          clearTimeout(entry.timer);
          pendingExecs.delete(key);
          entry.resolve(message.data || {});
        } else if (!replyExecId) {
          for (const [k, v] of pendingExecs) {
            if (k === String(message.id) || k.startsWith(String(message.id) + ":")) {
              clearTimeout(v.timer);
              pendingExecs.delete(k);
              v.resolve(message.data || {});
              break;
            }
          }
        }
        return;
      }
      if (action === "grant.reply") {
        const entry = pendingGrants.get(String(message.id));
        if (entry) {
          clearTimeout(entry.timer);
          pendingGrants.delete(String(message.id));
          if (message.data.grant) {
            entry.resolve(message.data.grant);
          } else {
            const error = (message.data && message.data.error) || {};
            entry.reject(new PkgError(error.code || "ERR_IO", error.message || "grant refused"));
          }
        }
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
          entry.reject(new PkgError(error.code || "ERR_IO", error.message || "call failed"));
        }
      }
      return;
    }

    if (message.kind === "ext.invoke") {
      const argv = (message.data && message.data.argv) || [];
      // The kernel may route a longer entry ("source list") while the handler stays the same: the
      // first token selects the handler, the rest of argv keeps the subcommand parsing unchanged.
      const command = String((message.data && message.data.cmd) || argv[0] || "").split(/\s+/)[0];
      if (command === "install") {
        runCommand(message, install);
      } else if (command === "remove") {
        runCommand(message, removeCommand);
      } else if (command === "upgrade") {
        runCommand(message, upgradeCommand);
      } else if (command === "list" || command === "ls") {
        runCommand(message, listCommand);
      } else if (command === "doctor") {
        runCommand(message, doctorCommand);
      } else if (command === "info") {
        runCommand(message, infoCommand);
      } else if (command === "outdated") {
        runCommand(message, outdatedCommand);
      } else if (command === "env") {
        runCommand(message, envCommand);
      } else if (command === "selftest") {
        runCommand(message, selftestCommand);
      } else if (command === "source") {
        runCommand(message, sourceCommand);
      } else if (command === "index") {
        runCommand(message, indexCommand);
      } else if (command === "search") {
        runCommand(message, searchCommand);
      } else if (command === "fetch") {
        runCommand(message, fetchCommand);
      } else if (command === "run") {
        runCommand(message, runPackage);
      } else if (command === "shell") {
        runCommand(message, shellPackage);
      } else {
        send({ v: 1, kind: "evt", id: message.id, seq: 1, data: { type: "done", ok: false, exitCode: 2, error: { code: "ERR_UNKNOWN_COMMAND", message: "pkgmgr has no command " + command } } });
      }
    }
  },
});
