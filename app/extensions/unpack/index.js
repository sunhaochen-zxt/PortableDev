// Unpack extension: extract an archive into a grant directory, with path and size guards.
//
// Guard strategy: entry names and link entries are rejected from the tar listing before
// anything is written; the byte/file limits are enforced *while* extracting (the child is
// killed and the destination removed as soon as a limit is crossed), so a bomb cannot fill
// the disk before it is noticed. A post-extraction walk then rejects links/specials.
const fs = require("fs");
const path = require("path");
const { execFileSync, spawn } = require("child_process");
const { connect, ready } = require("./protocol");

const ROOT = process.env.PD_ROOT || ".";

const MAX_FILES_DEFAULT = 200000;
const MAX_BYTES_DEFAULT = 4 * 1024 * 1024 * 1024;
const MONITOR_INTERVAL_MS = 100;

function UnpackError(code, message) {
  this.code = code;
  this.message = message;
}

function kindOf(archive, declared) {
  const explicit = String(declared || "auto").toLowerCase();
  if (explicit !== "auto" && explicit !== "") {
    return explicit;
  }
  const lower = archive.toLowerCase();
  if (lower.endsWith(".zip") || lower.endsWith(".pdpkg")) {
    return "zip";
  }
  if (lower.endsWith(".tar.gz") || lower.endsWith(".tgz")) {
    return "tar.gz";
  }
  if (lower.endsWith(".7z")) {
    return "7z";
  }
  if (lower.endsWith(".msi")) {
    return "msi";
  }
  return "zip";
}

function isInside(root, candidate) {
  const base = path.resolve(root).toLowerCase();
  const full = path.resolve(candidate).toLowerCase();
  const relative = path.relative(base, full);
  return relative.length > 0 && !relative.startsWith("..") && !path.isAbsolute(relative);
}

function listEntries(archive) {
  const output = execFileSync("tar", ["-tf", archive], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  return output.split("\n").map((line) => line.trim()).filter(Boolean);
}

function assertSafeEntries(entries) {
  for (const entry of entries) {
    const normalized = entry.replace(/\\/g, "/");
    if (normalized.startsWith("/") || /^[A-Za-z]:/.test(normalized) || normalized.split("/").includes("..")) {
      throw new UnpackError("ERR_UNSUPPORTED", "archive entry escapes the destination: " + entry);
    }
  }
}

// A link entry must never reach the disk: lift()/writes would follow it out of the sandbox.
function assertNoLinkEntries(archive) {
  const output = execFileSync("tar", ["-tvf", archive], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  for (const line of output.split("\n")) {
    if (line.length === 0) {
      continue;
    }
    const mode = line[0];
    if (mode === "l" || mode === "h") {
      throw new UnpackError("ERR_UNSUPPORTED", "archive contains a link entry: " + line.slice(0, 160));
    }
  }
}

function measure(dir) {
  let files = 0;
  let bytes = 0;
  const stack = [dir];
  while (stack.length > 0) {
    const current = stack.pop();
    let names;
    try {
      names = fs.readdirSync(current);
    } catch (error) {
      continue;
    }
    for (const name of names) {
      const full = path.join(current, name);
      let stat;
      try {
        stat = fs.lstatSync(full);
      } catch (error) {
        continue;
      }
      if (stat.isDirectory()) {
        stack.push(full);
      } else {
        files += 1;
        bytes += stat.size;
      }
    }
  }
  return { files, bytes };
}

function walkStats(dir) {
  let files = 0;
  let bytes = 0;
  const stack = [dir];
  while (stack.length > 0) {
    const current = stack.pop();
    for (const name of fs.readdirSync(current)) {
      const full = path.join(current, name);
      const stat = fs.lstatSync(full);
      if (stat.isSymbolicLink()) {
        throw new UnpackError("ERR_UNSUPPORTED", "archive contains a symbolic link or junction: " + full);
      }
      if (stat.isDirectory()) {
        stack.push(full);
      } else if (stat.isFile()) {
        files += 1;
        bytes += stat.size;
      } else {
        throw new UnpackError("ERR_UNSUPPORTED", "archive contains a special file: " + full);
      }
    }
  }
  return { files, bytes };
}

// Runs tar and watches the destination; the first crossing of a limit kills the extraction.
function extractWithLimits(archive, dest, limits) {
  return new Promise((resolve, reject) => {
    const child = spawn("tar", ["-xf", archive, "-C", dest], { stdio: "ignore", windowsHide: true });
    let settled = false;
    let timer = null;
    const finish = (error) => {
      if (settled) {
        return;
      }
      settled = true;
      if (timer !== null) {
        clearInterval(timer);
      }
      if (error) {
        try { child.kill(); } catch (killError) { /* already gone */ }
        reject(error);
        return;
      }
      resolve();
    };

    timer = setInterval(() => {
      const current = measure(dest);
      if (current.bytes > limits.maxBytes) {
        finish(new UnpackError("ERR_UNSUPPORTED", "extraction exceeded the byte limit (" + limits.maxBytes + " bytes)"));
      } else if (current.files > limits.maxFiles) {
        finish(new UnpackError("ERR_UNSUPPORTED", "extraction exceeded the file limit (" + limits.maxFiles + ")"));
      }
    }, MONITOR_INTERVAL_MS);

    child.on("error", (error) => finish(new UnpackError("ERR_IO", "cannot extract the archive: " + (error.message || String(error)))));
    child.on("exit", (code) => {
      if (code === 0) {
        finish(null);
      } else {
        finish(new UnpackError("ERR_IO", "tar exited with " + code));
      }
    });
  });
}

function lift(dir, levels) {
  for (let level = 0; level < levels; level += 1) {
    const entries = fs.readdirSync(dir).filter((name) => name !== ".." && name !== ".");
    if (entries.length !== 1) {
      return;
    }
    const only = path.join(dir, entries[0]);
    if (!fs.statSync(only).isDirectory()) {
      return;
    }
    for (const name of fs.readdirSync(only)) {
      fs.renameSync(path.join(only, name), path.join(dir, name));
    }
    fs.rmdirSync(only);
  }
}

async function extract(args) {
  const archive = path.resolve(String(args.archive || ""));
  const dest = path.resolve(String(args.dest || ""));
  if (!fs.existsSync(archive)) {
    throw new UnpackError("ERR_IO", "archive does not exist: " + archive);
  }
  if (dest.length === 0) {
    throw new UnpackError("ERR_BAD_ARGS", "extract needs a destination");
  }

  const kind = kindOf(archive, args.kind);
  if (kind !== "zip" && kind !== "tar.gz") {
    throw new UnpackError("ERR_UNSUPPORTED", "unpacking " + kind + " is not implemented yet (zip and tar.gz only)");
  }

  const stagingRoot = path.join(ROOT, "staging");
  if (!isInside(stagingRoot, dest)) {
    throw new UnpackError("ERR_UNSUPPORTED", "the destination must be inside the stick staging directory");
  }

  assertSafeEntries(listEntries(archive));
  assertNoLinkEntries(archive);

  const limits = {
    maxFiles: Number(args.maxFiles) > 0 ? Number(args.maxFiles) : MAX_FILES_DEFAULT,
    maxBytes: Number(args.maxBytes) > 0 ? Number(args.maxBytes) : MAX_BYTES_DEFAULT,
  };

  fs.mkdirSync(dest, { recursive: true });
  try {
    await extractWithLimits(archive, dest, limits);
    // Reject links before strip/lift: lift() would otherwise follow a link out of the sandbox.
    walkStats(dest);
    const strip = Number(args.strip) > 0 ? Number(args.strip) : 0;
    if (strip > 0) {
      lift(dest, strip);
    }

    const stats = walkStats(dest);
    if (stats.bytes > limits.maxBytes || stats.files > limits.maxFiles) {
      throw new UnpackError("ERR_UNSUPPORTED", "extracted archive exceeds the limits (" + stats.bytes + " bytes, " + stats.files + " files)");
    }
    return { dest, files: stats.files, bytes: stats.bytes, kind };
  } catch (error) {
    // Never leave a partially validated tree (possibly containing links) behind.
    try { fs.rmSync(dest, { recursive: true, force: true }); } catch (removeError) { /* best effort */ }
    throw error;
  }
}

let send = () => {};

async function handleCall(message) {
  const id = message.id;
  const capability = message.data && message.data.cap;
  const op = (message.data && message.data.op) || "";
  const args = (message.data && message.data.args) || {};
  try {
    if (capability !== "unpack.extract" || op !== "extract") {
      send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_UNSUPPORTED", message: "unknown " + capability + "/" + op } } });
      return;
    }
    const result = await extract(args);
    send({ v: 1, kind: "call.ok", id, data: { result } });
  } catch (error) {
    send({ v: 1, kind: "call.err", id, data: { error: { code: error.code || "ERR_IO", message: error.message || String(error) } } });
  }
}

connect({
  onMessage(message, outbound) {
    send = outbound;
    if (message.kind === "hello.ok") {
      ready(send, [{ name: "unpack.extract", version: 1, mutates: true }]);
      return;
    }
    if (message.kind === "call") {
      handleCall(message).catch(() => { /* errors are reported as call.err */ });
    }
  },
});
