// Reference log provider: one JSONL file per unit, size rotation, index-free seq scan.
const fs = require("fs");
const path = require("path");
const os = require("os");
const { connect, ready, emit, parseFlags, priOf, levelName } = require("./protocol");

const STATE = process.env.PD_STATE || "state";
const ROOT = path.join(STATE, "logs", "providers", "log");
const BOOT = process.env.PD_BOOT_ID || "";
fs.mkdirSync(ROOT, { recursive: true });

const settings = readSettings();
const MAX_BYTES = (settings.maxSizeMB || 50) * 1024 * 1024;
const KEEP_FILES = settings.keepFiles || 10;
const RETENTION_DAYS = settings.retentionDays || 30;
const AUDIT_RETENTION_DAYS = settings.auditRetentionDays || 7;
const AUDIT = path.join(ROOT, "audit");
fs.mkdirSync(AUDIT, { recursive: true });
let auditSeq = 0;
const seqByUnit = new Map();
const followTimers = new Map();

function readSettings() {
  try {
    const root = process.env.PD_ROOT || ".";
    const text = fs.readFileSync(path.join(root, "config", "settings.json"), "utf8");
    return (JSON.parse(text).log) || {};
  } catch { return {}; }
}

function unitFile(unit) { return path.join(ROOT, safe(unit) + ".jsonl"); }
function rotated(unit) { return path.join(ROOT, safe(unit) + ".<n>.jsonl"); }
function safe(unit) { return String(unit || "kernel").replace(/[^a-z0-9._-]/gi, "_"); }

function nextSeq(unit) {
  if (!seqByUnit.has(unit)) {
    let seq = 0;
    const files = allFiles(unit);
    for (const file of files) {
      try {
        const lines = fs.readFileSync(file, "utf8").trim().split("\n");
        const last = JSON.parse(lines[lines.length - 1] || "{}");
        if (typeof last.seq === "number" && last.seq > seq) seq = last.seq;
      } catch { /* ignore */ }
    }
    seqByUnit.set(unit, seq);
  }
  return seqByUnit.get(unit);
}

function allFiles(unit) {
  const files = [];
  for (let n = KEEP_FILES; n >= 1; n--) {
    const file = path.join(ROOT, safe(unit) + "." + n + ".jsonl");
    if (fs.existsSync(file)) files.push(file);
  }
  const current = unitFile(unit);
  if (fs.existsSync(current)) files.push(current);
  return files;
}

function rotate(unit) {
  for (let n = KEEP_FILES; n >= 1; n--) {
    const source = n === 1 ? unitFile(unit) : path.join(ROOT, safe(unit) + "." + (n - 1) + ".jsonl");
    const target = path.join(ROOT, safe(unit) + "." + n + ".jsonl");
    if (fs.existsSync(source)) fs.renameSync(source, target);
  }
}

function append(unit, records) {
  if (!records.length) return;
  const file = unitFile(unit);
  let seq = nextSeq(unit);
  const lines = records.map((record) => {
    seq += 1;
    return JSON.stringify(Object.assign({}, record, { seq: record.seq || seq, _unit: unit }));
  }).join("\n") + "\n";
  const size = fs.existsSync(file) ? fs.statSync(file).size : 0;
  if (size > 0 && size + Buffer.byteLength(lines) > MAX_BYTES) rotate(unit);
  fs.appendFileSync(file, lines);
  seqByUnit.set(unit, seq);
}

function readRecords(unit) {
  const out = [];
  for (const file of allFiles(unit)) {
    let text = "";
    try { text = fs.readFileSync(file, "utf8"); } catch { continue; }
    for (const line of text.split("\n")) {
      if (!line.trim()) continue;
      try { out.push(JSON.parse(line)); } catch { /* half-written line */ }
    }
  }
  return out;
}

function units() {
  const set = new Set();
  for (const name of fs.readdirSync(ROOT)) {
    const match = name.match(/^(.*?)(?:\.\d+)?\.jsonl$/);
    if (match) set.add(match[1]);
  }
  return [...set].sort();
}

function matches(record, flags) {
  if (flags.unit && record._unit !== flags.unit) return false;
  if (flags.level && record.pri > priOf(flags.level)) return false;
  if (flags.grep && !JSON.stringify(record).includes(flags.grep)) return false;
  if (flags.pkg && record.pkg !== flags.pkg) return false;
  if (flags.since && !afterSince(record, flags.since)) return false;
  if (flags.until && !beforeUntil(record, flags.until)) return false;
  return true;
}

function afterSince(record, value) {
  if (value.includes("/")) {
    const [boot, unit, seq] = value.split("/");
    if (record._bootId !== boot) return false;
    if (unit && record._unit !== unit) return false;
    return (record.seq || 0) > Number(seq || 0);
  }
  const cutoff = cutoffTime(value);
  return cutoff === null ? true : Date.parse(record.ts) >= cutoff;
}

function beforeUntil(record, value) {
  const cutoff = cutoffTime(value);
  return cutoff === null ? true : Date.parse(record.ts) <= cutoff;
}

function cutoffTime(value) {
  const relative = /^(\d+)([smhd])$/.exec(value);
  if (relative) {
    const n = Number(relative[1]);
    const factor = { s: 1000, m: 60000, h: 3600000, d: 86400000 }[relative[2]];
    return Date.now() - n * factor;
  }
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? null : parsed;
}

function query(flags) {
  let records = [];
  for (const unit of units()) {
    if (flags.unit && unit !== flags.unit) continue;
    records = records.concat(readRecords(unit).filter((r) => matches(r, flags)));
  }
  records.sort((a, b) => (Date.parse(a.ts) - Date.parse(b.ts)) || ((a.seq || 0) - (b.seq || 0)));
  const tail = flags.tail ? Number(flags.tail) : 50;
  return records.slice(Math.max(0, records.length - tail));
}

function diskUsage() {
  return units().map((unit) => {
    let bytes = 0;
    let files = 0;
    for (const file of allFiles(unit)) {
      try { bytes += fs.statSync(file).size; files += 1; } catch { /* ignore */ }
    }
    return [unit, bytes, files];
  });
}

function vacuum(flags) {
  const keep = flags.keep ? Number(flags.keep) : KEEP_FILES;
  const before = flags.before ? cutoffTime(flags.before) : Date.now() - RETENTION_DAYS * 86400000;
  let freed = 0;
  for (const unit of units()) {
    const files = [];
    for (let n = 1; n <= KEEP_FILES; n++) {
      const file = path.join(ROOT, safe(unit) + "." + n + ".jsonl");
      if (fs.existsSync(file)) files.push({ file, n, mtime: fs.statSync(file).mtimeMs });
    }
    files.sort((a, b) => a.mtime - b.mtime);
    const remove = files.filter((entry, index) => index < files.length - keep || entry.mtime < before);
    for (const entry of remove) {
      try { freed += fs.statSync(entry.file).size; fs.unlinkSync(entry.file); } catch { /* ignore */ }
    }
  }
  return freed;
}

function startFollow(send, id, flags) {
  const offsets = new Map();
  const timer = setInterval(() => {
    try {
      for (const unit of units()) {
        if (flags.unit && unit !== flags.unit) continue;
        const file = unitFile(unit);
        if (!fs.existsSync(file)) continue;
        const size = fs.statSync(file).size;
        const previous = offsets.get(file) || 0;
        if (size <= previous) continue;
        const fd = fs.openSync(file, "r");
        try {
          const buffer = Buffer.alloc(size - previous);
          fs.readSync(fd, buffer, 0, buffer.length, previous);
          offsets.set(file, size);
          for (const line of buffer.toString("utf8").split("\n")) {
            if (!line.trim()) continue;
            let record;
            try { record = JSON.parse(line); } catch { continue; }
            if (matches(record, flags)) emit(send, id, 0, "log", { record });
          }
        } finally { fs.closeSync(fd); }
      }
    } catch { /* keep following */ }
  }, 250);
  followTimers.set(id, timer);
}

function auditFile() {
  return path.join(AUDIT, new Date().toISOString().slice(0, 7) + ".jsonl");
}

// Durable append: call.ok is only sent after fsyncSync returns.
function auditAppend(records) {
  if (!records || records.length === 0) {
    return { count: 0, lastSeq: auditSeq };
  }
  const lines = [];
  for (const record of records) {
    auditSeq += 1;
    lines.push(JSON.stringify(Object.assign({}, record, { seq: auditSeq })));
  }
  const fd = fs.openSync(auditFile(), "a");
  try {
    fs.writeSync(fd, lines.join("\n") + "\n");
    fs.fsyncSync(fd);
  } finally {
    fs.closeSync(fd);
  }
  return { count: records.length, lastSeq: auditSeq };
}

function auditQuery(args) {
  const task = String((args && args.task) || "");
  const entries = [];
  for (const name of fs.readdirSync(AUDIT).sort()) {
    if (!name.endsWith(".jsonl")) {
      continue;
    }
    for (const line of fs.readFileSync(path.join(AUDIT, name), "utf8").split("\n")) {
      if (!line.trim()) {
        continue;
      }
      try {
        const entry = JSON.parse(line);
        if (!task || entry.task === task) {
          entries.push(entry);
        }
      } catch (error) {
        // skip a half-written line
      }
    }
  }
  entries.sort((a, b) => (a.seq || 0) - (b.seq || 0));
  const hasBegin = entries.some((entry) => entry.phase === "begin");
  return { entries, truncated: entries.length > 0 && !hasBegin, oldestTs: entries.length > 0 ? entries[0].ts : null };
}

function auditVacuum(args) {
  const days = (args && args.retentionDays) || AUDIT_RETENTION_DAYS;
  const cutoff = Date.now() - days * 86400000;
  let removed = 0;
  for (const name of fs.readdirSync(AUDIT)) {
    if (!name.endsWith(".jsonl")) {
      continue;
    }
    const full = path.join(AUDIT, name);
    try {
      if (fs.statSync(full).mtimeMs < cutoff) {
        fs.unlinkSync(full);
        removed += 1;
      }
    } catch (error) {
      // another writer may hold it; try again next vacuum
    }
  }
  return { removed };
}

function handleAuditCall(message, send) {
  const id = message.id;
  const data = message.data || {};
  const op = data.op || "";
  const args = data.args || {};
  try {
    let result;
    if (op === "append") {
      result = auditAppend(args.records || []);
    } else if (op === "query") {
      result = auditQuery(args);
    } else if (op === "vacuum") {
      result = auditVacuum(args);
    } else {
      send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_UNSUPPORTED", message: "unknown log.audit op " + op } } });
      return;
    }

    send({ v: 1, kind: "call.ok", id, data: { result } });
  } catch (error) {
    send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_IO", message: error.message || String(error) } } });
  }
}

connect({
  onMessage(message, send) {
    if (message.kind === "hello.ok") {
      ready(send, [
        { name: "log.ingest", version: 1, mutates: false },
        { name: "log.query", version: 1, mutates: false },
        { name: "log.audit", version: 1, mutates: true },
      ]);
      return;
    }

    if (message.kind === "call") {
      handleAuditCall(message, send);
      return;
    }

    if (message.kind === "ctl") {
      const action = message.data && message.data.action;
      if (action === "log.write") {
        const byUnit = new Map();
        for (const record of (message.data.records || [])) {
          const unit = record._unit || "kernel";
          if (!byUnit.has(unit)) byUnit.set(unit, []);
          byUnit.get(unit).push(record);
        }
        for (const [unit, records] of byUnit) append(unit, records);
      } else if (action === "log.config") {
        // accepted for forward compatibility
      } else if (action === "cancel") {
        // Stop following; the kernel publishes the terminal done for a cancelled task.
        const key = (message.data && message.data.id) || message.id;
        const timer = followTimers.get(key);
        if (timer) { clearInterval(timer); followTimers.delete(key); }
      }
      return;
    }

    if (message.kind !== "ext.invoke") return;

    const id = message.id;
    const argv = (message.data && message.data.argv) || [];
    const { flags } = parseFlags(argv);
    let seq = 0;
    const push = (type, data) => emit(send, id, ++seq, type, data);
    const done = (payload) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) });

    try {
      if (flags.since && !String(flags.since).includes("/") && cutoffTime(flags.since) === null) {
        send({ v: 1, kind: "evt", id, seq: ++seq, data: { type: "done", ok: false, exitCode: 2, error: { code: "ERR_BAD_ARGS", message: "bad --since" } } });
        return;
      }

      if (flags["disk-usage"]) {
        const rows = diskUsage();
        push("table", { title: "log disk usage", columns: [{ key: "unit", title: "unit" }, { key: "bytes", title: "bytes" }, { key: "files", title: "files" }], rows });
        done({ summary: rows.length + " units" });
        return;
      }

      if (flags.vacuum) {
        const freed = vacuum(flags);
        done({ summary: "vacuum freed " + freed + " bytes", artifacts: [{ kind: "bytes", value: freed }] });
        return;
      }

      const records = query(flags);
      for (const record of records) push("log", { record });
      if (flags.follow) {
        // No done yet: the task stays open until the kernel cancels it (spec 4.7).
        startFollow(send, id, flags);
        return;
      }

      done({ summary: records.length + " records" });
    } catch (error) {
      send({ v: 1, kind: "evt", id, seq: ++seq, data: { type: "done", ok: false, exitCode: 1, error: { code: "ERR_INTERNAL", message: String(error) } } });
    }
  },
}).send;
