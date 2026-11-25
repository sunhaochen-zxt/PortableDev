// Download extension: http(s) URL -> cache/blobs/<sha256>, hash verified, mirrors tried in order.
const fs = require("fs");
const path = require("path");
const http = require("http");
const https = require("https");
const crypto = require("crypto");
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
  return parsed;
}

function downloadHttp(url, target, expected, timeoutMs, maxBytes) {
  return new Promise((resolve, reject) => {
    const temp = target + ".part";
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

        if (status < 200 || status >= 400) {
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
            if (actual !== expected) {
              cleanup();
              finish(reject, new DownloadError("ERR_HASH", "hash mismatch for " + currentUrl + ": expected " + expected + ", got " + actual));
              return;
            }
            fs.renameSync(temp, target);
            finish(resolve, fs.statSync(target).size);
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

async function fetchBlob(args) {
  const sha256 = String(args.sha256 || "").toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(sha256)) {
    throw new DownloadError("ERR_BAD_ARGS", "fetch needs a 64-char sha256");
  }

  const target = path.join(BLOBS, sha256);
  if (fs.existsSync(target)) {
    const stat = fs.statSync(target);
    if (stat.isFile() && stat.size > 0) {
      return { path: target, bytes: stat.size, cached: true, sha256 };
    }
    fs.rmSync(target, { force: true });
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
      const bytes = await downloadHttp(url, target, sha256, timeoutMs, maxBytes);
      return { path: target, bytes, cached: false, sha256, url };
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
    if (capability !== "download.fetch" || op !== "fetch") {
      send({ v: 1, kind: "call.err", id, data: { error: { code: "ERR_UNSUPPORTED", message: "unknown " + capability + "/" + op } } });
      return;
    }

    const result = await fetchBlob(args);
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
