// Scoop bucket manifest -> PortableDev index entry conversion.
//
// A scoop manifest describes an upstream installer (url + hash + launchers).
// We accept only the subset that maps 1:1 onto our model (zip archives and
// single-file portable exes); anything needing installer scripts, driver
// installs or post-processing is skipped with a reason (see SKIP_* below).
// The converter is pure: no fs, no network, no kernel calls, so it is unit
// testable with plain node.
"use strict";

const VERSION = /^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$/;
const ID = /^[a-z][a-z0-9-]{0,63}$/;
const SAFE_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

function asList(value) {
  if (value === undefined || value === null) {
    return [];
  }
  return Array.isArray(value) ? value.slice() : [value];
}

function hashAlgo(hash) {
  const text = String(hash || "").trim().toLowerCase();
  const match = /^(sha256|sha512|sha1|md5):([0-9a-f]+)$/.exec(text);
  if (match) {
    return { algo: match[1], digest: match[2] };
  }
  if (/^[0-9a-f]{64}$/.test(text)) {
    return { algo: "sha256", digest: text };
  }
  if (/^[0-9a-f]{128}$/.test(text)) {
    return { algo: "sha512", digest: text };
  }
  return null;
}

function installerKind(url) {
  const lower = String(url || "").toLowerCase().split(/[?#]/)[0];
  if (lower.endsWith(".zip")) {
    return "zip";
  }
  if (lower.endsWith(".exe")) {
    return "exe";
  }
  return null;
}

function baseName(path) {
  return String(path || "").split(/[\\/]/).filter(Boolean).pop() || "";
}

function launchName(aliasOrPath) {
  const base = baseName(aliasOrPath).replace(/\.(exe|cmd|bat|ps1)$/i, "");
  const clean = base.replace(/[^A-Za-z0-9._-]/g, "");
  if (!SAFE_NAME.test(clean)) {
    return null;
  }
  return clean;
}

function envPathList(value) {
  const parts = Array.isArray(value) ? value.slice() : String(value || "").split(";");
  return parts.map((part) => String(part).trim()).filter((part) => part.length > 0 && !/^[A-Za-z]:[\\/]|^\\\\/.test(part));
}

function dependsOf(manifest) {
  const raw = manifest.depends;
  const items = Array.isArray(raw) ? raw : String(raw || "").split(",");
  const out = [];
  const seen = new Set();
  for (const item of items) {
    const id = String(item || "").trim().toLowerCase();
    if (!id || seen.has(id)) {
      continue;
    }
    seen.add(id);
    if (!ID.test(id)) {
      return { error: "bad depends id: " + item };
    }
    out.push({ id: id });
  }
  return { requires: out };
}

// Converts one scoop manifest to our index version entry.
// Returns { id, version, entry } or { skip: "<reason>" }.
function convertManifest(manifest, manifestName) {
  if (!manifest || typeof manifest !== "object" || Array.isArray(manifest)) {
    return { skip: "not an object" };
  }

  const id = String(manifestName || "").toLowerCase().replace(/\.json$/, "");
  if (!ID.test(id)) {
    return { skip: "bad bucket file name: " + manifestName };
  }

  const version = String(manifest.version || "").trim();
  if (!VERSION.test(version)) {
    return { skip: "bad version: " + String(manifest.version || "") };
  }

  // The bucket may carry a 64-bit variant; plain top-level fields are the fallback.
  // Buckets use both {"64bit": {...}} and {"architecture": {"64bit": {...}}}.
  const structure = manifest.architecture && typeof manifest.architecture === "object" && !Array.isArray(manifest.architecture)
    ? manifest.architecture : null;
  const direct = manifest["64bit"] && typeof manifest["64bit"] === "object" && !Array.isArray(manifest["64bit"])
    ? manifest["64bit"] : null;
  // Prefer the 64-bit entry, then a 32-bit-only bucket (works on x64), then the flat top-level fields.
  const arch = (structure && (structure["64bit"] || structure["32bit"])) || direct || manifest;
  const pick = (key) => (arch && arch[key] !== undefined ? arch[key] : manifest[key]);
  const urls = asList(pick("url"));
  const hashes = asList(pick("hash"));
  const pairs = [];
  for (let index = 0; index < Math.max(urls.length, hashes.length); index += 1) {
    const raw = String(urls[index] !== undefined ? urls[index] : (urls[0] || ""));
    // Scoop uses "#/dl.zip" fragments to hint the download name; the fragment is not fetched.
    const url = raw.split("#")[0];
    const parsed = hashAlgo(hashes[index] !== undefined ? hashes[index] : hashes[0]);
    if (!parsed || parsed.algo !== "sha256") {
      continue;
    }
    const kind = installerKind(url);
    if (!url || !kind) {
      continue;
    }
    pairs.push({ url: url, sha256: parsed.digest, kind: kind });
  }
  if (pairs.length === 0) {
    return { skip: "no usable url+sha256 pair (need .zip/.exe with sha256)" };
  }

  const primary = pairs[0];

  // Any lifecycle hook means the bucket expects code we never run: the installed tree would be
  // incomplete, so skip the package instead of installing something broken. This applies to
  // every installer kind (zip manifests carry pre/post install hooks too).
  // shortcuts/psmodulepath are features we cannot honor; installing without them silently
  // produces an incomplete package, so skip those manifests too.
  // persist = user data directories Scoop keeps across updates; this installer has no persist
  // primitive yet, so installing silently would lose profiles. Skip it honestly for now.
  const persist = pick("persist");
  if (Array.isArray(persist) ? persist.length > 0 : (persist !== undefined && persist !== null && String(persist).trim() !== "")) {
    return { skip: "manifest declares persist user data, which this installer cannot keep across updates" };
  }
  for (const hook of ["installer", "uninstaller", "pre_install", "post_install", "pre_uninstall", "post_uninstall", "pre_download", "post_download", "shortcuts", "psmodulepath"]) {
    const value = manifest[hook];
    if (value === undefined || value === null) {
      continue;
    }
    if (typeof value === "string" && value.trim() === "") {
      continue;
    }
    if (Array.isArray(value) && value.length === 0) {
      continue;
    }
    if (typeof value === "object" && !Array.isArray(value) && Object.keys(value).length === 0) {
      continue;
    }
    return { skip: "manifest carries " + hook + " hooks, which this installer cannot run" };
  }

  const bins = [];
  for (const bin of asList(pick("bin"))) {
    if (Array.isArray(bin)) {
      if (bin.length === 0) {
        continue;
      }
      bins.push({ path: String(bin[0]), alias: bin.length > 1 ? String(bin[1]) : null });
    } else if (bin !== undefined && bin !== null) {
      bins.push({ path: String(bin), alias: null });
    }
  }

  const extractDir = String(pick("extract_dir") || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
  const launch = [];
  for (const bin of bins) {
    if (!bin.path) {
      continue;
    }
    const name = launchName(bin.alias || bin.path);
    if (!name) {
      continue;
    }
    const rel = (extractDir ? extractDir + "/" : "") + String(bin.path).replace(/\\/g, "/").replace(/^\/+/, "");
    if (rel.split("/").includes("..")) {
      continue;
    }
    launch.push({ name: name, exe: rel });
  }
  if (primary.kind === "exe" && launch.length === 0) {
    launch.push({ name: id, exe: "bin/" + baseName(primary.url) });
  }
  if (launch.length === 0) {
    return { skip: "no usable bin entry" };
  }

  const pathDirs = [];
  const pathSeen = new Set();
  const addPathDir = (dir) => {
    const clean = String(dir || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
    if (!clean || clean.split("/").includes("..") || pathSeen.has(clean)) {
      return;
    }
    pathSeen.add(clean);
    pathDirs.push(clean);
  };
  addPathDir("bin");
  for (const dir of envPathList(pick("env_add_path"))) {
    addPathDir(dir);
  }
  for (const entry of launch) {
    const dir = entry.exe.split("/").slice(0, -1).join("/");
    if (dir) {
      addPathDir(dir);
    }
  }

  const env = {};
  const envSet = pick("env_set");
  if (envSet && typeof envSet === "object" && !Array.isArray(envSet)) {
    for (const [key, value] of Object.entries(envSet)) {
      env[String(key)] = String(value);
    }
  }

  const depends = dependsOf(manifest);
  if (depends.error) {
    return { skip: depends.error };
  }

  const entry = {
    url: primary.url,
    sha256: primary.sha256,
    requires: depends.requires,
    scoop: {
      kind: primary.kind,
      manifest: id + ".json",
      launch: launch,
      path: pathDirs,
      env: env,
      exeFile: primary.kind === "exe" ? baseName(primary.url) : null,
    },
  };
  if (Array.isArray(manifest.description) ? manifest.description.length > 0 : manifest.description) {
    entry.title = String(manifest.description).split("\n")[0].slice(0, 120);
  }
  if (manifest.homepage) {
    entry.homepage = String(manifest.homepage);
  }
  return { id: id, version: version, entry: entry };
}

// Groups converted manifests into our index.json shape.
function buildIndex(name, converted) {
  // Prototype-less map: a bucket file such as constructor.json must not hit Object.prototype.
  const packages = Object.create(null);
  let skipped = 0;
  for (const item of converted) {
    if (!item || item.skip) {
      skipped += 1;
      continue;
    }
    const bucket = packages[item.id] || (packages[item.id] = { latest: item.version, title: item.entry.title || item.id, versions: Object.create(null) });
    if (item.entry.title && bucket.title === item.id) {
      bucket.title = item.entry.title;
    }
    bucket.versions[item.version] = {
      url: item.entry.url,
      sha256: item.entry.sha256,
      requires: item.entry.requires,
      scoop: item.entry.scoop,
    };
    if (item.entry.homepage && !bucket.homepage) {
      bucket.homepage = item.entry.homepage;
    }
  }
  for (const bucket of Object.values(packages)) {
    let latest = null;
    for (const version of Object.keys(bucket.versions)) {
      if (latest === null || compareSimple(version, latest) > 0) {
        latest = version;
      }
    }
    bucket.latest = latest;
  }
  return { schema: 1, name: name, packages: packages, skipped: skipped };
}

function compareSimple(left, right) {
  const parts = (value) => String(value).split(".").map((part) => (/^\d+$/.test(part) ? Number(part) : part));
  const a = parts(left);
  const b = parts(right);
  const length = Math.max(a.length, b.length);
  for (let index = 0; index < length; index += 1) {
    const x = a[index] === undefined ? 0 : a[index];
    const y = b[index] === undefined ? 0 : b[index];
    if (x === y) {
      continue;
    }
    if (typeof x === "number" && typeof y === "number") {
      return x < y ? -1 : 1;
    }
    return String(x) < String(y) ? -1 : 1;
  }
  return 0;
}

module.exports = { convertManifest, buildIndex, compareSimple };
