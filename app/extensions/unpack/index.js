// Unpack extension: extract an archive into a grant directory, with path and size guards.
//
// Guard strategy: entry names and link entries are rejected from the tar listing before
// anything is written; the byte/file limits are enforced *while* extracting (the child is
// killed and the destination removed as soon as a limit is crossed), so a bomb cannot fill
// the disk before it is noticed. A post-extraction walk then rejects links/specials.
const fs = require("fs");
const path = require("path");
const { execFileSync, spawn, spawnSync } = require("child_process");
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


// ---- 7z / msi ----
// .7z is unpacked with the vendored 7-Zip (no PATH lookup, works offline); .msi uses the
// Windows Installer administrative install (msiexec /a), which extracts the file payload
// without registering the product or requiring elevation.
function sevenZipPath() {
  const candidates = [process.env.PD_7Z, path.join(__dirname, "vendor", "7zip", "7z.exe")].filter(Boolean);
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }
  throw new UnpackError("ERR_UNSUPPORTED", "7-Zip helper is missing (expected vendor/7zip/7z.exe)");
}

function sevenZipEntries(archive) {
  const output = execFileSync(sevenZipPath(), ["l", "-slt", archive], {
    encoding: "utf8", maxBuffer: 64 * 1024 * 1024, windowsHide: true,
  });
  // -slt prints the archive header first; entries start after the separator line.
  const lines = output.split(/\r?\n/);
  const separator = lines.findIndex((line) => line.trim() === "----------");
  const body = separator >= 0 ? lines.slice(separator + 1).join("\n") : output;
  const entries = [];
  let current = null;
  for (const raw of body.split(/\r?\n/)) {
    const line = raw.replace(/\s+$/, "");
    if (line.startsWith("Path = ")) {
      current = { name: line.slice(7).replace(/\\/g, "/"), size: 0, dir: false, link: false };
      entries.push(current);
      continue;
    }
    if (!current) {
      continue;
    }
    if (line.startsWith("Size = ")) {
      current.size = Number(line.slice(7)) || 0;
    } else if (line.startsWith("Folder = ")) {
      current.dir = line.endsWith("+");
    } else if (line.startsWith("Symbolic Link = ") || line.startsWith("Hard Link = ")) {
      current.link = true;
    }
  }
  return entries;
}

// Validates names, links and the declared sizes BEFORE extraction (bomb guard).
function assertSafeSevenZip(archive, limits) {
  const entries = sevenZipEntries(archive);
  let files = 0;
  let bytes = 0;
  for (const entry of entries) {
    const name = entry.name;
    if (entry.link) {
      throw new UnpackError("ERR_UNSUPPORTED", "archive contains a link entry: " + name.slice(0, 160));
    }
    if (name.startsWith("/") || /^[A-Za-z]:/.test(name) || name.split("/").includes("..")) {
      throw new UnpackError("ERR_UNSUPPORTED", "archive entry escapes the destination: " + name.slice(0, 160));
    }
    if (!entry.dir) {
      files += 1;
      bytes += entry.size;
    }
  }
  if (files > limits.maxFiles) {
    throw new UnpackError("ERR_UNSUPPORTED", "archive declares too many files (" + files + ")");
  }
  if (bytes > limits.maxBytes) {
    throw new UnpackError("ERR_UNSUPPORTED", "archive declares too many bytes (" + bytes + ")");
  }
}

function extractSevenZip(archive, dest) {
  const result = spawnSync(sevenZipPath(), ["x", archive, "-o" + dest, "-y", "-bso0", "-bsp0"], {
    encoding: "utf8", timeout: 15 * 60 * 1000, windowsHide: true,
  });
  if (result.error) {
    throw new UnpackError("ERR_IO", "7z failed: " + result.error.message);
  }
  if (result.status !== 0) {
    const detail = String(result.stderr || "").split(/\r?\n/).filter(Boolean).slice(-2).join(" | ");
    throw new UnpackError("ERR_IO", "7z exited with " + result.status + (detail ? ": " + detail.slice(0, 240) : ""));
  }
}

function moveChildren(from, to) {
  fs.mkdirSync(to, { recursive: true });
  for (const name of fs.readdirSync(from)) {
    const target = path.join(to, name);
    if (fs.existsSync(target)) {
      throw new UnpackError("ERR_IO", "msi payload collides with an existing entry: " + name);
    }
    fs.renameSync(path.join(from, name), target);
  }
}

function extractMsi(archive, dest) {
  const temp = path.join(path.dirname(dest), ".msi-tmp-" + process.pid + "-" + Date.now());
  const log = temp + ".log";
  fs.mkdirSync(temp, { recursive: true });
  try {
    const result = spawnSync("msiexec.exe", ["/a", archive, "/qn", "/norestart", "TARGETDIR=" + temp, "/L*v", log], {
      timeout: 15 * 60 * 1000, windowsHide: true, encoding: "utf8",
    });
    const code = result.status;
    if (code !== 0 && code !== 3010) {
      let detail = "";
      try {
        detail = fs.readFileSync(log, "utf8").split(/\r?\n/).filter(Boolean).slice(-3).join(" | ");
      } catch (error) {
        // the log is best effort
      }
      throw new UnpackError("ERR_IO", "msiexec administrative install exited with " + code +
        (detail ? ": " + detail.slice(0, 300) : ""));
    }
    moveChildren(temp, dest);
    // Administrative installs also copy the source .msi into the image root; it is not payload.
    const selfCopy = path.join(dest, path.basename(archive));
    try { fs.rmSync(selfCopy, { force: true }); } catch (error) { /* best effort */ }
  } finally {
    try { fs.rmSync(temp, { recursive: true, force: true }); } catch (error) { /* best effort */ }
    try { fs.rmSync(log, { force: true }); } catch (error) { /* best effort */ }
  }
}

// unpack.extractDir: only keep one subdirectory of the extracted tree.
function selectSubdir(dest, rel) {
  const normalized = String(rel || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
  if (!normalized || normalized.split("/").includes("..") || /^[A-Za-z]:/.test(normalized)) {
    throw new UnpackError("ERR_PKG_INVALID", "bad extractDir: " + rel);
  }
  const source = path.resolve(dest, normalized);
  if (!isInside(dest, source) || !fs.existsSync(source) || !fs.statSync(source).isDirectory()) {
    throw new UnpackError("ERR_PKG_INVALID", "extractDir is missing in the archive: " + rel);
  }
  const picked = dest + ".pick-" + process.pid;
  fs.rmSync(picked, { recursive: true, force: true });
  fs.mkdirSync(picked, { recursive: true });
  for (const name of fs.readdirSync(source)) {
    fs.renameSync(path.join(source, name), path.join(picked, name));
  }
  fs.rmSync(dest, { recursive: true, force: true });
  fs.renameSync(picked, dest);
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
  if (!["zip", "tar.gz", "7z", "msi"].includes(kind)) {
    throw new UnpackError("ERR_UNSUPPORTED", "unpacking " + kind + " is not implemented (zip, tar.gz, 7z, msi)");
  }

  const stagingRoot = path.join(ROOT, "staging");
  if (!isInside(stagingRoot, dest)) {
    throw new UnpackError("ERR_UNSUPPORTED", "the destination must be inside the stick staging directory");
  }

  const limits = {
    maxFiles: Number(args.maxFiles) > 0 ? Number(args.maxFiles) : MAX_FILES_DEFAULT,
    maxBytes: Number(args.maxBytes) > 0 ? Number(args.maxBytes) : MAX_BYTES_DEFAULT,
  };

  fs.mkdirSync(dest, { recursive: true });
  try {
    if (kind === "zip" || kind === "tar.gz") {
      assertSafeEntries(listEntries(archive));
      assertNoLinkEntries(archive);
      await extractWithLimits(archive, dest, limits);
    } else if (kind === "7z") {
      assertSafeSevenZip(archive, limits);
      extractSevenZip(archive, dest);
    } else {
      extractMsi(archive, dest);
    }
    // Reject links before strip/lift: lift() would otherwise follow a link out of the sandbox.
    walkStats(dest);
    const extractDir = String(args.extractDir || "");
    if (extractDir) {
      selectSubdir(dest, extractDir);
      walkStats(dest);
    }
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
