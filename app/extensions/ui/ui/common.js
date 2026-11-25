// Shared helpers for the built-in pages. window.pd is injected by the window shell.
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
