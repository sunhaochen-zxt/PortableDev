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
  const arch = (manifest["64bit"] && typeof manifest["64bit"] === "object") ? manifest["64bit"] : manifest;
  const urls = asList(arch.url);
  const hashes = asList(arch.hash);
  const pairs = [];
  for (let index = 0; index < Math.max(urls.length, hashes.length); index += 1) {
    const url = String(urls[index] !== undefined ? urls[index] : (urls[0] || ""));
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
  if (primary.kind === "exe") {
    // A single-file installer cannot be told apart from a portable exe, and running
    // installers silently is out of scope: only manifests without installer scripts qualify.
    const scripted = manifest.installer || manifest.uninstaller || manifest.pre_install || manifest.post_install;
    if (scripted !== undefined && scripted !== null && !(typeof scripted === "object" && Object.keys(scripted).length === 0)) {
      const text = JSON.stringify(scripted);
      if (/script/i.test(text)) {
        return { skip: "installer/uninstaller script not supported" };
      }
    }
  }

  const bins = [];
  for (const bin of asList(manifest.bin)) {
    if (Array.isArray(bin)) {
      if (bin.length === 0) {
        continue;
      }
      bins.push({ path: String(bin[0]), alias: bin.length > 1 ? String(bin[1]) : null });
    } else if (bin !== undefined && bin !== null) {
      bins.push({ path: String(bin), alias: null });
    }
  }

  const extractDir = String(manifest.extract_dir || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
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
  for (const dir of envPathList(manifest.env_add_path)) {
    addPathDir(dir);
  }
  for (const entry of launch) {
    const dir = entry.exe.split("/").slice(0, -1).join("/");
    if (dir) {
      addPathDir(dir);
    }
  }

  const env = {};
  if (manifest.env_set && typeof manifest.env_set === "object" && !Array.isArray(manifest.env_set)) {
    for (const [key, value] of Object.entries(manifest.env_set)) {
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
  const packages = {};
  let skipped = 0;
  for (const item of converted) {
    if (!item || item.skip) {
      skipped += 1;
      continue;
    }
    const bucket = packages[item.id] || (packages[item.id] = { latest: item.version, title: item.entry.title || item.id, versions: {} });
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
