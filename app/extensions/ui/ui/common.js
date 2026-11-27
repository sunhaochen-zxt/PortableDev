// Shared helpers for the PortableDev built-in UI pages.
// All dynamic values are rendered via textContent / DOM APIs (never raw innerHTML).
const PD_STYLES = [
  { id: "winui3", name: "WinUI 3 原生一体", desc: "WinUIonWeb 真实主题资源 · 云母分层与立体描边" },
  { id: "md3", name: "Material Design 3", desc: "官方 @material/web 组件 · 壁纸莫奈动态取色与真实涟漪" },
  { id: "glass-chrome", name: "悬浮液态玻璃（FlClash）", desc: "内容区保持清爽莫奈实体卡片 · 仅悬浮控件与弹窗启用液态玻璃" },
  { id: "glass", name: "全景液态玻璃", desc: "WebGL2 物理折射与色散 · 自定义壁纸全屏通透玻璃" },
];

function syncHtmlThemeClasses(theme) {
  const isDark = theme === "dark";
  document.documentElement.dataset.theme = isDark ? "dark" : "light";
  document.documentElement.classList.toggle("theme-dark", isDark);
  document.documentElement.classList.toggle("theme-light", !isDark);
}

(function applyThemeAndStyle() {
  try {
    const savedTheme = sessionStorage.getItem("pd-ui-theme");
    const theme = savedTheme || (window.pd && window.pd.theme) || document.documentElement.dataset.theme || "light";
    syncHtmlThemeClasses(theme === "dark" ? "dark" : "light");
    const savedStyle = localStorage.getItem("pd-ui-style") || "winui3";
    document.documentElement.dataset.style = ["winui3", "md3", "glass-chrome", "glass"].includes(savedStyle) ? savedStyle : "winui3";
  } catch (error) { /* keep markup default */ }
})();

// The window shell pushes theme changes (settings save, tray/settings, config set).
window.addEventListener("pd-theme", (event) => {
  try {
    const theme = event && event.detail === "dark" ? "dark" : "light";
    syncHtmlThemeClasses(theme);
    try { sessionStorage.setItem("pd-ui-theme", theme); } catch (error) { /* ignore */ }
    if (window.PDWallpaper && typeof window.PDWallpaper.applyCurrent === "function") window.PDWallpaper.applyCurrent();
    if (typeof window.pdThemeChanged === "function") window.pdThemeChanged(theme);
  } catch (error) { /* ignore */ }
});

const _loadedScripts = Object.create(null);
function loadVendorScript(src) {
  if (_loadedScripts[src]) return _loadedScripts[src];
  _loadedScripts[src] = new Promise((resolve, reject) => {
    const s = document.createElement("script");
    s.src = src;
    s.onload = () => resolve();
    s.onerror = (err) => reject(err);
    document.head.appendChild(s);
  });
  return _loadedScripts[src];
}

// Mount/unmount real @material/web Custom Elements (<md-ripple>, <md-elevation>, <md-focus-ring>)
let _md3Observer = null;
function decorateMd3Node(el) {
  if (!el || el.nodeType !== 1 || el.dataset.md3Mounted === "1") return;
  el.dataset.md3Mounted = "1";
  if (el.matches("button, .quick-tile, .style-option, .tab-btn")) {
    const ripple = document.createElement("md-ripple");
    ripple.setAttribute("data-pd-mwc", "1");
    el.appendChild(ripple);
    const ring = document.createElement("md-focus-ring");
    ring.setAttribute("data-pd-mwc", "1");
    el.appendChild(ring);
  }
  if (el.matches(".kpi, .pkg-card, .modal-box")) {
    const elev = document.createElement("md-elevation");
    elev.setAttribute("data-pd-mwc", "1");
    el.appendChild(elev);
  }
}

function mountMaterialWeb() {
  loadVendorScript("vendor/material-web.js").then(() => {
    const _st = document.documentElement.dataset.style;
    if ((_st !== "md3" && _st !== "glass-chrome") || !document.body) return;
    const sel = "button, .quick-tile, .style-option, .tab-btn, .kpi, .pkg-card, .modal-box";
    document.querySelectorAll(sel).forEach(decorateMd3Node);
    if (!_md3Observer) {
      _md3Observer = new MutationObserver((muts) => {
        const _st2 = document.documentElement.dataset.style;
        if (_st2 !== "md3" && _st2 !== "glass-chrome") return;
        for (const m of muts) {
          for (const node of m.addedNodes) {
            if (node.nodeType !== 1) continue;
            if (node.matches && node.matches(sel)) decorateMd3Node(node);
            if (node.querySelectorAll) node.querySelectorAll(sel).forEach(decorateMd3Node);
          }
        }
      });
      _md3Observer.observe(document.body, { childList: true, subtree: true });
    }
  }).catch(() => { /* fallback gracefully */ });
}

function unmountMaterialWeb() {
  if (_md3Observer) {
    _md3Observer.disconnect();
    _md3Observer = null;
  }
  document.querySelectorAll("[data-pd-mwc]").forEach((n) => n.remove());
  document.querySelectorAll("[data-md3-mounted]").forEach((el) => delete el.dataset.md3Mounted);
}

function activateStyleRuntime(styleId) {
  if (styleId === "md3") {
    if (window.PDLiquidGlass) window.PDLiquidGlass.unmount();
    mountMaterialWeb();
  } else if (styleId === "glass" || styleId === "glass-chrome") {
    unmountMaterialWeb();
    loadVendorScript("vendor/webgl-liquid-glass.js")
      .then(() => loadVendorScript("vendor/liquid-glass.js"))
      .then(() => {
        const cur = document.documentElement.dataset.style;
        if ((cur === "glass" || cur === "glass-chrome") && window.PDLiquidGlass) {
          window.PDLiquidGlass.mount(true);
        }
      }).catch(() => { /* ignore */ });
  } else {
    unmountMaterialWeb();
    if (window.PDLiquidGlass) window.PDLiquidGlass.unmount();
  }
}

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
  const valid = ["winui3", "md3", "glass-chrome", "glass"].includes(styleId) ? styleId : "winui3";
  runSmoothSwitch(() => {
    document.documentElement.dataset.style = valid;
  });
  try { localStorage.setItem("pd-ui-style", valid); } catch (e) { /* ignore */ }
  activateStyleRuntime(valid);
  showToast("已切换外观风格：" + getUiStyleName(valid), "ok");
  return valid;
}

function cycleUiStyle() {
  const order = ["winui3", "md3", "glass-chrome", "glass"];
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
    syncHtmlThemeClasses(next);
  });
  try { sessionStorage.setItem("pd-ui-theme", next); } catch (e) { /* ignore */ }
  if (window.pd && typeof window.pd.setTheme === "function") {
    window.pd.setTheme(next).catch(() => { /* optional */ });
  }
  if (persist && window.pd && window.pd.run) {
    window.pd.run(["config", "set", "ui.theme", next]).catch(() => { /* optional */ });
  }
  if (window.PDWallpaper) window.PDWallpaper.applyCurrent();
  if ((getUiStyle() === "glass" || getUiStyle() === "glass-chrome") && window.PDLiquidGlass) {
    setTimeout(() => window.PDLiquidGlass.mount(true), 40);
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

function isBusyError(error) {
  if (!error) return false;
  if (error.code === "ERR_BUSY" || error.exitCode === 10) return true;
  return /busy|ERR_BUSY|正忙/i.test(String(error.message || error));
}

// Kernel mutating commands are exclusive: while a refresh/install runs, reads can legitimately
// return ERR_BUSY. Retry briefly instead of rendering an empty page.
async function pdTimedRetry(argv, tries) {
  const max = tries || 6;
  let last = null;
  for (let attempt = 0; attempt < max; attempt += 1) {
    try {
      return await pdTimed(argv);
    } catch (error) {
      last = error;
      if (!isBusyError(error) || attempt === max - 1) throw error;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  }
  throw last;
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
  let finished = null;
  try {
    const taskId = await window.pd.exec(argv);
    await new Promise((resolve) => {
      unsub = window.pd.on("evt:" + taskId, (ev) => {
        if (!ev) return;
        if (ev.type === "log") {
          const rec = ev.record || ev;
          append(rec.level || "info", rec.msg || "");
        } else if (ev.type === "step") {
          const state = String(ev.state || "running");
          const tag = state === "failed" ? "error" : "info";
          const mark = state === "done" ? "ok" : state === "failed" ? "!!" : "..";
          append(tag, "[" + mark + "] " + (ev.title || ev.stepId || "step") + (state === "running" ? "" : " (" + state + ")"));
        } else if (ev.type === "progress") {
          const pct = typeof ev.percent === "number" ? " (" + Math.round(ev.percent) + "%)" : "";
          append("info", (ev.stage || ev.msg || "进行中") + pct);
        } else if (ev.type === "error") {
          append("error", (ev.code ? ev.code + ": " : "") + (ev.message || "执行失败"));
        } else if (ev.type === "done") {
          finished = ev;
          const ok = (ev.exitCode === 0);
          append(ok ? "info" : "error", (ev.summary || (ok ? "完成" : "退出码 " + ev.exitCode)));
          if (unsub) { try { unsub(); } catch (e) { /* ignore */ } }
          resolve(ev);
        }
      });
    });
    if (onDone) onDone(finished ? finished.exitCode === 0 : true);
  } catch (error) {
    if (unsub) { try { unsub(); } catch (e) { /* ignore */ } }
    append("error", (error && error.message) || String(error));
    if (onDone) onDone(false);
  }
}

document.addEventListener("DOMContentLoaded", () => {
  hydrateIcons();
  loadVendorScript("vendor/monet-wallpaper.js").then(() => {
    if (window.PDWallpaper) window.PDWallpaper.applyCurrent();
  }).catch(() => { /* ignore */ });
  activateStyleRuntime(getUiStyle());
});

// Liquid Glass specular angle follows pointer gently
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
      const angle = (135 + nx * 42 + ny * 20).toFixed(1) + "deg";
      document.documentElement.style.setProperty("--glass-angle", angle);
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
