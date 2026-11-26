// Source & index resolution for pkgmgr (index-spec.md): sources.json, cached indexes,
// version resolution and blobs. Pure Node; network access goes through the download extension.
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { execFileSync } = require("child_process");
const scoop = require("./scoop");

const SOURCE_NAME = /^[a-z][a-z0-9-]{0,63}$/;
const VERSION = /^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$/;
const SHA256 = /^[0-9a-f]{64}$/;
const PACKAGE_ID = /^[a-z][a-z0-9-]{0,63}$/;

class SourceError extends Error {
  constructor(code, message, exitCode) {
    super(message);
    this.code = code;
    this.exitCode = exitCode;
  }
}

function now() {
  return new Date().toISOString();
}

function writeJsonAtomic(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temp = file + ".tmp-" + process.pid + "-" + Math.random().toString(36).slice(2, 8);
  fs.writeFileSync(temp, JSON.stringify(value, null, 2) + "\n");
  fs.renameSync(temp, file);
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function expandRoot(text, root) {
  return String(text === undefined || text === null ? "" : text).replace(/\{\{STICK\}\}/g, root);
}

function hashFile(file) {
  const digest = crypto.createHash("sha256");
  const fd = fs.openSync(file, "r");
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  try {
    let read;
    while ((read = fs.readSync(fd, buffer, 0, buffer.length, null)) > 0) {
      digest.update(buffer.subarray(0, read));
    }
  } finally {
    fs.closeSync(fd);
  }
  return digest.digest("hex");
}

function compareVersions(left, right) {
  const split = (value) => {
    const text = String(value || "");
    const dash = text.indexOf("-");
    return { base: dash < 0 ? text : text.slice(0, dash), pre: dash < 0 ? "" : text.slice(dash + 1) };
  };
  const a = split(left);
  const b = split(right);
  const aParts = a.base.split(".").map((part) => (/^\d+$/.test(part) ? Number(part) : part));
  const bParts = b.base.split(".").map((part) => (/^\d+$/.test(part) ? Number(part) : part));
  const length = Math.max(aParts.length, bParts.length);
  for (let index = 0; index < length; index += 1) {
    const x = aParts[index] ?? 0;
    const y = bParts[index] ?? 0;
    if (x === y) { continue; }
    if (typeof x === "number" && typeof y === "number") { return x < y ? -1 : 1; }
    return String(x) < String(y) ? -1 : 1;
  }
  if (a.pre === b.pre) { return 0; }
  if (a.pre === "") { return 1; }
  if (b.pre === "") { return -1; }
  return a.pre < b.pre ? -1 : 1;
}

function bumpSegment(version, index) {
  const parts = String(version).split(".").map((part) => Number(part) || 0);
  for (let position = 0; position < index; position += 1) {
    if (parts[position] === undefined) { parts[position] = 0; }
  }
  parts[index] = (parts[index] || 0) + 1;
  return parts.slice(0, index + 1).join(".");
}

function parseConstraint(text) {
  const raw = String(text === undefined || text === null ? "" : text).trim();
  if (raw === "" || raw === "latest") { return { kind: "latest" }; }
  if (raw.startsWith("^")) { return { kind: "range", terms: [{ op: ">=", version: raw.slice(1) }, { op: "<", version: bumpSegment(raw.slice(1), 0) }] }; }
  if (raw.startsWith("~")) {
    const base = raw.slice(1);
    const minor = base.split(".").length >= 2 ? 1 : 0;
    return { kind: "range", terms: [{ op: ">=", version: base }, { op: "<", version: bumpSegment(base, minor) }] };
  }
  if (/^(>=|<=|==|=|<|>)/.test(raw)) {
    const terms = raw.split(/\s+/).filter(Boolean).map((token) => {
      const match = /^(>=|<=|==|=|<|>)(.+)$/.exec(token);
      if (!match) { throw new SourceError("ERR_PKG_INVALID", "invalid version constraint: " + token); }
      return { op: match[1] === "=" ? "==" : match[1], version: match[2] };
    });
    return { kind: "range", terms };
  }
  if (!VERSION.test(raw)) { throw new SourceError("ERR_PKG_INVALID", "invalid version: " + raw); }
  return { kind: "exact", version: raw };
}

function satisfies(version, constraint) {
  if (constraint.kind === "latest") { return true; }
  if (constraint.kind === "exact") { return compareVersions(version, constraint.version) === 0; }
  for (const term of constraint.terms) {
    const order = compareVersions(version, term.version);
    if (term.op === ">=" && order < 0) { return false; }
    if (term.op === "<=" && order > 0) { return false; }
    if (term.op === ">" && order <= 0) { return false; }
    if (term.op === "<" && order >= 0) { return false; }
    if (term.op === "==" && order !== 0) { return false; }
  }
  return true;
}

function readIndexFile(file, expectedName) {
  let document;
  try {
    document = JSON.parse(fs.readFileSync(file, "utf8"));
  } catch (error) {
    throw new SourceError("ERR_INDEX_INVALID", "index " + (expectedName || "") + " is not valid JSON: " + (error.message || String(error)), 3);
  }
  return validateIndex(document, expectedName);
}

function validateIndex(document, expectedName) {
  const fail = (field) => {
    throw new SourceError("ERR_INDEX_INVALID", "index " + (expectedName || "") + " invalid: " + field, 3);
  };
  if (!document || typeof document !== "object" || Array.isArray(document)) { fail("not an object"); }
  if (document.schema !== 1) { fail("schema must be 1"); }
  if (typeof document.name !== "string" || !SOURCE_NAME.test(document.name)) { fail("name"); }
  if (!document.packages || typeof document.packages !== "object" || Array.isArray(document.packages)) { fail("packages"); }
  for (const [id, entry] of Object.entries(document.packages)) {
    if (!PACKAGE_ID.test(id)) { fail("package id " + id); }
    if (!entry || typeof entry !== "object") { fail("package " + id); }
    if (typeof entry.latest !== "string" || entry.latest.length === 0) { fail("package " + id + " latest"); }
    if (!entry.versions || typeof entry.versions !== "object" || Array.isArray(entry.versions)) { fail("package " + id + " versions"); }
    for (const [version, versionEntry] of Object.entries(entry.versions)) {
      if (!VERSION.test(version)) { fail("package " + id + " version " + version); }
      if (!versionEntry || typeof versionEntry !== "object") { fail("package " + id + " version entry " + version); }
      if (typeof versionEntry.url !== "string" || versionEntry.url.length === 0) { fail("package " + id + "@" + version + " url"); }
      if (typeof versionEntry.sha256 !== "string" || !SHA256.test(versionEntry.sha256.toLowerCase())) {
        fail("package " + id + "@" + version + " sha256");
      }
    }
  }
  return document;
}

function indexFromDirectory(directory) {
  const packages = {};
  let names = [];
  try { names = fs.readdirSync(directory).filter((name) => name.toLowerCase().endsWith(".pdpkg")).sort(); } catch (error) { names = []; }
  for (const name of names) {
    const file = path.join(directory, name);
    let manifest;
    try {
      manifest = JSON.parse(execFileSync("tar", ["-xOf", file, "package.json"], { encoding: "utf8", maxBuffer: 4 * 1024 * 1024 }));
    } catch (error) {
      continue;
    }
    if (!manifest || !PACKAGE_ID.test(String(manifest.id || "")) || !VERSION.test(String(manifest.version || ""))) { continue; }
    const sha256 = hashFile(file);
    const entry = packages[manifest.id] || (packages[manifest.id] = { latest: manifest.version, title: manifest.title || manifest.id, versions: {} });
    entry.versions[manifest.version] = {
      url: name,
      sha256,
      size: fs.statSync(file).size,
      platform: manifest.platform || "win-x64",
      channel: manifest.channel || "stable",
      yanked: false,
    };
    if (compareVersions(manifest.version, entry.latest) > 0) { entry.latest = manifest.version; }
  }
  return { schema: 1, name: "local", packages };
}

function createSourceManager(options) {
  const root = path.resolve(options.root);
  const stateDir = path.resolve(options.stateDir || path.join(root, "state"));
  const configDir = path.resolve(options.configDir || path.join(root, "config"));
  const appRoot = path.resolve(options.appRoot || path.join(root, "app"));
  const call = options.call;
  const sourcesFile = path.join(configDir, "sources.json");
  const cacheRoot = path.join(stateDir, "sources");
  const setup = options.settings || {};

  function defaults() {
    const sources = [];
    const builtinRoot = path.join(appRoot, "sources");
    try {
      for (const name of fs.readdirSync(builtinRoot).sort()) {
        if (fs.existsSync(path.join(builtinRoot, name, "index.json"))) {
          sources.push({ name: name, kind: "builtin", priority: 100, enabled: true, path: path.join(builtinRoot, name, "index.json") });
        }
      }
    } catch (error) { /* no builtin sources */ }
    const packages = path.join(root, "packages");
    if (fs.existsSync(packages)) {
      sources.push({ name: "local", kind: "dir", path: packages, priority: 10, enabled: true });
    }
    return { schema: 1, sources: sources, defaultSource: "", allowPrerelease: false, offlineOnly: false };
  }

  function load() {
    let document = null;
    try { document = readJson(sourcesFile); } catch (error) { document = null; }
    if (!document || document.schema !== 1 || !Array.isArray(document.sources)) {
      document = defaults();
      save(document);
      return document;
    }
    document.sources = document.sources.filter((source) => SOURCE_NAME.test(String(source.name || "")));
    return document;
  }

  function save(document) {
    writeJsonAtomic(sourcesFile, document);
    return document;
  }

  function sourceDir(source) {
    return source.path ? path.resolve(expandRoot(source.path, root)) : null;
  }

  function cacheFor(source) {
    return { dir: path.join(cacheRoot, source.name), index: path.join(cacheRoot, source.name, "index.json"), meta: path.join(cacheRoot, source.name, "meta.json") };
  }

  function ttlMillis(source) {
    const text = String(source.ttl || "").trim();
    if (text === "always") { return 0; }
    if (text === "never") { return Number.MAX_SAFE_INTEGER; }
    const match = /^(\d+)(s|m|h|d)?$/.exec(text);
    if (!match) { return 24 * 3600 * 1000; }
    const amount = Number(match[1]);
    const unit = match[2] || "s";
    return amount * (unit === "d" ? 86400000 : unit === "h" ? 3600000 : unit === "m" ? 60000 : 1000);
  }

  function readCached(source) {
    const cache = cacheFor(source);
    try {
      const index = validateIndex(readJson(cache.index), source.name);
      let meta = {};
      try { meta = readJson(cache.meta); } catch (error) { meta = {}; }
      return { index: index, meta: meta, cache: cache };
    } catch (error) {
      return null;
    }
  }

  function scoopRepo(source) {
    const repo = String(source.repo || "").replace(/^(gh:|github:)/i, "");
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)) {
      throw new SourceError("ERR_BAD_ARGS", "scoop source needs repo owner/name, got: " + String(source.repo || ""), 2);
    }
    return repo;
  }

  function scoopZipUrls(source) {
    if (source.tarballUrl) {
      return [String(source.tarballUrl)];
    }
    const repo = scoopRepo(source);
    if (source.branch) {
      return ["https://github.com/" + repo + "/archive/refs/heads/" + encodeURIComponent(String(source.branch)) + ".zip"];
    }
    return [
      "https://github.com/" + repo + "/archive/refs/heads/main.zip",
      "https://github.com/" + repo + "/archive/refs/heads/master.zip",
    ];
  }

  async function loadScoopIndex(source, flags) {
    const offline = flags.offline === true;
    const refresh = flags.refresh === true;
    const cached = readCached(source);
    const age = cached ? Date.now() - Date.parse(cached.meta.fetchedAt || 0) : Number.MAX_SAFE_INTEGER;
    if (cached && !refresh && age < ttlMillis(source)) {
      return { index: cached.index, source: source, stale: false };
    }
    if (offline) {
      if (cached) { return { index: cached.index, source: source, stale: true }; }
      throw new SourceError("ERR_OFFLINE_MISS", "source " + source.name + " has no cached index and --offline is requested", 23);
    }
    const cache = cacheFor(source);
    fs.mkdirSync(cache.dir, { recursive: true });
    const subdir = String(source.subdir || "bucket").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
    const dest = path.join(cache.dir, "bucket.zip");
    // Unpack only writes inside the stick staging directory; convert there, then clean up.
    const extractDir = path.join(root, "staging", "scoop-" + String(source.name).replace(/[^a-z0-9-]/gi, "_").slice(0, 48));
    let downloaded = false;
    let lastError = null;
    for (const url of scoopZipUrls(source)) {
      try {
        await call("download.fetch", "get", { url: url, dest: dest + ".part", maxBytes: 256 * 1024 * 1024 });
        fs.renameSync(dest + ".part", dest);
        downloaded = true;
        break;
      } catch (error) {
        lastError = error;
        fs.rmSync(dest + ".part", { force: true });
      }
    }
    if (!downloaded) {
      throw lastError || new SourceError("ERR_NET", "could not download scoop bucket for " + source.name, 14);
    }
    fs.rmSync(extractDir, { recursive: true, force: true });
    fs.mkdirSync(extractDir, { recursive: true });
    await call("unpack.extract", "extract", { archive: dest, dest: extractDir, kind: "zip" });
    const manifests = [];
    const roots = fs.readdirSync(extractDir, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => path.join(extractDir, entry.name));
    roots.push(extractDir);
    for (const bucketDir of roots.map((root) => path.join(root, subdir))) {
      let files = [];
      try { files = fs.readdirSync(bucketDir); } catch (error) { continue; }
      for (const file of files.filter((name) => name.toLowerCase().endsWith(".json")).sort()) {
        try {
          manifests.push(scoop.convertManifest(JSON.parse(fs.readFileSync(path.join(bucketDir, file), "utf8")), file));
        } catch (error) {
          manifests.push({ skip: "unreadable " + file });
        }
      }
    }
    const built = scoop.buildIndex(source.name, manifests);
    fs.rmSync(extractDir, { recursive: true, force: true });
    const index = validateIndex(built, source.name);
    writeJsonAtomic(cache.index, index);
    writeJsonAtomic(cache.meta, { schema: 1, fetchedAt: now(), subdir: subdir });
    return { index: index, source: source, stale: false };
  }

  async function loadIndex(source, flags) {
    const offline = flags.offline === true;
    const refresh = flags.refresh === true;
    if (source.kind === "dir") {
      const directory = sourceDir(source);
      if (!directory || !fs.existsSync(directory)) {
        throw new SourceError("ERR_SOURCE_UNREACHABLE", "source " + source.name + " directory does not exist: " + directory, 15);
      }
      const indexFile = path.join(directory, "index.json");
      if (fs.existsSync(indexFile)) {
        return { index: readIndexFile(indexFile, source.name), source: source, stale: false };
      }
      return { index: indexFromDirectory(directory), source: source, stale: false };
    }

    if (source.kind === "scoop") {
      return loadScoopIndex(source, flags);
    }

    if (source.kind === "builtin") {
      const file = source.path ? path.resolve(expandRoot(source.path, root)) : path.join(appRoot, "sources", source.name, "index.json");
      if (!fs.existsSync(file)) {
        throw new SourceError("ERR_SOURCE_UNREACHABLE", "builtin source " + source.name + " is missing: " + file, 15);
      }
      return { index: readIndexFile(file, source.name), source: source, stale: false };
    }

    // http / git: use the cached copy while fresh; otherwise fetch (unless offline).
    const cached = readCached(source);
    const age = cached ? Date.now() - Date.parse(cached.meta.fetchedAt || 0) : Number.MAX_SAFE_INTEGER;
    if (cached && !refresh && age < ttlMillis(source)) {
      return { index: cached.index, source: source, stale: false };
    }
    if (offline) {
      if (cached) { return { index: cached.index, source: source, stale: true }; }
      throw new SourceError("ERR_OFFLINE_MISS", "source " + source.name + " has no cached index and --offline is on; run pd source refresh", 23);
    }

    const cache = cacheFor(source);
    fs.mkdirSync(cache.dir, { recursive: true });
    const dest = cache.index + ".part";
    await call("download.fetch", "get", { url: source.url, dest: dest, maxBytes: 32 * 1024 * 1024 });
    let parsed;
    try {
      parsed = readIndexFile(dest, source.name);
    } catch (error) {
      fs.rmSync(dest, { force: true });
      throw error;
    }
    fs.renameSync(dest, cache.index);
    writeJsonAtomic(cache.meta, { schema: 1, fetchedAt: now(), sha256: hashFile(cache.index), url: source.url });
    return { index: parsed, source: source, stale: false };
  }

  async function resolveSources(flags) {
    const document = load();
    let sources = document.sources.filter((source) => source.enabled !== false);
    if (flags.source) {
      sources = sources.filter((source) => source.name === flags.source);
      if (sources.length === 0) {
        throw new SourceError("ERR_PKG_NOT_FOUND", "source not found: " + flags.source, 20);
      }
    }
    sources = sources.slice().sort((left, right) => (left.priority || 500) - (right.priority || 500));
    return { document: document, sources: sources };
  }

  async function resolve(target, flags) {
    const match = /^([a-z][a-z0-9-]{0,63})(?:@(.+))?$/.exec(String(target || "").trim());
    if (!match) {
      throw new SourceError("ERR_PKG_INVALID", "not a package reference: " + target, 3);
    }
    const id = match[1];
    const constraint = parseConstraint(match[2]);
    const { document, sources } = await resolveSources(flags);
    const channel = flags.channel || setup.defaultChannel || "stable";
    const allowPrerelease = document.allowPrerelease === true || flags.prerelease === true || channel !== "stable";
    const kernelAbi = Number(process.env.PD_KERNEL_ABI || 1);
    const reasons = [];
    const yankedHits = [];

    for (const source of sources) {
      let loaded;
      try {
        loaded = await loadIndex(source, flags);
      } catch (error) {
        reasons.push(source.name + ": " + (error.message || String(error)));
        if (flags["fail-fast"] === true) { throw error; }
        continue;
      }
      const entry = loaded.index.packages[id];
      if (!entry) { continue; }

      const versions = Object.entries(entry.versions || {});
      const viable = [];
      for (const [version, versionEntry] of versions) {
        if (!satisfies(version, constraint)) { continue; }
        if (versionEntry.yanked === true) {
          if (!(constraint.kind === "exact" && flags.force === true)) {
            if (constraint.kind === "exact" && compareVersions(version, constraint.version) === 0) {
              yankedHits.push(source.name + "@" + version);
            }
            continue;
          }
        }
        if (!allowPrerelease && /-/.test(version)) { continue; }
        const versionChannel = versionEntry.channel || loaded.index.channel || "stable";
        if (versionChannel !== channel && constraint.kind !== "exact") { continue; }
        const platform = versionEntry.platform || "win-x64";
        if (platform !== "win-x64") { reasons.push(id + "@" + version + ": platform " + platform); continue; }
        const minKernel = versionEntry.minKernel;
        if (typeof minKernel === "string" && minKernel.startsWith(">=") && kernelAbi < Number(minKernel.slice(2))) {
          reasons.push(id + "@" + version + ": needs kernel ABI " + minKernel);
          continue;
        }
        viable.push({ version: version, entry: versionEntry, source: source, index: loaded.index, stale: loaded.stale });
      }
      if (viable.length === 0) { continue; }

      let chosen;
      if (constraint.kind === "exact") {
        chosen = viable[0];
      } else {
        const latest = entry.latest;
        const latestOk = viable.find((candidate) => candidate.version === latest);
        chosen = latestOk || viable.sort((left, right) => compareVersions(right.version, left.version))[0];
      }

      return {
        id: id,
        version: chosen.version,
        entry: chosen.entry,
        source: source,
        index: chosen.index,
        stale: chosen.stale === true,
        document: document,
        reasons: reasons,
      };
    }

    if (yankedHits.length > 0) {
      throw new SourceError("ERR_PKG_YANKED",
        "version " + id + "@" + constraint.version + " is yanked (" + yankedHits[0] + "); use --force to install it", 22);
    }

    throw new SourceError("ERR_PKG_NOT_FOUND", "package not found in enabled sources: " + id +
      (reasons.length ? " (" + reasons.slice(0, 3).join("; ") + ")" : ""), 20);
  }

  async function materialize(resolved, flags) {
    const sha256 = String(resolved.entry.sha256 || "").toLowerCase();
    const blob = path.join(root, "cache", "blobs", sha256);
    if (fs.existsSync(blob)) {
      return { path: blob, cached: true, sha256: sha256, size: fs.statSync(blob).size };
    }

    const local = resolved.source.kind === "dir" || resolved.source.kind === "builtin";
    if (local) {
      const base = resolved.source.kind === "builtin"
        ? path.dirname(resolved.source.path ? path.resolve(expandRoot(resolved.source.path, root)) : path.join(appRoot, "sources", resolved.source.name, "index.json"))
        : sourceDir(resolved.source);
      const file = path.resolve(base, String(resolved.entry.url));
      if (!file.startsWith(path.resolve(base) + path.sep)) {
        throw new SourceError("ERR_INDEX_INVALID", "index url escapes the source directory: " + resolved.entry.url, 3);
      }
      if (!fs.existsSync(file)) {
        throw new SourceError("ERR_PKG_NOT_FOUND", "package file is missing from source " + resolved.source.name + ": " + file, 20);
      }
      const actual = hashFile(file);
      if (actual !== sha256) {
        throw new SourceError("ERR_HASH", "sha256 mismatch for " + file + ": expected " + sha256 + ", got " + actual, 3);
      }
      fs.mkdirSync(path.dirname(blob), { recursive: true });
      const temp = blob + ".part-" + process.pid;
      fs.copyFileSync(file, temp);
      fs.renameSync(temp, blob);
      return { path: blob, cached: false, sha256: sha256, size: fs.statSync(blob).size };
    }

    if (flags.offline === true) {
      throw new SourceError("ERR_OFFLINE_MISS",
        "package " + resolved.id + " " + resolved.version + " is not cached and --offline is on; run pd fetch " +
        resolved.id + "@" + resolved.version, 23);
    }
    if (!resolved.source.url) {
      throw new SourceError("ERR_SOURCE_UNREACHABLE", "source " + resolved.source.name + " has no url", 15);
    }
    const resolveUrl = (value) => {
      try {
        return new URL(String(value), resolved.source.url).toString();
      } catch (error) {
        return String(value);
      }
    };
    const result = await call("download.fetch", "fetch", {
      url: resolveUrl(resolved.entry.url),
      mirrors: (resolved.entry.mirrors || []).map(resolveUrl),
      sha256: sha256,
      maxBytes: resolved.entry.size ? Math.max(resolved.entry.size, 1024) : undefined,
    });
    return { path: result.path, cached: result.cached === true, sha256: sha256, size: result.bytes };
  }

  return {
    load: load,
    save: save,
    defaults: defaults,
    resolve: resolve,
    materialize: materialize,
    satisfies: satisfies,
    loadIndex: loadIndex,
    validateIndex: validateIndex,
    indexFromDirectory: indexFromDirectory,
    compareVersions: compareVersions,
    parseConstraint: parseConstraint,
    cacheFor: cacheFor,
    sourceDir: sourceDir,
    ttlMillis: ttlMillis,
  };
}

module.exports = { createSourceManager: createSourceManager, SourceError: SourceError, compareVersions: compareVersions, validateIndex: validateIndex };
