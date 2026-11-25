// Test driver for the storage extension: grant -> write -> adopt -> current -> record.
const fs = require("fs");
const path = require("path");
const { connect, ready, emit } = require("./protocol");

const pendingCalls = new Map();
const pendingGrants = new Map();
let counter = 0;
let send = () => {};

function call(capability, op, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    counter += 1;
    const id = "c" + counter;
    const timer = setTimeout(() => {
      pendingCalls.delete(id);
      reject(new Error("call timeout: " + capability + "/" + op));
    }, timeoutMs || 20000);
    pendingCalls.set(id, { resolve, reject, timer });
    send({ v: 1, kind: "call", id, data: { cap: capability, op, args } });
  });
}

function grant(taskId, hint, ttl) {
  return new Promise((resolve, reject) => {
    const key = String(taskId);
    const timer = setTimeout(() => {
      pendingGrants.delete(key);
      reject(new Error("grant timeout"));
    }, 20000);
    pendingGrants.set(key, { resolve, reject, timer });
    send({ v: 1, kind: "ctl", id: taskId, data: { action: "grant.request", kind: "dir", hint, mode: "rw", ttl: ttl || "10m" } });
  });
}

function writeWorkFiles(dir) {
  fs.writeFileSync(path.join(dir, "a.txt"), "hello from storeprobe\n");
  fs.mkdirSync(path.join(dir, "bin"), { recursive: true });
  fs.writeFileSync(path.join(dir, "bin", "tool.txt"), "tool\n");
}

async function runCommand(message) {
  const id = message.id;
  const argv = (message.data && message.data.argv) || [];
  let seq = 0;
  const event = (type, data) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type }, data) });
  const done = (payload) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) });
  const fail = (code, text) => send({ v: 1, kind: "evt", id, seq: ++seq, data: { type: "done", ok: false, exitCode: 1, error: { code, message: text } } });

  const mode = argv[0] || "run";
  try {
    if (mode === "run") {
      const pkg = argv[1] || "demo";
      const version = argv[2] || "1.0.0";
      event("step", { stepId: "grant", title: "grant", state: "running" });
      const g = await grant(id, "probe");
      event("step", { stepId: "grant", title: "grant", state: "done" });
      writeWorkFiles(g.path);
      event("progress", { task: "adopt", pct: 50, label: "adopt" });
      const commit = await call("storage.adopt", "commit", { task: id, pkg, version, stagingPath: g.path });
      const current = await call("storage.current", "set", { task: id, pkg, storePath: commit.storePath });
      const record = await call("storage.record", "get", { pkg });
      done({
        summary: "adopted " + commit.storePath,
        metrics: {
          task: id,
          storePath: commit.storePath,
          treeHash: commit.treeHash,
          existed: commit.existed === true,
          link: current.link,
          recordVersion: record.version,
        },
      });
      return;
    }

    if (mode === "abort") {
      const g = await grant(id, "probe");
      writeWorkFiles(g.path);
      const result = await call("storage.adopt", "abort", { task: id, stagingPath: g.path });
      done({ summary: "aborted", metrics: { removed: result.removed === true, stagingPath: g.path } });
      return;
    }

    if (mode === "grant-expired") {
      const g = await grant(id, "expired", "1s");
      writeWorkFiles(g.path);
      await new Promise((resolve) => setTimeout(resolve, 1300));
      try {
        await call("storage.adopt", "commit", { task: id, pkg: "expired", version: "1.0.0", stagingPath: g.path });
        fail("ERR_UNEXPECTED", "expired grant was accepted");
      } catch (error) {
        done({ summary: "expired grant rejected", metrics: { code: error.code || "", message: error.message || "" } });
      }
      return;
    }

    if (mode === "gc") {
      const result = await call("storage.gc", "run", {});
      done({ summary: "gc done", metrics: { stagingRemoved: result.stagingRemoved, storeRemoved: result.storeRemoved } });
      return;
    }

    if (mode === "ledger") {
      await call("storage.ledger", "begin", { task: id, op: "install", pkg: "demo" });
      await call("storage.ledger", "commit", { task: id });
      const listed = await call("storage.ledger", "list", { task: id });
      done({ summary: "audit entries: " + listed.entries.length, metrics: { entries: listed.entries.length, truncated: listed.truncated === true } });
      return;
    }

    fail("ERR_BAD_ARGS", "unknown mode " + mode);
  } catch (error) {
    fail(error.code || "ERR_IO", error.message || String(error));
  }
}

const link = connect({
  onMessage(message, outbound) {
    send = outbound;
    if (message.kind === "hello.ok") {
      ready(send, []);
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
            entry.reject(new Error((message.data.error && message.data.error.message) || "grant refused"));
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
          const failure = new Error(error.message || "call failed");
          failure.code = error.code;
          entry.reject(failure);
        }
      }
      return;
    }

    if (message.kind === "ext.invoke") {
      runCommand(message);
    }
  },
});

send = link.send;
