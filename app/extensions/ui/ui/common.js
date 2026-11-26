// Shared helpers for the PortableDev built-in UI pages.
// All dynamic values are rendered via textContent / DOM APIs (never raw innerHTML).
(function applyTheme() {
  try {
    const saved = sessionStorage.getItem("pd-ui-theme");
    const theme = saved || (window.pd && window.pd.theme) || document.documentElement.dataset.theme || "light";
    document.documentElement.dataset.theme = theme === "dark" ? "dark" : "light";
  } catch (error) { /* keep markup default */ }
})();

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
  document.documentElement.dataset.theme = next;
  try { sessionStorage.setItem("pd-ui-theme", next); } catch (e) { /* ignore */ }
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
  const el = document.createElement("div");
  el.className = "toast" + (kind ? " " + kind : "");
  el.textContent = String(message || "");
  stack.appendChild(el);
  setTimeout(() => {
    el.style.opacity = "0";
    el.style.transition = "opacity 0.25s ease";
    setTimeout(() => el.remove(), 260);
  }, 2600);
}

function copyText(text, label) {
  const value = String(text || "");
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(value).then(
      () => showToast("已复制" + (label ? " " + label : ""), "ok"),
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
    const backdrop = document.createElement("div");
    backdrop.className = "modal-backdrop";
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
    cancelBtn.className = "secondary";
    cancelBtn.textContent = "取消";
    const okBtn = document.createElement("button");
    if (danger) okBtn.className = "danger";
    okBtn.textContent = confirmLabel || "确认";

    const close = (val) => { backdrop.remove(); resolve(val); };
    cancelBtn.addEventListener("click", () => close(false));
    okBtn.addEventListener("click", () => close(true));
    backdrop.addEventListener("click", (e) => { if (e.target === backdrop) close(false); });

    foot.appendChild(cancelBtn);
    foot.appendChild(okBtn);
    box.appendChild(h);
    box.appendChild(p);
    box.appendChild(foot);
    backdrop.appendChild(box);
    document.body.appendChild(backdrop);
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

// Press "/" or Ctrl+K anywhere outside an input to focus the page's search bar.
document.addEventListener("keydown", (e) => {
  const tag = document.activeElement && document.activeElement.tagName;
  const editing = tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
  if ((e.key === "k" && (e.ctrlKey || e.metaKey)) || (e.key === "/" && !editing)) {
    const search = document.querySelector('input[type="search"]');
    if (search) {
      e.preventDefault();
      search.focus();
      search.select();
    }
  }
});
