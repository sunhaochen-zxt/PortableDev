// Minimal framing client for the pd-kernel pipe. Shared shape with tests/fixtures/echo-ext.
const net = require("net");

const BS = String.fromCharCode(92);
const PIPE = BS + BS + "." + BS + "pipe" + BS + (process.env.PD_PIPE || "");
const NAME = process.env.PD_EXTENSION || "log";

function connect(handlers) {
  const socket = net.connect(PIPE);
  let buffer = Buffer.alloc(0);

  const send = (obj) => {
    const body = Buffer.from(JSON.stringify(obj), "utf8");
    const head = Buffer.alloc(4);
    head.writeUInt32LE(body.length, 0);
    socket.write(Buffer.concat([head, body]));
  };

  socket.on("connect", () => {
    send({ v: 1, kind: "hello", data: { protocol: 1, role: "extension", name: NAME, version: "0.0.1", features: [], invocationId: process.env.PD_INVOCATION_ID || "" } });
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
      if (message.kind === "ctl" && message.data && message.data.action === "ping") {
        send({ v: 1, kind: "ctl", data: { action: "pong", ts: message.data.ts } });
        continue;
      }
      handlers.onMessage(message, send);
    }
  });

  socket.on("error", (err) => {
    console.error(NAME + ": " + err.message);
    process.exit(2);
  });

  return { send };
}

function ready(send, capabilities) {
  send({ v: 1, kind: "ext.ready", data: { name: NAME, version: "0.0.1", protocol: 1, capabilities } });
}

function emit(send, id, seq, type, data) {
  send({ v: 1, kind: "evt", id, seq, data: Object.assign({ type }, data) });
}

function parseFlags(argv) {
  const flags = {};
  const positionals = [];
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg.startsWith("--")) {
      const key = arg.slice(2);
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith("--")) { flags[key] = next; i++; }
      else flags[key] = true;
    } else {
      positionals.push(arg);
    }
  }
  return { flags, positionals };
}

function priOf(level) {
  return level === "error" ? 3 : level === "warn" ? 4 : level === "debug" ? 7 : 6;
}

function levelName(pri) {
  return pri <= 3 ? "error" : pri === 4 ? "warn" : pri >= 7 ? "debug" : "info";
}

module.exports = { connect, ready, emit, parseFlags, priOf, levelName, NAME };