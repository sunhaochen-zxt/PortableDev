const net = require("net");

const BS = String.fromCharCode(92);
const pipe = BS + BS + "." + BS + "pipe" + BS + (process.env.PD_PIPE || "");
const name = process.env.PD_EXTENSION || "echo";

const socket = net.connect(pipe);
let buffer = Buffer.alloc(0);
const pendingPrompts = new Map();
let dead = false;

function send(obj) {
  const body = Buffer.from(JSON.stringify(obj), "utf8");
  const head = Buffer.alloc(4);
  head.writeUInt32LE(body.length, 0);
  socket.write(Buffer.concat([head, body]));
}

socket.on("connect", () => {
  send({ v: 1, kind: "hello", data: { protocol: 1, role: "extension", name, version: "0.0.1", features: [], invocationId: process.env.PD_INVOCATION_ID || "" } });
});

socket.on("data", (chunk) => {
  buffer = Buffer.concat([buffer, chunk]);
  while (buffer.length >= 4) {
    const size = buffer.readUInt32LE(0);
    if (buffer.length < 4 + size) break;
    const body = buffer.subarray(4, 4 + size).toString("utf8");
    buffer = buffer.subarray(4 + size);
    let message;
    try { message = JSON.parse(body); } catch { continue; }
    handle(message);
  }
});

socket.on("error", (err) => {
  console.error("echo: " + err.message);
  process.exit(2);
});

function handle(message) {
  if (dead) {
    return;
  }

  if (message.kind === "ctl" && message.data && message.data.action === "ping") {
    send({ v: 1, kind: "ctl", data: { action: "pong", ts: message.data.ts } });
    return;
  }

  if (message.kind === "hello.ok") {
    send({ v: 1, kind: "ext.ready", data: { name, version: "0.0.1", protocol: 1, capabilities: ["echo.echo"] } });
    return;
  }

  if (message.kind === "ctl") {
    const action = message.data && message.data.action;
    if (action === "prompt_reply") {
      const entry = pendingPrompts.get(message.data.promptId);
      if (entry) {
        pendingPrompts.delete(message.data.promptId);
        entry.finish(message.data.value);
      }
    }
    return;
  }

  if (message.kind !== "ext.invoke") return;

  const id = message.id;
  const argv = (message.data && message.data.argv) || [];
  const first = String(argv[0] || "");
  let seq = 0;
  const emit = (type, data) => send({ v: 1, kind: "evt", id, seq: ++seq, data: Object.assign({ type }, data) });

  if (first === "crash") {
    setTimeout(() => process.exit(1), 50);
    return;
  }

  if (first === "hang") {
    dead = true;
    return;
  }

  if (first === "slow") {
    setTimeout(() => emit("done", { ok: true, exitCode: 0, summary: "slow" }), 6500);
    return;
  }

  if (first === "sleep") {
    setTimeout(() => emit("done", { ok: true, exitCode: 0, summary: "slept" }), 1200);
    return;
  }

  if (first === "ask") {
    const promptId = "p-" + Math.random().toString(36).slice(2, 8);
    emit("prompt", { promptId, kind: "confirm", message: "continue?", default: false });
    pendingPrompts.set(promptId, {
      finish: (value) => emit("done", { ok: true, exitCode: 0, summary: "reply=" + JSON.stringify(value) }),
    });
    return;
  }

  const proxy = process.env.HTTP_PROXY || "absent";
  emit("step", { stepId: "echo", title: "echo", state: "running" });
  emit("progress", { task: "echo", pct: 50, label: first });
  emit("log", { level: "info", msg: "echo " + first });
  emit("done", { ok: true, exitCode: 0, summary: "echo " + first + " proxy=" + proxy });
}
