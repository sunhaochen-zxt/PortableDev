// Test driver for kernel-side capability permissions: one declared call must pass, one undeclared
// call must be rejected by the kernel with ERR_UNAUTHORIZED before it reaches the provider.
const { connect, ready } = require("./protocol");

const pending = new Map();
let counter = 0;
let send = () => {};

function call(capability, op, args) {
  return new Promise((resolve, reject) => {
    counter += 1;
    const id = "p" + counter;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error("call timeout")); }, 20000);
    pending.set(id, { resolve, reject, timer });
    send({ v: 1, kind: "call", id, data: { cap: capability, op, args } });
  });
}

async function run(message) {
  const id = message.id;
  const argv = (message.data && message.data.argv) || [];
  const mode = argv[0] || "ok";
  const done = (payload) => send({ v: 1, kind: "evt", id, seq: 1, data: Object.assign({ type: "done", ok: true, exitCode: 0 }, payload) });
  const fail = (code, text) => send({ v: 1, kind: "evt", id, seq: 1, data: { type: "done", ok: false, exitCode: 1, error: { code, message: text } } });
  try {
    if (mode === "ok") {
      const result = await call("storage.record", "list", {});
      done({ summary: "declared call ok", metrics: { packages: (result.items || []).length } });
      return;
    }
    if (mode === "denied") {
      try {
        await call("storage.uninstall", "list", {});
        done({ summary: "undeclared call unexpectedly succeeded", metrics: { denied: false } });
      } catch (error) {
        fail(error.code || "ERR_IO", error.message || String(error));
      }
      return;
    }
    fail("ERR_BAD_ARGS", "capprobe expects ok|denied");
  } catch (error) {
    fail(error.code || "ERR_IO", error.message || String(error));
  }
}

connect({
  onMessage(message, outbound) {
    send = outbound;
    if (message.kind === "hello.ok") { ready(send, []); return; }
    if (message.kind === "call.ok" || message.kind === "call.err") {
      const entry = pending.get(message.id);
      if (entry) {
        clearTimeout(entry.timer);
        pending.delete(message.id);
        if (message.kind === "call.ok") entry.resolve((message.data && message.data.result) || {});
        else {
          const error = (message.data && message.data.error) || {};
          const failure = new Error(error.message || "call failed");
          failure.code = error.code;
          entry.reject(failure);
        }
      }
      return;
    }
    if (message.kind === "ext.invoke") { run(message); }
  },
});
