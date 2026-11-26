// Shared helpers for the built-in pages. window.pd is injected by the window shell.
(function applyTheme() {
  try {
    const theme = (window.pd && window.pd.theme) || document.documentElement.dataset.theme || "light";
    document.documentElement.dataset.theme = theme === "dark" ? "dark" : "light";
  } catch (error) { /* keep the markup default */ }
})();

function showTiming(text) {
  let node = document.getElementById("pd-timing");
  if (!node) {
    node = document.createElement("div");
    node.id = "pd-timing";
    node.style.cssText = "position:fixed;right:8px;bottom:8px;font-size:12px;color:var(--muted);background:var(--panel);border:1px solid var(--border);border-radius:6px;padding:4px 10px;z-index:9999;";
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

async function renderError(container, error) {
  const node = document.createElement("div");
  node.className = "error";
  node.textContent = (error && (error.code + ": " + error.message)) || String(error);
  container.appendChild(node);
}

function cell(row, index) {
  const td = document.createElement("td");
  td.textContent = row[index] === null || row[index] === undefined ? "" : String(row[index]);
  return td;
}
