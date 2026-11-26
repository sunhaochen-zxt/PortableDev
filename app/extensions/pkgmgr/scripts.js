// Restricted package script runner (package-spec 3.5 / architecture 8.2).
// The script never talks to the kernel: it prints JSON requests on stdout, pkgmgr executes the
// whitelisted ones through capabilities and answers on stdin.
const fs = require("fs");
const path = require("path");

const REQUEST_TIMEOUT_MS = 30000;
const JOB_MEMORY_LIMIT_MB = 1024;
const JOB_ACTIVE_PROCESS_LIMIT = 64;

// Creates the three named pipes the proc extension will hand to the child. The facade mimics the
// parts of a ChildProcess the request loop uses (stdin/stdout/stderr streams and an "exit" event).
function createProcPipeSet() {
  const net = require("net");
  const { PassThrough } = require("stream");
  const { EventEmitter } = require("events");
  const BS = String.fromCharCode(92);
  const prefix = BS + BS + "." + BS + "pipe" + BS;
  const suffix = String(process.pid) + "-" + Date.now().toString(36) + "-" + Math.floor(Math.random() * 1e6).toString(36);
  const names = {
    stdin: "pd-script-" + suffix + "-in",
    stdout: "pd-script-" + suffix + "-out",
    stderr: "pd-script-" + suffix + "-err",
  };
  const stdinStream = new PassThrough();
  const stdoutStream = new PassThrough();
  const stderrStream = new PassThrough();
  const sockets = [];
  const ignore = () => {};
  const socketsIn = (socket, wire) => {
    sockets.push(socket);
    socket.on("error", ignore);
    wire(socket);
  };
  const servers = [
    net.createServer((socket) => socketsIn(socket, (s) => stdinStream.pipe(s))),
    net.createServer((socket) => socketsIn(socket, (s) => s.pipe(stdoutStream))),
    net.createServer((socket) => socketsIn(socket, (s) => s.pipe(stderrStream))),
  ];
  const child = new EventEmitter();
  child.stdin = stdinStream;
  child.stdout = stdoutStream;
  child.stderr = stderrStream;
  child.pid = 0;
  child.kill = ignore;

  const ready = () => Promise.all(servers.map((server, index) => new Promise((resolve, reject) => {
    const onError = (error) => reject(error);
    server.once("error", onError);
    server.listen(prefix + [names.stdin, names.stdout, names.stderr][index], () => {
      server.removeListener("error", onError);
      resolve();
    });
  })));

  const close = () => {
    for (const server of servers) {
      try { server.close(); } catch (error) { /* already closed */ }
    }
    for (const socket of sockets) {
      try { socket.destroy(); } catch (error) { /* already gone */ }
    }
    stdinStream.destroy();
    stdoutStream.destroy();
    stderrStream.destroy();
  };

  return { names, child, ready, close };
}

function ScriptError(code, message, exitCode) {
  this.code = code;
  this.message = message;
  this.exitCode = exitCode;
}

function parseDuration(text, fallbackMs) {
  const raw = String(text || "").trim();
  const match = /^(\d+)(s|m|h|d)?$/.exec(raw);
  if (!match) {
    return fallbackMs;
  }
  const amount = Number(match[1]);
  const unit = match[2] || "s";
  return amount * (unit === "d" ? 86400000 : unit === "h" ? 3600000 : unit === "m" ? 60000 : 1000);
}

function allowedByPolicy(flags, settings, fromNetwork) {
  if (flags["no-scripts"] === true) {
    return false;
  }
  if (flags["allow-scripts"] === true) {
    return true;
  }
  const mode = String((settings.install && settings.install.allowScripts) || "local");
  if (mode === "never") {
    return false;
  }
  if (mode === "always") {
    return true;
  }
  return !fromNetwork;
}

function inside(root, candidate) {
  const base = path.resolve(root);
  const full = path.resolve(candidate);
  const relative = path.relative(base, full);
  return relative.length > 0 && !relative.startsWith("..") && !path.isAbsolute(relative);
}

function expandText(value, context) {
  return String(value === undefined || value === null ? "" : value)
    .replace(/\{\{STICK\}\}/g, context.root)
    .replace(/\{\{ROOT\}\}/g, context.stableRoot)
    .replace(/\{\{HOME\}\}/g, context.home)
    .replace(/\{\{PERSIST\}\}/g, context.home)
    .replace(/\{\{ID\}\}/g, context.pkgId)
    .replace(/\{\{VERSION\}\}/g, context.version)
    .replace(/\{\{APPDATA\}\}/g, process.env.APPDATA || "")
    .replace(/\{\{LOCALAPPDATA\}\}/g, process.env.LOCALAPPDATA || "")
    .replace(/\{\{USERPROFILE\}\}/g, process.env.USERPROFILE || "");
}

function relativeInside(root, candidate) {
  const relative = path.relative(path.resolve(root), path.resolve(candidate));
  if (relative.length === 0 || relative.startsWith("..") || path.isAbsolute(relative)) {
    return null;
  }
  return relative.split(path.sep).join("/");
}

function makeContext(options) {
  const settings = options.settings || {};
  const stableRoot = String((settings.root && settings.root.stableRoot) || "C:\\Dev");
  return {
    root: options.root,
    stableRoot: stableRoot,
    home: path.join(options.root, "home", options.pkgId),
    pkgId: options.pkgId,
    version: options.version,
    contentRoot: options.contentRoot,
    scriptRoot: options.scriptRoot || options.contentRoot,
    task: options.task,
    network: false,
  };
}

function scriptPath(context, value) {
  const expanded = expandText(value, context);
  const candidate = path.isAbsolute(expanded) ? path.resolve(expanded) : path.resolve(context.scriptRoot, expanded);
  if (!inside(context.scriptRoot, candidate)) {
    throw new ScriptError("ERR_UNSUPPORTED", "script entry escapes the package root: " + value, 11);
  }
  return candidate;
}

function contentPath(context, value) {
  const expanded = expandText(value, context);
  const candidate = path.isAbsolute(expanded) ? path.resolve(expanded) : path.resolve(context.contentRoot, expanded);
  if (!inside(context.contentRoot, candidate)) {
    throw new ScriptError("ERR_UNSUPPORTED", "script path escapes the package content: " + value, 11);
  }
  return candidate;
}

function readPathArg(context, value) {
  return /^[A-Za-z]:[\\/]|^\{\{/.test(String(value || "")) ? expandText(value, context) : contentPath(context, value);
}

function assertRequest(context, args) {
  const kind = String(args.kind || "exists");
  const target = contentPath(context, args.path || "");
  if (kind === "exists") {
    if (!fs.existsSync(target)) {
      throw new ScriptError("ERR_ASSERT", "assert exists failed: " + args.path, 12);
    }
    return {};
  }
  if (kind === "missing") {
    if (fs.existsSync(target)) {
      throw new ScriptError("ERR_ASSERT", "assert missing failed: " + args.path, 12);
    }
    return {};
  }
  if (kind === "json") {
    const document = JSON.parse(fs.readFileSync(target, "utf8"));
    const segments = String(args.pointer || "").split("/").filter(Boolean);
    let current = document;
    for (const segment of segments) {
      current = current == null ? undefined : current[segment];
    }
    if (args.equals !== undefined && JSON.stringify(current) !== JSON.stringify(args.equals)) {
      throw new ScriptError("ERR_ASSERT", "assert json mismatch at " + (args.pointer || "/"), 12);
    }
    return {};
  }
  if (kind === "fileHash") {
    const crypto = require("crypto");
    const actual = crypto.createHash("sha256").update(fs.readFileSync(target)).digest("hex");
    if (actual !== String(args.sha256 || "").toLowerCase()) {
      throw new ScriptError("ERR_ASSERT", "assert fileHash mismatch: " + args.path, 12);
    }
    return {};
  }
  throw new ScriptError("ERR_UNSUPPORTED", "unsupported assert kind: " + kind, 11);
}

async function runScript(options) {
  const { manifest, contentRoot, pkgId, version, task, flags, settings, root, fromNetwork, event, call, scriptRoot } = options;
  const script = manifest.script;
  if (!script || typeof script !== "object") {
    return { ran: false };
  }
  const on = Array.isArray(script.on) ? script.on : ["install"];
  if (!on.includes("install")) {
    return { ran: false };
  }
  if (!allowedByPolicy(flags, settings, fromNetwork)) {
    event("step", { stepId: "script", title: "script (skipped)", state: "skipped" });
    return { ran: false, skipped: true };
  }
  if (script.elevated === true) {
    throw new ScriptError("ERR_UNSUPPORTED", "elevated package scripts are not implemented yet (elevated: true)", 11);
  }

  const entry = String(script.entry || "");
  if (!entry) {
    throw new ScriptError("ERR_PKG_INVALID", "script.entry is required", 3);
  }
  const entryPath = scriptPath(makeContext({ root: root, settings: settings, pkgId: pkgId, version: version, contentRoot: contentRoot, scriptRoot: scriptRoot }), entry);
  if (!fs.existsSync(entryPath)) {
    throw new ScriptError("ERR_PKG_INVALID", "script entry does not exist: " + entry, 3);
  }

  const interpreter = String(script.interpreter || "pwsh").toLowerCase();
  let command;
  let args;
  if (interpreter === "pwsh" || interpreter === "powershell") {
    command = "pwsh";
    args = ["-NoProfile", "-NonInteractive", "-File", entryPath];
  } else if (interpreter === "python") {
    command = "python";
    args = [entryPath];
  } else if (interpreter === "cmd") {
    command = "cmd";
    args = ["/c", entryPath];
  } else if (interpreter === "node") {
    command = "node";
    args = [entryPath];
  } else {
    throw new ScriptError("ERR_UNSUPPORTED", "unsupported script interpreter: " + interpreter, 11);
  }

  const context = makeContext({ root: root, settings: settings, pkgId: pkgId, version: version, contentRoot: contentRoot, scriptRoot: scriptRoot });
  context.network = script.network === true;
  const timeoutMs = parseDuration(script.timeout, 10 * 60 * 1000);
  const environment = {};
  for (const name of ["SystemRoot", "windir", "TEMP", "TMP", "PATH", "APPDATA", "LOCALAPPDATA", "USERPROFILE"]) {
    if (process.env[name]) {
      environment[name] = process.env[name];
    }
  }
  environment.PD_ROOT = root;
  environment.PD_EXTENSION = "pkgmgr";
  environment.PD_STICK_ID = process.env.PD_STICK_ID || "";
  environment.PD_KERNEL_ABI = process.env.PD_KERNEL_ABI || "1";
  // No PD_PIPE on purpose: scripts must not connect to the kernel (architecture 8.2).

  // Package scripts always run through the proc extension: it owns the Job Object, the limits,
  // the timeout and the tree kill. This process only owns the request/reply protocol over pipes.
  try {
    await call("proc.snapshot", "snapshot", {});
  } catch (error) {
    throw new ScriptError("ERR_EXT_MISSING", "process manager extension is unavailable; package scripts need proc", 20);
  }

  event("step", { stepId: "script", title: "script", state: "running" });

  const procPipes = createProcPipeSet();
  try {
    await procPipes.ready();
  } catch (error) {
    procPipes.close();
    throw new ScriptError("ERR_IO", "cannot create the script pipes: " + (error.message || String(error)), 1);
  }

  const child = procPipes.child;
  let procResult = null;
  const procCall = call("proc.spawn", "spawn", {
    command: command,
    args: args,
    cwd: contentRoot,
    env: environment,
    timeoutMs: timeoutMs,
    memoryLimitMB: JOB_MEMORY_LIMIT_MB,
    activeProcessLimit: JOB_ACTIVE_PROCESS_LIMIT,
    stdinPipe: procPipes.names.stdin,
    stdoutPipe: procPipes.names.stdout,
    stderrPipe: procPipes.names.stderr,
  }).then((result) => {
    procResult = result || {};
    child.emit("exit", procResult.exitCode === undefined ? null : procResult.exitCode);
    return procResult;
  }, (error) => {
    child.emit("error", error);
    throw error;
  });
  procCall.catch(() => {});

  let finished = false;
  let timedOut = false;
  let failure = null;
  let stderr = "";
  let buffer = "";
  let chain = Promise.resolve();
  let failedRequests = 0;
  const pendingLinks = [];

  const reply = (id, payload) => {
    if (finished || !child.stdin.writable) {
      return;
    }
    try {
      child.stdin.write(JSON.stringify(Object.assign({ id: id }, payload)) + "\n");
    } catch (error) {
      failure = new ScriptError("ERR_IO", "cannot write the script reply: " + (error.message || String(error)), 1);
      try { child.stdin.destroy(); } catch (inner) { /* pipes already gone */ }
    }
  };

  const dispatch = async (request) => {
    if (finished) {
      return;
    }
    const requestId = request && request.id !== undefined ? request.id : "";
    const command2 = String((request && request.cmd) || "");
    const requestArgs = (request && request.args) || {};
    try {
      if (command2 === "write") {
        const target = contentPath(context, requestArgs.path || "");
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(target, String(requestArgs.content === undefined ? "" : requestArgs.content));
        reply(requestId, { ok: true, data: { path: target } });
        return;
      }
      if (command2 === "copy" || command2 === "move") {
        const from = contentPath(context, requestArgs.from || requestArgs.source || "");
        const to = contentPath(context, requestArgs.to || requestArgs.target || "");
        fs.mkdirSync(path.dirname(to), { recursive: true });
        if (command2 === "copy") {
          fs.cpSync(from, to, { recursive: true });
        } else {
          fs.renameSync(from, to);
        }
        reply(requestId, { ok: true, data: { path: to } });
        return;
      }
      if (command2 === "rm") {
        const target = contentPath(context, requestArgs.path || "");
        fs.rmSync(target, { recursive: true, force: true });
        reply(requestId, { ok: true, data: {} });
        return;
      }
      if (command2 === "fetch") {
        if (!context.network) {
          throw new ScriptError("ERR_UNSUPPORTED", "script.network is false; fetch is not allowed", 11);
        }
        const url = String(requestArgs.url || "");
        if (!/^https?:\/\//i.test(url)) {
          throw new ScriptError("ERR_UNSUPPORTED", "script fetch needs an http(s) url", 11);
        }
        const target = contentPath(context, requestArgs.path || requestArgs.target || path.basename(new URL(url).pathname) || "download.bin");
        fs.mkdirSync(path.dirname(target), { recursive: true });
        await call("download.fetch", "get", { url: url, dest: target });
        reply(requestId, { ok: true, data: { path: target } });
        return;
      }
      if (command2 === "unpack") {
        const archive = contentPath(context, requestArgs.archive || "");
        const dest = contentPath(context, requestArgs.dest || "");
        const extracted = await call("unpack.extract", "extract", {
          archive: archive,
          dest: dest,
          kind: requestArgs.kind || "auto",
          strip: requestArgs.strip || 0,
        });
        reply(requestId, { ok: true, data: { path: extracted.dest, files: extracted.files } });
        return;
      }
      if (command2 === "assert") {
        const data = assertRequest(context, requestArgs);
        reply(requestId, { ok: true, data: data });
        return;
      }
      if (command2 === "link") {
        const source = readPathArg(context, requestArgs.source || "");
        const target = expandText(requestArgs.target || "", context);
        if (!target) {
          throw new ScriptError("ERR_BAD_ARGS", "link needs target", 2);
        }
        const relative = relativeInside(context.contentRoot, source);
        if (relative !== null) {
          // The content is renamed into the store after the script; point the link at the final path.
          pendingLinks.push({ relative: relative, target: target });
          reply(requestId, { ok: true, data: { deferred: true, relative: relative } });
          return;
        }
        const result = await call("storage.wire", "link.create", { task: task, source: source, target: target });
        reply(requestId, { ok: true, data: result });
        return;
      }
      if (command2 === "path.add" || command2 === "env.set" || command2 === "reg.set" || command2 === "reg.del" || command2 === "launch") {
        const args2 = Object.assign({}, requestArgs, { task: task });
        if (args2.path !== undefined) {
          args2.path = expandText(args2.path, context);
        }
        if (args2.value !== undefined) {
          args2.value = expandText(args2.value, context);
        }
        const op = command2 === "reg.set" ? "registry.set"
          : command2 === "reg.del" ? "registry.delete"
          : command2 === "launch" ? "launcher.write"
          : command2;
        const result = await call("storage.wire", op, args2);
        reply(requestId, { ok: true, data: result });
        return;
      }

      failedRequests += 1;
      reply(requestId, { ok: false, error: { code: "ERR_UNSUPPORTED", message: "script command not allowed: " + command2 } });
    } catch (error) {
      failedRequests += 1;
      reply(requestId, { ok: false, error: { code: error.code || "ERR_IO", message: error.message || String(error) } });
    }
  };

  child.stdout.on("data", (chunk) => {
    buffer += chunk.toString("utf8");
    let newline;
    while ((newline = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      if (!line) {
        continue;
      }
      let request = null;
      try {
        request = JSON.parse(line);
      } catch (error) {
        continue;
      }
      if (!request || request.type !== "request") {
        continue;
      }
      chain = chain.then(() => dispatch(request));
    }
  });
  child.stderr.on("data", (chunk) => {
    if (stderr.length < 16 * 1024) {
      stderr += chunk.toString("utf8");
    }
  });

  let exit = await new Promise((resolve) => {
    child.on("error", (error) => {
      finished = true;
      failure = new ScriptError("ERR_SCRIPT", "cannot start the package script: " + error.message, 1);
      resolve(null);
    });
    child.on("exit", (code) => {
      finished = true;
      resolve(code);
    });
  });
  await chain;

  let procTimeout = null;
  try {
    await Promise.race([
      procCall,
      new Promise((_, reject) => {
        procTimeout = setTimeout(() => {
          reject(new ScriptError("ERR_TIMEOUT", "package script did not finish within " + Math.round((timeoutMs + 120000) / 1000) + "s", 25));
        }, timeoutMs + 120000);
      }),
    ]);
  } catch (error) {
    if (procTimeout) {
      clearTimeout(procTimeout);
      procTimeout = null;
    }
    procPipes.close();
    if (error && error.code === "ERR_TIMEOUT") {
      throw error;
    }
    if (error && error.code) {
      throw new ScriptError(error.code, "package script could not be started: " + (error.message || String(error)), error.exitCode || 1);
    }
    throw new ScriptError("ERR_SCRIPT", "package script could not be started: " + String(error), 1);
  }
  if (procTimeout) {
    clearTimeout(procTimeout);
  }
  if (procResult) {
    if (procResult.timedOut === true) {
      timedOut = true;
    }
    if (typeof procResult.exitCode === "number") {
      exit = procResult.exitCode;
    }
  }
  procPipes.close();

  if (failure) {
    event("step", { stepId: "script", title: "script", state: "failed" });
    throw failure;
  }
  if (timedOut) {
    event("step", { stepId: "script", title: "script", state: "failed" });
    throw new ScriptError("ERR_TIMEOUT", "package script timed out after " + Math.round(timeoutMs / 1000) + "s", 25);
  }
  if (failedRequests > 0 && (exit === 0 || exit === null)) {
    event("step", { stepId: "script", title: "script", state: "failed" });
    throw new ScriptError("ERR_SCRIPT", "package script continued after a failed request", 1);
  }
  if (exit !== 0) {
    event("step", { stepId: "script", title: "script", state: "failed" });
    const tail = stderr.trim().split("\n").slice(-3).join(" | ");
    throw new ScriptError("ERR_SCRIPT", "package script exited with " + exit + (tail ? ": " + tail : ""), 1);
  }

  event("step", { stepId: "script", title: "script", state: "done" });
  return { ran: true, pendingLinks: pendingLinks };
}

module.exports = { runScript: runScript, allowedByPolicy: allowedByPolicy, ScriptError: ScriptError };
