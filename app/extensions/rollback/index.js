// Rollback extension: reads the durable audit stream and replays each entry's undo.
// It knows nothing about business operations; it only executes {cap, op, args} instructions.
const { connect, ready, emit } = require("./protocol");

const pendingCalls = new Map();
let callCounter = 0;
let send = () => {};

function callCapability(capability, op, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    callCounter += 1;
    const id = "b" + callCounter;
    const timer = setTimeout(() => {
      pendingCalls.delete(id);
      const error = new Error("call timeout: " + capability + "/" + op);
      error.code = "ERR_TIMEOUT";
      reject(error);
    }, timeoutMs || 30000);
    pendingCalls.set(id, { resolve, reject, timer });
    send({ v: 1, kind: "call", id, data: { cap: capability, op, args } });
  });
}

function exitForCode(code) {
  switch (code) {
    case "ERR_BAD_ARGS":
      return 2;
    case "ERR_PKG_INVALID":
    case "ERR_HASH":
      return 3;
    case "ERR_UNSUPPORTED":
      return 11;
    case "ERR_PKG_NOT_FOUND":
    case "ERR_AUDIT_EXPIRED":
      return 20;
    case "ERR_TIMEOUT":
    case "ERR_EXT_TIMEOUT":
      return 25;
    default:
      return 1;
  }
}

function fail(code, message) {
  const error = new Error(message);
  error.code = code;
  throw error;
}

async function plan(task) {
  if (!task) {
    fail("ERR_BAD_ARGS", "rollback needs a non-empty --task");
  }

  const result = await callCapability("log.audit", "query", { task });
  if (result.truncated === true) {
    fail("ERR_AUDIT_EXPIRED", "audit entries for " + task + " were vacuumed; cannot roll back");
  }

  const entries = result.entries || [];
  const doneRefs = new Set(
    entries.filter((entry) => entry.op === "rollback.step" && entry.phase === "done" && entry.ref).map((entry) => entry.ref)
  );

  const actions = [];
  const skipped = [];
  for (let index = entries.length - 1; index >= 0; index -= 1) {
    const entry = entries[index];
    if (entry.phase || !entry.op || String(entry.op).startsWith("rollback.")) {
      continue;
    }

    if (!entry.undo) {
      skipped.push({ id: entry.id, op: entry.op, reason: "entry has no undo instruction" });
      continue;
    }

    if (entry.id && doneRefs.has(entry.id)) {
      continue;
    }

    actions.push({ entry, undo: entry.undo });
  }

  return { actions, skipped };
}

async function run(task, dryRun, onEvent) {
  const planned = await plan(task);
  if (dryRun) {
    return {
      task,
      planned: planned.actions.map((action) => ({ op: action.entry.op, undo: action.undo })),
      skipped: planned.skipped,
      rolledBack: 0,
      failed: 0,
    };
  }

  const rolledBack = [];
  const failed = [];
  for (let index = 0; index < planned.actions.length; index += 1) {
    const action = planned.actions[index];
    try {
      const undo = action.undo;
      if (!undo || typeof undo.cap !== "string" || typeof undo.op !== "string" || !/^storage\.[a-z]+$/.test(undo.cap)) {
        failed.push({ op: action.entry.op, code: "ERR_UNSUPPORTED", message: "undo outside storage.* is not allowed" });
        break;
      }

      await callCapability(undo.cap, undo.op, undo.args || {});
      await callCapability("log.audit", "append", {
        stream: "audit",
        task,
        records: [
          {
            ts: new Date().toISOString(),
            task,
            op: "rollback.step",
            phase: "done",
            ref: action.entry.id,
            entryOp: action.entry.op,
          },
        ],
      });
      rolledBack.push(action.entry.op);
      if (onEvent) {
        onEvent("step", { stepId: "undo-" + index, title: "undo " + action.entry.op, state: "done" });
      }
    } catch (error) {
      failed.push({ op: action.entry.op, code: error.code || "ERR_IO", message: error.message || String(error) });
      break;
    }
  }

  return { task, rolledBack: rolledBack.length, skipped: planned.skipped, failed: failed.length, failures: failed };
}

async function handleCommand(message) {
  const id = message.id;
  const argv = (message.data && message.data.argv) || [];
  let seq = 0;
  const event = (type, data) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type }, data) });
  const done = (payload) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) });
  const failDone = (code, text, exitCode) => send({ v: 1, kind: "evt", id, seq: ++seq, data: { type: "done", ok: false, exitCode: exitCode || 1, error: { code, message: text } } });

  let task = null;
  let dryRun = false;
  let generation = null;
  let list = false;
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--task" && i + 1 < argv.length) {
      task = String(argv[i + 1]);
      i += 1;
    } else if (argv[i] === "--to" && i + 1 < argv.length) {
      generation = String(argv[i + 1]);
      i += 1;
    } else if (argv[i] === "--list") {
      list = true;
    } else if (argv[i] === "--dry-run" || argv[i] === "--plan") {
      dryRun = true;
    }
  }

  if (list) {
    try {
      const result = await callCapability("storage.generation", "list", {});
      const rows = (result.items || []).map((item) => [String(item.seq), item.at, (item.packages || []).join(", ")]);
      send({
        v: 1,
        kind: "evt",
        id,
        seq: ++seq,
        data: {
          type: "table",
          title: "generations",
          columns: [{ key: "seq", title: "序号" }, { key: "at", title: "时间" }, { key: "packages", title: "包" }],
          rows,
        },
      });
      done({ summary: rows.length + " generations", metrics: { generations: rows.length } });
    } catch (error) {
      failDone(error.code || "ERR_IO", error.message || String(error), exitForCode(error.code));
    }
    return;
  }

  if (generation !== null) {
    try {
      if (dryRun) {
        const info = await callCapability("storage.generation", "get", { seq: Number(generation) });
        done({ summary: "would restore generation " + generation, metrics: { dryRun: true, seq: info.seq, packages: (info.packages || []).length } });
      } else {
        const restored = await callCapability("storage.generation", "restore", { seq: Number(generation), task: id });
        done({
          summary: "restored generation " + generation + " (" + restored.packages + " packages, " + restored.changes + " changes)",
          metrics: { seq: restored.seq, packages: restored.packages, changes: restored.changes, newSeq: restored.newSeq },
        });
      }
    } catch (error) {
      failDone(error.code || "ERR_IO", error.message || String(error), exitForCode(error.code));
    }
    return;
  }

  if (!task) {
    failDone("ERR_BAD_ARGS", "rollback needs --task <任务号> or --to <序号>", 2);
    return;
  }

  try {
    event("step", { stepId: "query", title: "read audit stream", state: "running" });
    const result = await run(task, dryRun, event);
    event("step", { stepId: "query", title: "read audit stream", state: "done" });

    if (dryRun) {
      done({
        summary: "plan: " + (result.planned || []).length + " undo actions",
        metrics: { planned: (result.planned || []).length, skipped: (result.skipped || []).length, dryRun: true },
      });
      return;
    }

    done({
      summary: "rolled back " + result.rolledBack + " entries" + (result.failed > 0 ? ", " + result.failed + " failed" : ""),
      metrics: { rolledBack: result.rolledBack, skipped: (result.skipped || []).length, failed: result.failed },
    });
  } catch (error) {
    const code = error.code || "ERR_IO";
    failDone(code, error.message || String(error), exitForCode(code));
  }
}

function handleCall(message) {
  const id = message.id;
  const op = (message.data && message.data.op) || "";
  const args = (message.data && message.data.args) || {};
  if (op !== "run") {
    send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_UNSUPPORTED", message: "unknown rollback op " + op } } });
    return;
  }

  const requestedTask = String((args && args.task) || "");
  if (!args || (!args.seq && requestedTask.length === 0)) {
    send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_BAD_ARGS", message: "rollback.run needs a task or a generation seq" } } });
    return;
  }

  const work = args.seq
    ? callCapability("storage.generation", "restore", { seq: Number(args.seq), task: requestedTask })
    : run(requestedTask, args.dryRun === true, null);

  work
    .then((result) => send({ v: 1, kind: "call.ok", id, data: { result } }))
    .catch((error) => send({ v: 1, kind: "call.err", id, data: { error: { code: error.code || "ERR_IO", message: error.message || String(error) } } }));
}

connect({
  onMessage(message, outbound) {
    send = outbound;
    if (message.kind === "hello.ok") {
      ready(send, [{ name: "rollback.run", version: 1, mutates: true }]);
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

    if (message.kind === "call") {
      handleCall(message);
      return;
    }

    if (message.kind === "ext.invoke") {
      handleCommand(message);
    }
  },
});
