// Alternate log provider: one flat file, no rotation, no index. Deliberately different storage,
// same contract (ingest + log command). Proves the kernel does not depend on storage layout.
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { connect, ready, emit, parseFlags, priOf, levelName } = require("./protocol");

const STATE = process.env.PD_STATE || "state";
const ROOT = path.join(STATE, "logs", "providers", "alt");
fs.mkdirSync(ROOT, { recursive: true });
const FILE = path.join(ROOT, "records.jsonl");
let seq = countLines();
const followTimers = new Set();
const AUDIT = path.join(ROOT, "audit.jsonl");
let auditSeq = 0;

function auditLastSeq() {
  try {
    const text = fs.readFileSync(AUDIT, "utf8").replace(/\s+$/, "");
    const lines = text.split("\n");
    for (let index = lines.length - 1; index >= 0; index -= 1) {
      try {
        const parsed = JSON.parse(lines[index]);
        if (typeof parsed.seq === "number") { return parsed.seq; }
      } catch (error) { /* skip a partial line */ }
    }
  } catch (error) { /* first boot */ }
  return 0;
}

function auditTaskMarker(task) {
  const digest = crypto.createHash("sha256").update(String(task)).digest("hex").slice(0, 24);
  return path.join(ROOT, "tasks", digest + ".json");
}

auditSeq = auditLastSeq();

function auditAppend(records) {
  if (!records || records.length === 0) {
    return { count: 0, lastSeq: auditSeq };
  }
  const lines = [];
  for (const record of records) {
    auditSeq += 1;
    lines.push(JSON.stringify(Object.assign({}, record, { seq: auditSeq })));
  }
  const fd = fs.openSync(AUDIT, "a");
  try {
    fs.writeSync(fd, lines.join("\n") + "\n");
    fs.fsyncSync(fd);
  } finally {
    fs.closeSync(fd);
  }
  for (const record of records) {
    const task = record && record.task != null && String(record.task).length > 0 ? String(record.task) : null;
    if (!task) { continue; }
    try {
      const markerPath = auditTaskMarker(task);
      fs.mkdirSync(path.dirname(markerPath), { recursive: true });
      let marker = { schema: 1, task: task, first: null, last: null, count: 0 };
      try { marker = JSON.parse(fs.readFileSync(markerPath, "utf8")); } catch (error) { /* first append */ }
      marker.count = (marker.count || 0) + 1;
      marker.last = record.ts || marker.last || null;
      if (!marker.first) { marker.first = marker.last; }
      fs.writeFileSync(markerPath, JSON.stringify(marker));
    } catch (error) { /* markers are best effort */ }
  }

  return { count: records.length, lastSeq: auditSeq };
}

function auditQuery(args) {
  const task = String((args && args.task) || "");
  const entries = [];
  let text = "";
  try {
    text = fs.readFileSync(AUDIT, "utf8");
  } catch (error) { /* no audit file: marker below distinguishes expired from never-audited */ }
  for (const line of text.split("\n")) {
    if (!line.trim()) {
      continue;
    }
    try {
      const entry = JSON.parse(line);
      if (!task || entry.task === task) {
        entries.push(entry);
      }
    } catch (error) {
      // skip
    }
  }
  entries.sort((a, b) => (a.seq || 0) - (b.seq || 0));
  const hasBegin = entries.some((entry) => entry.phase === "begin");
  let truncated = entries.length > 0 && !hasBegin;
  let oldestTs = entries.length > 0 ? entries[0].ts : null;
  if (entries.length === 0 && task) {
    try {
      const marker = JSON.parse(fs.readFileSync(auditTaskMarker(task), "utf8"));
      truncated = true;
      oldestTs = marker.first || null;
    } catch (error) { /* never audited: a genuine no-op */ }
  }
  return { entries, truncated, oldestTs };
}

function handleCall2(message, send) {
  const id = message.id;
  const data = message.data || {};
  const args = data.args || {};
  try {
    let result;
    if ((data.op || "") === "append") {
      result = auditAppend(args.records || []);
    } else if ((data.op || "") === "query") {
      result = auditQuery(args);
    } else if ((data.op || "") === "vacuum") {
      result = { removed: 0 };
    } else {
      send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_UNSUPPORTED", message: "unknown log.audit op " + (data.op || "") } } });
      return;
    }
    send({ v: 1, kind: "call.ok", id, data: { result } });
  } catch (error) {
    send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_IO", message: error.message || String(error) } } });
  }
}



function countLines() {
  try { return fs.readFileSync(FILE, "utf8").split("\n").filter(Boolean).length; } catch { return 0; }
}

function append(records) {
  if (!records.length) return;
  const lines = records.map((r) => { seq += 1; return JSON.stringify(Object.assign({}, r, { seq })); }).join("\n") + "\n";
  fs.appendFileSync(FILE, lines);
}

function readAll() {
  const out = [];
  let text = "";
  try { text = fs.readFileSync(FILE, "utf8"); } catch { return out; }
  for (const line of text.split("\n")) {
    if (!line.trim()) continue;
    try { out.push(JSON.parse(line)); } catch { /* half line */ }
  }
  return out;
}

function cutoff(value) {
  const m = /^(\d+)([smhd])$/.exec(value || "");
  if (m) {
    const n = Number(m[1]);
    const f = { s: 1000, m: 60000, h: 3600000, d: 86400000 }[m[2]];
    return Date.now() - n * f;
  }
  const t = Date.parse(value);
  return Number.isNaN(t) ? null : t;
}

function matches(r, flags) {
  if (flags.unit && r._unit !== flags.unit) return false;
  if (flags.level && r.pri > priOf(flags.level)) return false;
  if (flags.grep && !JSON.stringify(r).includes(flags.grep)) return false;
  if (flags.pkg && r.pkg !== flags.pkg) return false;
  if (flags.since) {
    if (String(flags.since).includes("/")) {
      const [boot, unit, s] = String(flags.since).split("/");
      if (r._bootId !== boot || (unit && r._unit !== unit) || (r.seq || 0) <= Number(s || 0)) return false;
    } else {
      const c = cutoff(flags.since);
      if (c !== null && Date.parse(r.ts) < c) return false;
    }
  }
  if (flags.until) {
    const c = cutoff(flags.until);
    if (c !== null && Date.parse(r.ts) > c) return false;
  }
  return true;
}

function query(flags) {
  const all = readAll().filter((r) => matches(r, flags));
  all.sort((a, b) => (Date.parse(a.ts) - Date.parse(b.ts)) || ((a.seq || 0) - (b.seq || 0)));
  const tail = flags.tail ? Number(flags.tail) : 50;
  return all.slice(Math.max(0, all.length - tail));
}

function follow(send, id, flags) {
  let offset = 0;
  try { offset = fs.statSync(FILE).size; } catch { offset = 0; }
  const timer = setInterval(() => {
    try {
      const size = fs.statSync(FILE).size;
      if (size <= offset) return;
      const fd = fs.openSync(FILE, "r");
      try {
        const buffer = Buffer.alloc(size - offset);
        fs.readSync(fd, buffer, 0, buffer.length, offset);
        offset = size;
        for (const line of buffer.toString("utf8").split("\n")) {
          if (!line.trim()) continue;
          let r; try { r = JSON.parse(line); } catch { continue; }
          if (matches(r, flags)) emit(send, id, 0, "log", { record: r });
        }
      } finally { fs.closeSync(fd); }
    } catch { /* keep following */ }
  }, 250);
  followTimers.add(timer);
  return timer;
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
      handleCall2(message, send);
      return;
    }

    if (message.kind === "ctl") {
      const action = message.data && message.data.action;
      if (action === "log.write") append(message.data.records || []);
      if (action === "cancel") {
        for (const timer of followTimers) clearInterval(timer);
        followTimers.clear();
      }
      return;
    }

    if (message.kind !== "ext.invoke") return;
    const id = message.id;
    const { flags } = parseFlags((message.data && message.data.argv) || []);
    let n = 0;
    const push = (type, data) => emit(send, id, ++n, type, data);
    const done = (payload) => send({ v: 1, kind: "evt", id, seq: ++n, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) });

    if (flags.since && !String(flags.since).includes("/") && cutoff(flags.since) === null) {
      send({ v: 1, kind: "evt", id, seq: ++n, data: { type: "done", ok: false, exitCode: 2, error: { code: "ERR_BAD_ARGS", message: "bad --since" } } });
      return;
    }

    if (flags["disk-usage"]) {
      const units = [...new Set(readAll().map((r) => r._unit))];
      let bytes = 0; try { bytes = fs.statSync(FILE).size; } catch { bytes = 0; }
      push("table", { title: "log disk usage", columns: [{ key: "unit", title: "unit" }, { key: "bytes", title: "bytes" }], rows: units.map((u) => [u, bytes]) });
      done({ summary: units.length + " units" });
      return;
    }

    if (flags.vacuum) {
      done({ summary: "vacuum freed 0 bytes" });
      return;
    }

    for (const r of query(flags)) push("log", { record: r });
    if (flags.follow) { follow(send, id, flags); return; }
    done({ summary: "ok" });
  },
}).send;
