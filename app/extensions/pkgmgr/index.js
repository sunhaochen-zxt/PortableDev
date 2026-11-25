// Package manager: owns the install flow. Storage still executes every disk effect.
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { connect, ready } = require("./protocol");

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
    }, timeoutMs || 120000);
    pendingCalls.set(id, { resolve, reject, timer });
    send({ v: 1, kind: "call", id, data: { cap: capability, op, args } });
  });
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
    send({ v: 1, kind: "ctl", id: taskId, data: { action: "grant.request", kind: "dir", hint, mode: "rw", ttl: "10m" } });
  });
}

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
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith("--")) {
        flags[arg.slice(2)] = next;
        i += 1;
      } else {
        flags[arg.slice(2)] = true;
      }
    } else {
      positional.push(arg);
    }
  }
  return { flags, positional };
}

const PACKAGE_ID = /^[a-z][a-z0-9-]{0,63}$/;
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

async function install(message) {
  const id = message.id;
  const argv = (message.data && message.data.argv) || [];
  const { flags, positional } = parseFlags(argv);
  let seq = 0;
  const event = (type, data) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type }, data) });
  const done = (payload) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) });
  const fail = (error) => send({ v: 1, kind: "evt", id, seq: ++seq, data: { type: "done", ok: false, exitCode: error.exitCode || 1, error: { code: error.code || "ERR_INTERNAL", message: error.message || String(error) } } });

  const target = positional[0];
  // Relative paths come from the caller's shell, not from this process (spec 3.5 context snapshot).
  const clientCwd = (message.data && message.data.ctx && message.data.ctx.cwd) || process.cwd();
  const resolveFrom = (value) => (path.isAbsolute(String(value)) ? path.resolve(String(value)) : path.resolve(clientCwd, String(value)));
  if (!target) {
    fail(new PkgError("ERR_BAD_ARGS", "install needs a target: package file, directory or URL", 2));
    return;
  }

  let grantPath = null;
  const cleanupGrant = async () => {
    if (!grantPath) {
      return;
    }
    const staging = grantPath;
    grantPath = null;
    try {
      await callCapability("storage.adopt", "abort", { task: id, stagingPath: staging });
    } catch (error) {
      event("log", { level: "warn", msg: "grant cleanup failed: " + (error.message || String(error)) });
    }
  };

  try {
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

    if (/^https?:\/\//i.test(source)) {
      if (!flags.sha256) {
        throw new PkgError("ERR_UNSUPPORTED", "installing from a URL needs --sha256 (source index is not implemented yet)", 11);
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
      } else {
        if (String(target).toLowerCase().endsWith(".pdpkg")) {
          throw new PkgError("ERR_PKG_NOT_FOUND", "no such package file: " + full, 20);
        }
        throw new PkgError("ERR_UNSUPPORTED", "installing by package name needs an index (not implemented yet); pass a .pdpkg, a directory or a URL", 11);
      }
    }

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

    // A local archive with --sha256 must actually match; the flag used to be silently ignored.
    if (kind === "archive" && flags.sha256) {
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
      event("kv", { title: "install plan", items: { package: pkgId, version, source: label } });
      await cleanupGrant();
      done({ summary: "dry-run: would install " + pkgId + " " + version, metrics: { dryRun: true } });
      return;
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
        });
        const extracted = await callCapability("unpack.extract", "extract", {
          archive: blob.path,
          dest: path.join(grantPath, "content"),
          kind: (manifest.unpack && manifest.unpack.kind) || "auto",
          strip: (manifest.unpack && manifest.unpack.strip) || 0,
        });
        contentRoot = extracted.dest;
        event("step", { stepId: "fetch", title: "fetch", state: "done" });
      } else {
        throw new PkgError("ERR_PKG_INVALID", "package has neither files/ nor source.url", 3);
      }
    }

    event("step", { stepId: "assert", title: "assert", state: "running" });
    assertFilesExist(contentRoot, manifest.asserts);
    event("step", { stepId: "assert", title: "assert", state: "done" });

    const wiring = {
      path: manifest.path || [],
      env: manifest.env || {},
      registry: manifest.registry || [],
      launch: manifest.launch || [],
    };

    event("step", { stepId: "adopt", title: "adopt", state: "running" });
    const commit = await callCapability("storage.adopt", "commit", {
      task: id,
      pkg: pkgId,
      version,
      stagingPath: contentRoot,
      contentHash: (manifest.hashes && manifest.hashes.archive) || (manifest.source && manifest.source.sha256) || undefined,
      origin: kind === "dir" ? "local" : "archive",
      wiring,
    });
    event("step", { stepId: "adopt", title: "adopt", state: "done" });

    event("step", { stepId: "wire", title: "wire", state: "running" });
    const current = await callCapability("storage.current", "set", { task: id, pkg: pkgId, storePath: commit.storePath });
    const wired = await callCapability("storage.wire", "apply", { task: id, only: pkgId });
    event("step", { stepId: "wire", title: "wire", state: "done" });

    try {
      await callCapability("storage.ledger", "commit", { task: id });
    } catch (error) {
      event("log", { level: "warn", msg: "ledger commit failed: " + (error.message || String(error)) });
    }
    await cleanupGrant();

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
        task: id,
      },
    });
  } catch (error) {
    try {
      await callCapability("rollback.run", "run", { task: id });
    } catch (rollbackError) {
      event("log", { level: "warn", msg: "rollback failed: " + (rollbackError.message || String(rollbackError)) });
    }
    await cleanupGrant();
    fail(error);
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
      const command = (message.data && message.data.cmd) || argv[0] || "";
      if (command === "install") {
        install(message);
      } else {
        send({ v: 1, kind: "evt", id: message.id, seq: 1, data: { type: "done", ok: false, exitCode: 2, error: { code: "ERR_UNKNOWN_COMMAND", message: "pkgmgr has no command " + command } } });
      }
    }
  },
});
