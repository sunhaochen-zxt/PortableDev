// Shared helpers for the PortableDev built-in UI pages.
// All dynamic values are rendered via textContent / DOM APIs (never raw innerHTML).
const PD_STYLES = [
  { id: "winui3", name: "WinUI 3 原生一体", desc: "Windows 11 Fluent 2 云母分层 · 立体描边与底栏指示条" },
  { id: "md3", name: "Material Design 3", desc: "莫奈浅绿取色 · 色调表面与胶囊涟漪" },
  { id: "glass", name: "液态玻璃 Liquid Glass", desc: "纯净中性水晶 · SDF 边缘折射与镜面高光" },
];

(function applyThemeAndStyle() {
  try {
    const savedTheme = sessionStorage.getItem("pd-ui-theme");
    const theme = savedTheme || (window.pd && window.pd.theme) || document.documentElement.dataset.theme || "light";
    document.documentElement.dataset.theme = theme === "dark" ? "dark" : "light";
    const savedStyle = localStorage.getItem("pd-ui-style") || "winui3";
    document.documentElement.dataset.style = ["winui3", "md3", "glass"].includes(savedStyle) ? savedStyle : "winui3";
  } catch (error) { /* keep markup default */ }
})();

function getUiStyle() {
  return document.documentElement.dataset.style || "winui3";
}

function getUiStyleName(id) {
  const hit = PD_STYLES.find((s) => s.id === (id || getUiStyle()));
  return hit ? hit.name : "WinUI 3 原生一体";
}

function runSmoothSwitch(fn) {
  const root = document.documentElement;
  root.classList.add("pd-switching");
  fn();
  clearTimeout(runSmoothSwitch._t);
  runSmoothSwitch._t = setTimeout(() => root.classList.remove("pd-switching"), 200);
}

function setUiStyle(styleId) {
  const valid = ["winui3", "md3", "glass"].includes(styleId) ? styleId : "winui3";
  runSmoothSwitch(() => {
    document.documentElement.dataset.style = valid;
  });
  try { localStorage.setItem("pd-ui-style", valid); } catch (e) { /* ignore */ }
  if (valid === "glass") {
    ensureLiquidGlassFilters();
  }
  showToast("已切换外观风格：" + getUiStyleName(valid), "ok");
  return valid;
}

function cycleUiStyle() {
  const order = ["winui3", "md3", "glass"];
  const next = order[(order.indexOf(getUiStyle()) + 1) % order.length];
  return setUiStyle(next);
}

const PD_ICONS = {
  home: "M3 10.5L12 3l9 7.5V20a1 1 0 0 1-1 1h-5.5v-6.5h-5V21H4a1 1 0 0 1-1-1z",
  box: "M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16zM3.3 7L12 12l8.7-5M12 22V12",
  compass: "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm3.8 6.2l-2.1 5.5-5.5 2.1 2.1-5.5 5.5-2.1z",
  globe: "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zM2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z",
  cpu: "M9 9h6v6H9zM4 9h2M4 15h2M18 9h2M18 15h2M9 4v2M15 4v2M9 18v2M15 18v2M6 6h12v12H6z",
  list: "M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01",
  sliders: "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M2 14h4M10 8h4M18 16h4",
  search: "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zm10 17l-4.35-4.35",
  refresh: "M21 12a9 9 0 1 1-2.64-6.36L21 8M21 3v5h-5",
  sun: "M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10zM12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42",
  moon: "M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z",
  copy: "M9 9h11v11H9zM5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1",
  plus: "M12 5v14M5 12h14",
  trash: "M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14",
  download: "M12 3v12M7 10l5 5 5-5M5 20h14",
  arrowUp: "M12 19V5M5 12l7-7 7 7",
  terminal: "M4 17l6-5-6-5M12 19h8",
  shield: "M12 2l8 4v6c0 5.5-3.8 9.7-8 10-4.2-.3-8-4.5-8-10V6l8-4z",
  check: "M20 6L9 17l-5-5",
};

function makeIcon(name, size) {
  const s = size || 16;
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("width", String(s));
  svg.setAttribute("height", String(s));
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "2");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  const path = document.createElementNS(ns, "path");
  path.setAttribute("d", PD_ICONS[name] || PD_ICONS.box);
  svg.appendChild(path);
  return svg;
}

function hydrateIcons() {
  document.querySelectorAll("[data-icon]").forEach((el) => {
    const name = el.getAttribute("data-icon");
    const size = Number(el.getAttribute("data-size")) || 16;
    el.textContent = "";
    el.appendChild(makeIcon(name, size));
  });
}

function toggleTheme(persist) {
  const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  runSmoothSwitch(() => {
    document.documentElement.dataset.theme = next;
  });
  try { sessionStorage.setItem("pd-ui-theme", next); } catch (e) { /* ignore */ }
  if (window.pd && typeof window.pd.setTheme === "function") {
    window.pd.setTheme(next).catch(() => { /* optional */ });
  }
  if (persist && window.pd && window.pd.run) {
    window.pd.run(["config", "set", "ui.theme", next]).catch(() => { /* optional */ });
  }
  showToast("已切换至" + (next === "dark" ? "深色" : "浅色") + "主题", "ok");
  return next;
}

function showToast(message, kind) {
  let stack = document.getElementById("pd-toasts");
  if (!stack) {
    stack = document.createElement("div");
    stack.id = "pd-toasts";
    stack.className = "toast-stack";
    document.body.appendChild(stack);
  }
  while (stack.children.length >= 4) {
    stack.firstElementChild.remove();
  }
  const el = document.createElement("div");
  el.className = "toast" + (kind ? " " + kind : "");
  el.textContent = String(message || "");
  el.title = "点击关闭";
  stack.appendChild(el);

  let timer = 0;
  let closed = false;
  const dismiss = () => {
    if (closed) return;
    closed = true;
    clearTimeout(timer);
    el.classList.add("is-leaving");
    setTimeout(() => el.remove(), 190);
  };
  const schedule = (ms) => {
    clearTimeout(timer);
    timer = setTimeout(dismiss, ms);
  };
  el.addEventListener("click", dismiss);
  el.addEventListener("pointerenter", () => clearTimeout(timer));
  el.addEventListener("pointerleave", () => schedule(1400));
  schedule(2800);
}

function copyText(text, label) {
  const value = String(text || "");
  const trigger = document.activeElement && document.activeElement.tagName === "BUTTON" ? document.activeElement : null;
  const flashCheck = () => {
    if (!trigger) return;
    const origSvg = trigger.querySelector("svg");
    if (!origSvg) return;
    trigger.classList.add("pd-copied");
    const check = makeIcon("check", Number(origSvg.getAttribute("width")) || 13);
    origSvg.replaceWith(check);
    setTimeout(() => {
      trigger.classList.remove("pd-copied");
      if (check.parentNode) check.replaceWith(origSvg);
    }, 1100);
  };
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(value).then(
      () => { flashCheck(); showToast("已复制" + (label ? " " + label : ""), "ok"); },
      () => showToast("复制失败", "error")
    );
  } else {
    showToast(value);
  }
}

function showTiming(text) {
  let node = document.getElementById("pd-timing");
  if (!node) {
    node = document.createElement("div");
    node.id = "pd-timing";
    document.body.appendChild(node);
  }
  node.textContent = text;
}

async function pdTimed(argv) {
  const start = performance.now();
  const result = await window.pd.run(argv);
  return { result: result, ms: Math.round(performance.now() - start) };
}

function navMs() {
  try {
    const entries = performance.getEntriesByType("navigation");
    if (entries.length > 0) return Math.round(entries[0].domContentLoadedEventEnd);
  } catch (error) { /* ignore */ }
  return -1;
}

function renderError(container, error) {
  const node = document.createElement("div");
  node.className = "error";
  node.textContent = (error && error.code ? error.code + ": " + (error.message || "") : String(error));
  container.appendChild(node);
}

function cell(row, index, className) {
  const td = document.createElement("td");
  if (className) td.className = className;
  td.textContent = row[index] === null || row[index] === undefined ? "" : String(row[index]);
  return td;
}

function extractTable(result) {
  const ev = ((result && result.events) || []).filter((e) => e && e.type === "table").pop();
  return {
    columns: (ev && Array.isArray(ev.columns)) ? ev.columns : [],
    rows: (ev && Array.isArray(ev.rows)) ? ev.rows : [],
    title: (ev && ev.title) || "",
  };
}

function extractKv(result) {
  const ev = ((result && result.events) || []).filter((e) => e && e.type === "kv").pop();
  return (ev && ev.items && typeof ev.items === "object") ? ev.items : {};
}

function confirmDialog(title, message, confirmLabel, danger) {
  return new Promise((resolve) => {
    const prevFocus = document.activeElement;
    const backdrop = document.createElement("div");
    backdrop.className = "modal-backdrop";
    backdrop.setAttribute("role", "dialog");
    backdrop.setAttribute("aria-modal", "true");
    const box = document.createElement("div");
    box.className = "modal-box";
    const h = document.createElement("h3");
    h.className = "modal-title";
    h.textContent = title;
    const p = document.createElement("div");
    p.className = "modal-body";
    p.textContent = message;
    const foot = document.createElement("div");
    foot.className = "modal-foot";

    const cancelBtn = document.createElement("button");
    cancelBtn.type = "button";
    cancelBtn.className = "secondary";
    cancelBtn.textContent = "取消";
    const okBtn = document.createElement("button");
    okBtn.type = "button";
    if (danger) okBtn.className = "danger";
    okBtn.textContent = confirmLabel || "确认";

    let done = false;
    const onKey = (e) => {
      if (e.key === "Escape") {
        e.preventDefault();
        close(false);
      } else if (e.key === "Enter" && document.activeElement !== cancelBtn) {
        e.preventDefault();
        close(true);
      }
    };
    const close = (val) => {
      if (done) return;
      done = true;
      document.removeEventListener("keydown", onKey, true);
      backdrop.classList.add("is-closing");
      setTimeout(() => {
        backdrop.remove();
        if (prevFocus && typeof prevFocus.focus === "function") {
          try { prevFocus.focus(); } catch (e) { /* ignore */ }
        }
        resolve(val);
      }, 125);
    };
    cancelBtn.addEventListener("click", () => close(false));
    okBtn.addEventListener("click", () => close(true));
    backdrop.addEventListener("click", (e) => { if (e.target === backdrop) close(false); });
    document.addEventListener("keydown", onKey, true);

    foot.appendChild(cancelBtn);
    foot.appendChild(okBtn);
    box.appendChild(h);
    box.appendChild(p);
    box.appendChild(foot);
    backdrop.appendChild(box);
    document.body.appendChild(backdrop);
    (danger ? cancelBtn : okBtn).focus();
  });
}

// Run a command with live streaming output into a console container element.
async function streamCommand(argv, consoleEl, onDone) {
  consoleEl.textContent = "";
  const append = (lvl, text) => {
    const row = document.createElement("div");
    row.className = "log-row";
    const tag = document.createElement("span");
    tag.className = "log-lvl " + (lvl || "info");
    tag.textContent = (lvl || "info").slice(0, 5);
    const msg = document.createElement("span");
    msg.className = "log-msg";
    msg.textContent = String(text || "");
    row.appendChild(tag);
    row.appendChild(msg);
    consoleEl.appendChild(row);
    consoleEl.scrollTop = consoleEl.scrollHeight;
  };

  append("info", "$ pd " + argv.join(" "));
  let unsub = null;
  try {
    const taskId = await window.pd.exec(argv);
    await new Promise((resolve) => {
      unsub = window.pd.on("evt:" + taskId, (ev) => {
        if (!ev) return;
        if (ev.type === "log") {
          const rec = ev.record || ev;
          append(rec.level || "info", rec.msg || "");
        } else if (ev.type === "progress") {
          const pct = typeof ev.percent === "number" ? " (" + Math.round(ev.percent) + "%)" : "";
          append("info", (ev.stage || ev.msg || "进行中") + pct);
        } else if (ev.type === "error") {
          append("error", (ev.code ? ev.code + ": " : "") + (ev.message || "执行失败"));
        } else if (ev.type === "done") {
          const ok = (ev.exitCode === 0);
          append(ok ? "info" : "error", (ev.summary || (ok ? "完成" : "退出码 " + ev.exitCode)));
          if (unsub) { try { unsub(); } catch (e) { /* ignore */ } }
          resolve(ev);
        }
      });
    });
    if (onDone) onDone(true);
  } catch (error) {
    if (unsub) { try { unsub(); } catch (e) { /* ignore */ } }
    append("error", (error && error.message) || String(error));
    if (onDone) onDone(false);
  }
}

document.addEventListener("DOMContentLoaded", hydrateIcons);

// Material Web 3 hold-and-release tactile ripple on interactive controls.
document.addEventListener("pointerdown", (e) => {
  if (document.documentElement.dataset.style !== "md3") return;
  const el = e.target && e.target.closest && e.target.closest("button, .quick-tile, .style-option");
  if (!el || el.disabled) return;
  const rect = el.getBoundingClientRect();
  const size = Math.max(rect.width, rect.height);
  const ripple = document.createElement("span");
  ripple.className = "md3-ripple";
  ripple.style.width = size + "px";
  ripple.style.height = size + "px";
  ripple.style.left = (e.clientX - rect.left - size / 2) + "px";
  ripple.style.top = (e.clientY - rect.top - size / 2) + "px";
  el.appendChild(ripple);
  requestAnimationFrame(() => ripple.classList.add("is-expanding"));

  let released = false;
  const release = () => {
    if (released) return;
    released = true;
    ripple.classList.add("is-fading");
    setTimeout(() => ripple.remove(), 260);
  };
  el.addEventListener("pointerup", release, { once: true });
  el.addEventListener("pointerleave", release, { once: true });
  el.addEventListener("pointercancel", release, { once: true });
  setTimeout(release, 650);
});

// Per-card local pointer glow for WinUI 3 Fluent Reveal & Apple Liquid Glass specular sheen.
(function bindSurfacePointerGlow() {
  const selector = ".kpi, .quick-tile, .pkg-card, .style-option";
  let raf = 0;
  let pendingTarget = null;
  let px = 0;
  let py = 0;
  document.addEventListener("pointermove", (e) => {
    const style = document.documentElement.dataset.style || "winui3";
    if (style !== "winui3" && style !== "glass") return;
    const card = e.target && e.target.closest && e.target.closest(selector);
    if (!card) return;
    pendingTarget = card;
    px = e.clientX;
    py = e.clientY;
    if (raf) return;
    raf = requestAnimationFrame(() => {
      raf = 0;
      if (!pendingTarget) return;
      const r = pendingTarget.getBoundingClientRect();
      pendingTarget.style.setProperty("--rx", Math.round(px - r.left) + "px");
      pendingTarget.style.setProperty("--ry", Math.round(py - r.top) + "px");
    });
  }, { passive: true });
})();

// Press "/" or Ctrl+K to focus search; Escape inside search clears or blurs.
document.addEventListener("keydown", (e) => {
  const active = document.activeElement;
  const tag = active && active.tagName;
  const editing = tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
  if (e.key === "Escape" && active && active.tagName === "INPUT" && active.type === "search") {
    if (active.value) {
      active.value = "";
      active.dispatchEvent(new Event("input", { bubbles: true }));
    } else {
      active.blur();
    }
    return;
  }
  if ((e.key === "k" && (e.ctrlKey || e.metaKey)) || (e.key === "/" && !editing)) {
    const search = document.querySelector('input[type="search"]');
    if (search) {
      e.preventDefault();
      search.focus();
      search.select();
    }
  }
});


// Real Liquid Glass optical refraction engine (adapted from shuding/liquid-glass & rdev/liquid-glass-react):
// Generates rounded-rectangle SDF displacement maps on an offscreen canvas and applies a subtle,
// crisp edge refraction + very gentle RGB dispersion via SVG backdrop-filter.
function ensureLiquidGlassFilters() {
  if (document.getElementById("pd-liquid-svg") || !document.body) return;

  function smoothStep(a, b, t) {
    t = Math.max(0, Math.min(1, (t - a) / (b - a)));
    return t * t * (3 - 2 * t);
  }

  function roundedRectSDF(x, y, w, h, r) {
    const qx = Math.abs(x) - w + r;
    const qy = Math.abs(y) - h + r;
    return Math.min(Math.max(qx, qy), 0) + Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) - r;
  }

  function buildDisplacementDataUrl(width, height, radius, edgeBand) {
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext("2d");
    if (!ctx) return { url: "", maxScale: 12 };
    const raw = new Float32Array(width * height * 2);
    let maxScale = 0;
    let p = 0;
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        const ix = x / width - 0.5;
        const iy = y / height - 0.5;
        const dist = roundedRectSDF(ix, iy, 0.36, 0.30, radius);
        const disp = smoothStep(edgeBand, 0, dist - 0.10);
        const scaled = smoothStep(0, 1, disp);
        const dx = (ix * scaled + 0.5) * width - x;
        const dy = (iy * scaled + 0.5) * height - y;
        const m = Math.max(Math.abs(dx), Math.abs(dy));
        if (m > maxScale) maxScale = m;
        raw[p++] = dx;
        raw[p++] = dy;
      }
    }
    maxScale = Math.max(maxScale * 0.5, 1);
    const img = ctx.createImageData(width, height);
    const data = img.data;
    p = 0;
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        const dx = raw[p++];
        const dy = raw[p++];
        const edge = Math.min(1, Math.min(x, y, width - 1 - x, height - 1 - y) / 2);
        const r = Math.max(0, Math.min(255, ((dx * edge) / maxScale + 0.5) * 255));
        const g = Math.max(0, Math.min(255, ((dy * edge) / maxScale + 0.5) * 255));
        const idx = (y * width + x) * 4;
        data[idx] = r;
        data[idx + 1] = g;
        data[idx + 2] = g;
        data[idx + 3] = 255;
      }
    }
    ctx.putImageData(img, 0, 0);
    return { url: canvas.toDataURL(), maxScale: maxScale };
  }

  const ns = "http://www.w3.org/2000/svg";
  const el = (tag, attrs) => {
    const node = document.createElementNS(ns, tag);
    for (const [k, v] of Object.entries(attrs || {})) node.setAttribute(k, String(v));
    return node;
  };

  function makeLiquidFilter(id, mapInfo, scale, aberration) {
    const f = el("filter", {
      id: id,
      x: "-15%",
      y: "-15%",
      width: "130%",
      height: "130%",
      colorInterpolationFilters: "sRGB",
    });
    f.appendChild(el("feImage", {
      x: "0",
      y: "0",
      width: "100%",
      height: "100%",
      preserveAspectRatio: "none",
      href: mapInfo.url,
      result: "MAP",
    }));

    const addChannel = (name, chScale, matrix) => {
      f.appendChild(el("feDisplacementMap", {
        in: "SourceGraphic",
        in2: "MAP",
        scale: chScale.toFixed(2),
        xChannelSelector: "R",
        yChannelSelector: "G",
        result: name + "_D",
      }));
      f.appendChild(el("feColorMatrix", {
        in: name + "_D",
        type: "matrix",
        values: matrix,
        result: name,
      }));
    };

    addChannel("R", scale, "1 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 1 0");
    addChannel("G", scale * (1 - aberration), "0 0 0 0 0  0 1 0 0 0  0 0 0 0 0  0 0 0 1 0");
    addChannel("B", scale * (1 - aberration * 2), "0 0 0 0 0  0 0 0 0 0  0 0 1 0 0  0 0 0 1 0");

    f.appendChild(el("feBlend", { in: "G", in2: "B", mode: "screen", result: "GB" }));
    f.appendChild(el("feBlend", { in: "R", in2: "GB", mode: "screen", result: "RGB" }));
    return f;
  }

  const svg = el("svg", { id: "pd-liquid-svg", width: "0", height: "0", "aria-hidden": "true" });
  svg.style.cssText = "position:fixed;width:0;height:0;pointer-events:none;";
  const defs = el("defs");

  const lensMap = buildDisplacementDataUrl(180, 120, 0.34, 0.65);
  const cardMap = buildDisplacementDataUrl(220, 140, 0.22, 0.50);
  defs.appendChild(makeLiquidFilter("pd-liquid-lens", lensMap, Math.min(20, lensMap.maxScale * 0.75), 0.04));
  defs.appendChild(makeLiquidFilter("pd-liquid-card", cardMap, Math.min(12, cardMap.maxScale * 0.50), 0.025));

  svg.appendChild(defs);
  document.body.appendChild(svg);
}

(function bindLiquidGlassLight() {
  let raf = 0;
  let lastX = 0;
  let lastY = 0;
  document.addEventListener("pointermove", (e) => {
    if (document.documentElement.dataset.style !== "glass") return;
    lastX = e.clientX;
    lastY = e.clientY;
    if (raf) return;
    raf = requestAnimationFrame(() => {
      raf = 0;
      const nx = (lastX / Math.max(1, window.innerWidth)) - 0.5;
      const ny = (lastY / Math.max(1, window.innerHeight)) - 0.5;
      const angle = (135 + nx * 44 + ny * 22).toFixed(1) + "deg";
      document.documentElement.style.setProperty("--glass-angle", angle);
      document.documentElement.style.setProperty("--gx", Math.round((nx + 0.5) * 100) + "%");
      document.documentElement.style.setProperty("--gy", Math.round((ny + 0.5) * 100) + "%");
    });
  }, { passive: true });

  document.addEventListener("DOMContentLoaded", () => {
    if (document.documentElement.dataset.style === "glass") {
      ensureLiquidGlassFilters();
    }
  });
})();
