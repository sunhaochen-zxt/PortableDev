// Download extension: http(s) URL -> cache/blobs/<sha256>, hash verified, mirrors tried in order.
const fs = require("fs");
const path = require("path");
const http = require("http");
const https = require("https");
const crypto = require("crypto");
const { spawnSync } = require("child_process");
const { connect, ready } = require("./protocol");

const ROOT = process.env.PD_ROOT || ".";
const BLOBS = path.join(ROOT, "cache", "blobs");
fs.mkdirSync(BLOBS, { recursive: true });

const DEFAULT_MAX_BYTES = 4 * 1024 * 1024 * 1024;
const MAX_REDIRECTS = 5;

function DownloadError(code, message) {
  this.code = code;
  this.message = message;
}

function parseTarget(url) {
  let parsed;
  try {
    parsed = new URL(String(url));
  } catch (error) {
    throw new DownloadError("ERR_BAD_ARGS", "not a valid url: " + String(url));
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new DownloadError("ERR_UNSUPPORTED", "only http(s) URLs can be fetched: " + String(url));
  }
  assertNotInternal(parsed);
  return parsed;
}

// Refuse link-local metadata endpoints and RFC1918 targets so a hostile index cannot use the
// user's machine as a proxy into its LAN. Loopback stays allowed (local mirrors and tests); set
// PD_DOWNLOAD_ALLOW_PRIVATE=1 to opt out for a private mirror.
function assertNotInternal(parsed) {
  if (process.env.PD_DOWNLOAD_ALLOW_PRIVATE === "1") {
    return;
  }

  const host = String(parsed.hostname || "").toLowerCase().replace(/^\[|\]$/g, "");
  if (host === "localhost" || host === "::1" || host.startsWith("127.")) {
    return;
  }

  const octets = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
  if (!octets) {
    return;
  }

  const [first, second] = [Number(octets[1]), Number(octets[2])];
  const internal = first === 10 ||
    (first === 172 && second >= 16 && second <= 31) ||
    (first === 192 && second === 168) ||
    (first === 169 && second === 254) ||
    first === 0;
  if (internal) {
    throw new DownloadError("ERR_UNSUPPORTED", "refusing to fetch an internal address: " + host +
      " (set PD_DOWNLOAD_ALLOW_PRIVATE=1 for a private mirror)");
  }
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


// ---- proxy support ----
// Node's http/https do not use the Windows system proxy, so a machine that relies on a local
// proxy (FlClash/Clash/V2Ray) would fail on release assets that need it. When a proxy is
// configured (env HTTP(S)_PROXY, settings network.proxy, or the WinINET system proxy) the
// download goes through the bundled curl.exe, which handles CONNECT, TLS and redirects.
let proxyResolved = false;
let proxyValue = null;

function proxyFromText(text) {
  const raw = String(text || "").trim();
  if (!raw) {
    return null;
  }
  const match = /^(?:https?:\/\/)?([^:]+):(\d{1,5})$/.exec(raw);
  if (!match) {
    return null;
  }
  return { host: match[1], port: Number(match[2]) };
}

function proxyBypassed(host) {
  const plain = String(host || "").toLowerCase().replace(/^\[|\]$/g, "");
  if (plain === "localhost" || plain === "::1" || plain.startsWith("127.")) {
    return true;
  }
  const noProxy = String(process.env.NO_PROXY || process.env.no_proxy || "");
  for (const part of noProxy.split(",").map((item) => item.trim().toLowerCase()).filter(Boolean)) {
    if (part === "*" || plain === part.replace(/^\./, "") || plain.endsWith(part)) {
      return true;
    }
  }
  return false;
}

function readSystemProxy() {
  try {
    const powershell = require("path").join(process.env.SystemRoot || "C:\\Windows",
      "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    const script = "$p = Get-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings'; if ($p.ProxyEnable -eq 1) { $p.ProxyServer } else { '' }";
    const out = require("child_process").execFileSync(powershell,
      ["-NoProfile", "-NonInteractive", "-Command", script],
      { encoding: "utf8", timeout: 8000, windowsHide: true }).trim();
    if (!out) {
      return null;
    }
    const map = {};
    for (const part of out.split(";")) {
      const index = part.indexOf("=");
      if (index > 0) {
        map[part.slice(0, index).trim().toLowerCase()] = part.slice(index + 1).trim();
      }
    }
    return proxyFromText(map.https || map.http || out.split(";")[0]);
  } catch (error) {
    return null;
  }
}

function resolveProxy() {
  if (proxyResolved) {
    return proxyValue;
  }
  proxyResolved = true;
  // network.proxy follows kernel.md: a URL = explicit proxy, "none" = never, "system"/absent =
  // use the environment or the WinINET system proxy.
  let setting = null;
  try {
    const settings = JSON.parse(fs.readFileSync(path.join(ROOT, "config", "settings.json"), "utf8"));
    if (settings && settings.network && settings.network.proxy !== undefined) {
      setting = String(settings.network.proxy).trim();
    }
  } catch (error) {
    // no settings yet
  }
  if (setting) {
    if (setting.toLowerCase() === "none") {
      proxyValue = null;
      return proxyValue;
    }
    if (setting.toLowerCase() !== "system") {
      proxyValue = proxyFromText(setting);
      if (proxyValue) {
        return proxyValue;
      }
    }
  }
  const env = process.env;
  const explicit = [env.PD_HTTP_PROXY, env.HTTPS_PROXY, env.https_proxy, env.HTTP_PROXY, env.http_proxy, env.ALL_PROXY, env.all_proxy];
  for (const candidate of explicit) {
    proxyValue = proxyFromText(candidate);
    if (proxyValue) {
      return proxyValue;
    }
  }
  proxyValue = readSystemProxy();
  return proxyValue;
}

function curlDownload(url, target, expected, timeoutMs, maxBytes, proxy) {
  const curl = path.join(process.env.SystemRoot || "C:\\Windows", "System32", "curl.exe");
  const temp = target + ".part-" + process.pid + "-" + crypto.randomBytes(4).toString("hex");
  const budgetSeconds = Math.max(Math.ceil(timeoutMs / 1000), 900);
  const args = [
    "-sS", "-L", "--fail", "--connect-timeout", String(Math.min(30, budgetSeconds)),
    "--max-time", String(budgetSeconds), "--max-filesize", String(maxBytes),
    "--proxy", "http://" + proxy.host + ":" + proxy.port,
    "-o", temp, url,
  ];
  const result = spawnSync(curl, args, { timeout: (budgetSeconds + 15) * 1000, windowsHide: true, encoding: "utf8" });
  try {
    if (result.error) {
      throw new DownloadError("ERR_NET", "curl failed: " + result.error.message);
    }
    if (result.status !== 0) {
      const detail = String(result.stderr || "").trim().split(/\r?\n/).filter(Boolean).slice(-1)[0] || "";
      throw new DownloadError("ERR_NET", "curl exited with " + result.status + (detail ? ": " + detail.slice(0, 200) : ""));
    }
    const stat = fs.statSync(temp);
    if (!stat.isFile() || stat.size <= 0) {
      throw new DownloadError("ERR_NET", "empty download: " + url);
    }
    if (stat.size > maxBytes) {
      throw new DownloadError("ERR_UNSUPPORTED", "download is larger than " + maxBytes + " bytes: " + url);
    }
    const actual = hashFile(temp);
    if (expected !== null && expected !== undefined && actual !== expected) {
      throw new DownloadError("ERR_HASH", "hash mismatch for " + url + ": expected " + expected + ", got " + actual);
    }
    fs.renameSync(temp, target);
    return { bytes: stat.size, sha256: actual, proxy: true };
  } finally {
    try {
      fs.rmSync(temp, { force: true });
    } catch (error) {
      // best effort
    }
  }
}
function downloadHttp(url, target, expected, timeoutMs, maxBytes) {
  const proxy = proxyBypassed(new URL(url).hostname) ? null : resolveProxy();
  if (proxy) {
    return Promise.resolve().then(() => curlDownload(url, target, expected, timeoutMs, maxBytes, proxy));
  }
  return new Promise((resolve, reject) => {
    // Unique temp name: two concurrent fetches of the same blob must not interleave writes.
    const temp = target + ".part-" + process.pid + "-" + crypto.randomBytes(4).toString("hex");
    let settled = false;
    let redirects = 0;

    const finish = (callback, value) => {
      if (settled) {
        return;
      }
      settled = true;
      callback(value);
    };

    const cleanup = () => {
      try {
        fs.rmSync(temp, { force: true });
      } catch (error) {
        // best effort
      }
    };

    const attempt = (currentUrl) => {
      const parsed = parseTarget(currentUrl);
      const client = parsed.protocol === "https:" ? https : http;
      const request = client.get(parsed, { timeout: timeoutMs }, (response) => {
        const status = response.statusCode || 0;

        if (status >= 300 && status < 400 && response.headers.location) {
          response.resume();
          redirects += 1;
          if (redirects > MAX_REDIRECTS) {
            finish(reject, new DownloadError("ERR_NET", "too many redirects for " + url));
            return;
          }
          const next = new URL(response.headers.location, currentUrl).toString();
          if (parsed.protocol === "https:" && next.startsWith("http:")) {
            finish(reject, new DownloadError("ERR_NET", "refusing to downgrade https to http: " + next));
            return;
          }
          attempt(next);
          return;
        }

        if (status < 200 || status >= 300) {
          // Covers 3xx without Location (a redirect we cannot follow) and every error status.
          response.resume();
          finish(reject, new DownloadError("ERR_SOURCE_UNREACHABLE", "http " + status + " for " + currentUrl));
          return;
        }

        const declared = Number(response.headers["content-length"]);
        if (Number.isFinite(declared) && declared > maxBytes) {
          response.resume();
          finish(reject, new DownloadError("ERR_UNSUPPORTED", "download is larger than " + maxBytes + " bytes: " + currentUrl));
          return;
        }

        const out = fs.createWriteStream(temp);
        const digest = crypto.createHash("sha256");
        let bytes = 0;

        const fail = (error) => {
          response.destroy();
          out.destroy();
          cleanup();
          finish(reject, error);
        };

        response.on("data", (chunk) => {
          bytes += chunk.length;
          if (bytes > maxBytes) {
            fail(new DownloadError("ERR_UNSUPPORTED", "download exceeded " + maxBytes + " bytes: " + currentUrl));
            return;
          }
          digest.update(chunk);
          if (!out.write(chunk)) {
            response.pause();
            out.once("drain", () => response.resume());
          }
        });
        response.on("aborted", () => fail(new DownloadError("ERR_NET", "connection aborted while fetching " + currentUrl)));
        response.on("error", (error) => fail(new DownloadError("ERR_NET", error.message)));
        response.on("end", () => {
          out.end(() => {
            const actual = digest.digest("hex");
            if (expected !== null && expected !== undefined && actual !== expected) {
              cleanup();
              finish(reject, new DownloadError("ERR_HASH", "hash mismatch for " + currentUrl + ": expected " + expected + ", got " + actual));
              return;
            }
            fs.renameSync(temp, target);
            finish(resolve, { bytes: bytes, sha256: actual });
          });
        });
      });

      request.on("timeout", () => request.destroy(new DownloadError("ERR_NET", "timeout fetching " + currentUrl)));
      request.on("error", (error) => {
        cleanup();
        finish(reject, error instanceof DownloadError ? error : new DownloadError("ERR_NET", error.message));
      });
    };

    attempt(url);
  });
}

function insideRoot(candidate) {
  const relative = path.relative(path.resolve(ROOT), path.resolve(candidate));
  return relative.length > 0 && !relative.startsWith("..") && !path.isAbsolute(relative);
}

// Untrusted fetch: no expected hash (used for index/metadata files). The caller validates content.
async function fetchToPath(args) {
  const url = String(args.url || "");
  parseTarget(url);
  const dest = path.resolve(String(args.dest || ""));
  if (!insideRoot(dest)) {
    throw new DownloadError("ERR_UNSUPPORTED", "destination must be inside the stick root: " + dest);
  }

  const maxBytes = Number(args.maxBytes) > 0 ? Number(args.maxBytes) : 32 * 1024 * 1024;
  const timeoutMs = Number(args.timeoutMs) > 0 ? Number(args.timeoutMs) : 60000;
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  const downloaded = await downloadHttp(url, dest, null, timeoutMs, maxBytes);
  return { path: dest, bytes: downloaded.bytes, sha256: downloaded.sha256 };
}

async function fetchBlob(args) {
  const sha256 = String(args.sha256 || "").toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(sha256)) {
    throw new DownloadError("ERR_BAD_ARGS", "fetch needs a 64-char sha256");
  }

  const target = path.join(BLOBS, sha256);
  if (fs.existsSync(target)) {
    const stat = fs.statSync(target);
    if (stat.isFile() && stat.size > 0) {
      // A cached blob must still hash to its own name: disk corruption or a tampered cache
      // would otherwise be trusted forever (index-spec section 7 requires re-verification).
      const actual = hashFile(target);
      if (actual === sha256) {
        return { path: target, bytes: stat.size, cached: true, sha256 };
      }
      fs.rmSync(target, { force: true });
    } else {
      fs.rmSync(target, { force: true });
    }
  }

  if (args.offline === true) {
    throw new DownloadError("ERR_OFFLINE_MISS", "blob " + sha256 + " is not cached and --offline was requested");
  }

  const urls = [];
  if (args.url) {
    urls.push(String(args.url));
  }
  for (const mirror of args.mirrors || []) {
    urls.push(String(mirror));
  }
  if (urls.length === 0) {
    throw new DownloadError("ERR_BAD_ARGS", "fetch needs a url");
  }

  const timeoutMs = Number(args.timeoutMs) > 0 ? Number(args.timeoutMs) : 60000;
  const maxBytes = Number(args.maxBytes) > 0 ? Number(args.maxBytes) : DEFAULT_MAX_BYTES;
  let lastError = null;
  for (const url of urls) {
    try {
      parseTarget(url);
      const downloaded = await downloadHttp(url, target, sha256, timeoutMs, maxBytes);
      return { path: target, bytes: downloaded.bytes, cached: false, sha256, url };
    } catch (error) {
      lastError = error;
    }
  }

  throw lastError || new DownloadError("ERR_NET", "no url could be fetched");
}

let send = () => {};

async function handleCall(message) {
  const id = message.id;
  const capability = message.data && message.data.cap;
  const op = (message.data && message.data.op) || "";
  const args = (message.data && message.data.args) || {};
  try {
    if (capability !== "download.fetch" || (op !== "fetch" && op !== "get")) {
      send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_UNSUPPORTED", message: "unknown " + capability + "/" + op } } });
      return;
    }

    const result = op === "get" ? await fetchToPath(args) : await fetchBlob(args);
    send({ v: 1, kind: "call.ok", id, data: { result } });
  } catch (error) {
    send({ v: 1, kind: "call.err", id, data: { error: { code: error.code || "ERR_NET", message: error.message || String(error) } } });
  }
}

connect({
  onMessage(message, outbound) {
    send = outbound;
    if (message.kind === "hello.ok") {
      ready(send, [{ name: "download.fetch", version: 1, mutates: true }]);
      return;
    }
    if (message.kind === "call") {
      handleCall(message);
    }
  },
});
