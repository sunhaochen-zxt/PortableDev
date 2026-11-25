// Unpack extension: extract an archive into a grant directory, with path and size guards.
const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
const { connect, ready } = require("./protocol");

const ROOT = process.env.PD_ROOT || ".";

const MAX_FILES_DEFAULT = 200000;
const MAX_BYTES_DEFAULT = 4 * 1024 * 1024 * 1024;

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

function listEntries(archive, kind) {
  if (kind === "zip" || kind === "tar.gz") {
    const output = execFileSync("tar", ["-tf", archive], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
    return output.split("\n").map((line) => line.trim()).filter(Boolean);
  }
  throw new UnpackError("ERR_UNSUPPORTED", "cannot list a " + kind + " archive yet");
}

function assertSafeEntries(entries) {
  for (const entry of entries) {
    const normalized = entry.replace(/\\/g, "/");
    if (normalized.startsWith("/") || /^[A-Za-z]:/.test(normalized) || normalized.split("/").includes("..")) {
      throw new UnpackError("ERR_UNSUPPORTED", "archive entry escapes the destination: " + entry);
    }
  }
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

function scanEntries(archive, kind) {
  let output;
  try {
    output = execFileSync("tar", ["-tvf", archive], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  } catch (error) {
    throw new UnpackError("ERR_IO", "cannot list the archive: " + (error.message || String(error)));
  }
  let declaredBytes = 0;
  for (const line of output.split("\n")) {
    if (line.length === 0) {
      continue;
    }
    if (line[0] === "l" || line[0] === "h") {
      throw new UnpackError("ERR_UNSUPPORTED", "archive contains a link entry: " + line.slice(0, 160));
    }
    const match = /^\S+\s+\S+\s+\S+\s+(\d+)\s/.exec(line);
    if (match) {
      declaredBytes += Number(match[1]);
    }
  }
  return declaredBytes;
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

function extract(args) {
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

  const entries = listEntries(archive, kind);
  if (args.sanitize !== false) {
    assertSafeEntries(entries);
  }

  const maxFiles = Number(args.maxFiles) > 0 ? Number(args.maxFiles) : MAX_FILES_DEFAULT;
  const maxBytes = Number(args.maxBytes) > 0 ? Number(args.maxBytes) : MAX_BYTES_DEFAULT;
  const declaredBytes = scanEntries(archive, kind);
  if (declaredBytes > maxBytes) {
    throw new UnpackError("ERR_UNSUPPORTED", "archive declares " + declaredBytes + " bytes (limit " + maxBytes + ")");
  }
  if (entries.length > maxFiles) {
    throw new UnpackError("ERR_UNSUPPORTED", "archive has " + entries.length + " entries (limit " + maxFiles + ")");
  }

  fs.mkdirSync(dest, { recursive: true });
  execFileSync("tar", ["-xf", archive, "-C", dest], { stdio: "ignore", maxBuffer: 64 * 1024 * 1024 });

  // Reject links before strip/lift: lift() would otherwise follow a link out of the sandbox.
  walkStats(dest);
  const strip = Number(args.strip) > 0 ? Number(args.strip) : 0;
  if (strip > 0) {
    lift(dest, strip);
  }

  const stats = walkStats(dest);
  if (stats.bytes > maxBytes) {
    fs.rmSync(dest, { recursive: true, force: true });
    throw new UnpackError("ERR_UNSUPPORTED", "extracted size " + stats.bytes + " exceeds the limit " + maxBytes);
  }

  return { dest, files: stats.files, bytes: stats.bytes, kind };
}

let send = () => {};

function handleCall(message) {
  const id = message.id;
  const capability = message.data && message.data.cap;
  const op = (message.data && message.data.op) || "";
  const args = (message.data && message.data.args) || {};
  try {
    if (capability !== "unpack.extract" || op !== "extract") {
      send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_UNSUPPORTED", message: "unknown " + capability + "/" + op } } });
      return;
    }
    const result = extract(args);
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
      handleCall(message);
    }
  },
});
