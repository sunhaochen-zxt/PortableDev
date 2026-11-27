// Offline Custom Wallpaper Cropper + Material You (Monet) Dynamic Color Engine
// Works 100% offline in WebView2 and standalone HTML previews (zero external URLs).
(function (global) {
  "use strict";

  const KEY_WP_LIGHT = "pd-glass-wp-light";
  const KEY_WP_DARK = "pd-glass-wp-dark";
  const KEY_MD3_SEED = "pd-md3-seed";
  const KEY_WP_ALL = "pd-wp-all-styles";
  const DEFAULT_LIGHT_SEED = "#4e8c52";

  // --- Color space helpers (RGB <-> HSL) ---
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

  function rgbToHsl(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b);
    let h = 0, s = 0;
    const l = (max + min) / 2;
    if (max !== min) {
      const d = max - min;
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      switch (max) {
        case r: h = (g - b) / d + (g < b ? 6 : 0); break;
        case g: h = (b - r) / d + 2; break;
        default: h = (r - g) / d + 4; break;
      }
      h *= 60;
    }
    return { h: h, s: s * 100, l: l * 100 };
  }

  function hslToHex(h, s, l) {
    h = ((h % 360) + 360) % 360;
    s = clamp(s, 0, 100) / 100;
    l = clamp(l, 0, 100) / 100;
    const c = (1 - Math.abs(2 * l - 1)) * s;
    const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
    const m = l - c / 2;
    let r = 0, g = 0, b = 0;
    if (h < 60) { r = c; g = x; }
    else if (h < 120) { r = x; g = c; }
    else if (h < 180) { g = c; b = x; }
    else if (h < 240) { g = x; b = c; }
    else if (h < 300) { r = x; b = c; }
    else { r = c; b = x; }
    const toHex = (v) => Math.round((v + m) * 255).toString(16).padStart(2, "0");
    return "#" + toHex(r) + toHex(g) + toHex(b);
  }

  function hexToRgb(hex) {
    const clean = String(hex || "").replace(/^#/, "").trim();
    if (!/^[0-9a-fA-F]{6}$/.test(clean)) return { r: 78, g: 140, b: 82 };
    return {
      r: parseInt(clean.slice(0, 2), 16),
      g: parseInt(clean.slice(2, 4), 16),
      b: parseInt(clean.slice(4, 6), 16),
    };
  }

  function hexToRgba(hex, alpha) {
    const c = hexToRgb(hex);
    return "rgba(" + c.r + ", " + c.g + ", " + c.b + ", " + alpha + ")";
  }

  // --- Monet dominant seed extraction (chroma-weighted 36-hue histogram) ---
  function extractSeedFromSource(imgOrCanvas) {
    const w = 72, h = 42;
    const canvas = document.createElement("canvas");
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) return DEFAULT_LIGHT_SEED;
    ctx.drawImage(imgOrCanvas, 0, 0, w, h);
    let data;
    try {
      data = ctx.getImageData(0, 0, w, h).data;
    } catch (e) {
      return DEFAULT_LIGHT_SEED;
    }

    const bins = new Array(36).fill(null).map(() => ({ score: 0, hSum: 0, sSum: 0, lSum: 0, wSum: 0 }));
    for (let i = 0; i < data.length; i += 4) {
      if (data[i + 3] < 128) continue;
      const hsl = rgbToHsl(data[i], data[i + 1], data[i + 2]);
      // Skip dead shadows, blown highlights, and near-achromatic grays
      if (hsl.l < 14 || hsl.l > 88 || hsl.s < 12) continue;
      const idx = Math.floor(((hsl.h % 360) + 360) % 360 / 10) % 36;
      const chromaWeight = Math.pow(hsl.s / 100, 1.35) * (1 - Math.abs(hsl.l - 52) / 55);
      const b = bins[idx];
      b.score += chromaWeight;
      b.hSum += hsl.h * chromaWeight;
      b.sSum += hsl.s * chromaWeight;
      b.lSum += hsl.l * chromaWeight;
      b.wSum += chromaWeight;
    }

    let best = null;
    for (let i = 0; i < 36; i++) {
      // Smooth with immediate neighbor hue bins like Material Color Utilities
      const prev = bins[(i + 35) % 36].score * 0.4;
      const next = bins[(i + 1) % 36].score * 0.4;
      const total = bins[i].score + prev + next;
      if (bins[i].wSum > 0 && (!best || total > best.total)) {
        best = { total: total, bin: bins[i] };
      }
    }
    if (!best) return DEFAULT_LIGHT_SEED;
    const hue = best.bin.hSum / best.bin.wSum;
    const sat = clamp(best.bin.sSum / best.bin.wSum, 28, 62);
    return hslToHex(hue, sat, 45);
  }

  // --- Generate calm, authentic Material Design 3 Monet Light & Dark token maps ---
  function buildMonetTokens(seedHex, dark) {
    const rgb = hexToRgb(seedHex);
    const hsl = rgbToHsl(rgb.r, rgb.g, rgb.b);
    const h = hsl.h;
    // Keep Monet tasteful & understated (never neon)
    const pSat = clamp(hsl.s, 26, 54);
    const nSat = clamp(pSat * 0.24, 6, 14); // neutral-variant tint (signature Monet harmony)
    const sSat = clamp(pSat * 0.52, 14, 30);

    if (!dark) {
      const primary = hslToHex(h, pSat, 40);
      const primaryHover = hslToHex(h, pSat, 35);
      const primaryPressed = hslToHex(h, pSat, 30);
      const primaryContainer = hslToHex(h, clamp(pSat + 10, 32, 68), 88);
      const onPrimaryContainer = hslToHex(h, clamp(pSat + 6, 28, 58), 16);
      const secondaryContainer = hslToHex(h, sSat, 90);
      const onSecondaryContainer = hslToHex(h, sSat, 18);
      const surface = hslToHex(h, nSat, 98);
      const containerLow = hslToHex(h, nSat, 95.5);
      const container = hslToHex(h, nSat, 93);
      const containerHigh = hslToHex(h, nSat, 90.5);
      const outline = hslToHex(h, nSat, 48);
      const outlineVariant = hslToHex(h, nSat, 80);
      return {
        seed: seedHex,
        swatches: [primary, primaryContainer, secondaryContainer, containerLow, hslToHex(h, pSat, 72)],
        vars: {
          "--md-sys-color-primary": primary,
          "--md-sys-color-on-primary": "#ffffff",
          "--md-sys-color-primary-container": primaryContainer,
          "--md-sys-color-on-primary-container": onPrimaryContainer,
          "--md-sys-color-secondary-container": secondaryContainer,
          "--md-sys-color-on-secondary-container": onSecondaryContainer,
          "--md-sys-color-surface": surface,
          "--md-sys-color-surface-container-low": containerLow,
          "--md-sys-color-surface-container": container,
          "--md-sys-color-surface-container-high": containerHigh,
          "--md-sys-color-outline": outline,
          "--md-sys-color-outline-variant": outlineVariant,
          "--md-ripple-hover-color": primary,
          "--md-ripple-pressed-color": primary,
          "--md-focus-ring-color": primary,
          "--bg": surface,
          "--panel": containerLow,
          "--panel-subtle": container,
          "--control-bg": containerHigh,
          "--control-hover": hslToHex(h, nSat, 87),
          "--fg": hslToHex(h, nSat, 12),
          "--fg-2": hslToHex(h, nSat, 29),
          "--muted": hslToHex(h, nSat, 41),
          "--accent": primary,
          "--accent-hover": primaryHover,
          "--accent-pressed": primaryPressed,
          "--accent-fg": "#ffffff",
          "--accent-soft": primaryContainer,
          "--accent-soft-fg": onPrimaryContainer,
          "--accent-border": hexToRgba(primary, 0.28),
          "--border": hexToRgba(outline, 0.20),
          "--border-strong": outlineVariant,
        },
      };
    }

    const primary = hslToHex(h, clamp(pSat + 6, 30, 62), 74);
    const onPrimary = hslToHex(h, pSat, 14);
    const primaryHover = hslToHex(h, clamp(pSat + 6, 30, 62), 80);
    const primaryPressed = hslToHex(h, clamp(pSat + 6, 30, 62), 68);
    const primaryContainer = hslToHex(h, pSat, 24);
    const onPrimaryContainer = hslToHex(h, clamp(pSat + 10, 32, 68), 88);
    const secondaryContainer = hslToHex(h, sSat, 25);
    const onSecondaryContainer = hslToHex(h, sSat, 86);
    const surface = hslToHex(h, nSat, 7.5);
    const containerLow = hslToHex(h, nSat, 10.5);
    const container = hslToHex(h, nSat, 12.5);
    const containerHigh = hslToHex(h, nSat, 16);
    const outline = hslToHex(h, nSat, 56);
    const outlineVariant = hslToHex(h, nSat, 27);
    return {
      seed: seedHex,
      swatches: [primary, primaryContainer, secondaryContainer, containerHigh, hslToHex(h, pSat, 42)],
      vars: {
        "--md-sys-color-primary": primary,
        "--md-sys-color-on-primary": onPrimary,
        "--md-sys-color-primary-container": primaryContainer,
        "--md-sys-color-on-primary-container": onPrimaryContainer,
        "--md-sys-color-secondary-container": secondaryContainer,
        "--md-sys-color-on-secondary-container": onSecondaryContainer,
        "--md-sys-color-surface": surface,
        "--md-sys-color-surface-container-low": containerLow,
        "--md-sys-color-surface-container": container,
        "--md-sys-color-surface-container-high": containerHigh,
        "--md-sys-color-outline": outline,
        "--md-sys-color-outline-variant": outlineVariant,
        "--md-ripple-hover-color": primary,
        "--md-ripple-pressed-color": primary,
        "--md-focus-ring-color": primary,
        "--bg": surface,
        "--panel": container,
        "--panel-solid": hslToHex(h, nSat, 15.5),
        "--panel-subtle": containerHigh,
        "--control-bg": containerHigh,
        "--control-hover": hslToHex(h, nSat, 20),
        "--fg": hslToHex(h, nSat, 89),
        "--fg-2": hslToHex(h, nSat, 76),
        "--muted": hslToHex(h, nSat, 58),
        "--accent": primary,
        "--accent-hover": primaryHover,
        "--accent-pressed": primaryPressed,
        "--accent-fg": onPrimary,
        "--accent-soft": primaryContainer,
        "--accent-soft-fg": onPrimaryContainer,
        "--accent-border": hexToRgba(primary, 0.30),
        "--border": hexToRgba(outline, 0.18),
        "--border-strong": outlineVariant,
      },
    };
  }

  function getStored(key) {
    try { return localStorage.getItem(key) || ""; } catch (e) { return ""; }
  }
  function setStored(key, val) {
    try {
      if (val) localStorage.setItem(key, val);
      else localStorage.removeItem(key);
      return true;
    } catch (e) {
      return false;
    }
  }

  function getWallpaperUrl(theme) {
    const isDark = theme === "dark";
    const custom = getStored(isDark ? KEY_WP_DARK : KEY_WP_LIGHT);
    if (custom) return custom;
    if (global.PD_GLASS_WALLPAPERS && global.PD_GLASS_WALLPAPERS[isDark ? "dark" : "light"]) {
      return global.PD_GLASS_WALLPAPERS[isDark ? "dark" : "light"];
    }
    return isDark ? "vendor/glass-bg-dark.webp" : "vendor/glass-bg-light.webp";
  }

  function getMd3Seed() {
    return getStored(KEY_MD3_SEED) || DEFAULT_LIGHT_SEED;
  }

  function applyMd3DynamicStyle() {
    let styleEl = document.getElementById("pd-monet-vars");
    const seed = getStored(KEY_MD3_SEED);
    if (!seed) {
      if (styleEl) styleEl.remove();
      return buildMonetTokens(DEFAULT_LIGHT_SEED, document.documentElement.dataset.theme === "dark");
    }
    const light = buildMonetTokens(seed, false);
    const dark = buildMonetTokens(seed, true);
    const toDecls = (map) => Object.entries(map).map(([k, v]) => "  " + k + ": " + v + ";").join("\n");
    const chromeLightAccent =
      'html[data-style="glass-chrome"][data-theme="light"], html[data-style="glass-chrome"]:not([data-theme="dark"]) {\n' +
      '  --accent: ' + light.vars["--accent"] + ';\n' +
      '  --accent-hover: ' + light.vars["--accent-hover"] + ';\n' +
      '  --accent-pressed: ' + light.vars["--accent-pressed"] + ';\n' +
      '  --accent-soft: ' + light.vars["--accent-soft"] + ';\n' +
      '  --accent-soft-fg: ' + light.vars["--accent-soft-fg"] + ';\n' +
      '  --accent-border: ' + light.vars["--accent-border"] + ';\n' +
      '}\n' +
      'html[data-style="glass-chrome"][data-theme="dark"] {\n' +
      '  --accent: ' + dark.vars["--accent"] + ';\n' +
      '  --accent-hover: ' + dark.vars["--accent-hover"] + ';\n' +
      '  --accent-pressed: ' + dark.vars["--accent-pressed"] + ';\n' +
      '  --accent-soft: ' + dark.vars["--accent-soft"] + ';\n' +
      '  --accent-soft-fg: ' + dark.vars["--accent-soft-fg"] + ';\n' +
      '  --accent-border: ' + dark.vars["--accent-border"] + ';\n' +
      '}\n';
    const cssText =
      'html[data-style="md3"][data-theme="light"], html[data-style="md3"]:not([data-theme="dark"]) {\n' +
      toDecls(light.vars) + "\n}\n" +
      'html[data-style="md3"][data-theme="dark"] {\n' +
      toDecls(dark.vars) + "\n}\n" +
      chromeLightAccent +
      '.style-preview.md3 { background: linear-gradient(135deg, ' + light.vars["--bg"] + " 0%, " + light.vars["--md-sys-color-primary-container"] + " 100%) !important; }\n" +
      'html[data-theme="dark"] .style-preview.md3 { background: linear-gradient(135deg, ' + dark.vars["--bg"] + " 0%, " + dark.vars["--md-sys-color-primary-container"] + " 100%) !important; }\n";

    if (!styleEl) {
      styleEl = document.createElement("style");
      styleEl.id = "pd-monet-vars";
      document.head.appendChild(styleEl);
    }
    styleEl.textContent = cssText;
    return document.documentElement.dataset.theme === "dark" ? dark : light;
  }

  function isWallpaperOnAllStyles() {
    const raw = getStored(KEY_WP_ALL);
    return raw === "" ? true : raw === "1";
  }

  function setWallpaperOnAllStyles(enabled) {
    setStored(KEY_WP_ALL, enabled ? "1" : "0");
    applyWallpaperVars();
    notifyChange();
  }

  function applyWallpaperVars() {
    const dark = document.documentElement.dataset.theme === "dark";
    const url = getWallpaperUrl(dark ? "dark" : "light");
    document.documentElement.style.setProperty("--pd-glass-bg", 'url("' + url.replace(/"/g, '\\"') + '")');
    document.documentElement.dataset.wp = isWallpaperOnAllStyles() ? "on" : "off";
    if ((document.documentElement.dataset.style === "glass" || document.documentElement.dataset.style === "glass-chrome") && global.PDLiquidGlass) {
      global.PDLiquidGlass.mount(true);
    }
  }

  function notifyChange() {
    window.dispatchEvent(new CustomEvent("pd-wallpaper-change", {
      detail: {
        seed: getMd3Seed(),
        hasCustomLight: Boolean(getStored(KEY_WP_LIGHT)),
        hasCustomDark: Boolean(getStored(KEY_WP_DARK)),
        allStyles: isWallpaperOnAllStyles(),
      },
    }));
  }

  function applyCurrent() {
    applyMd3DynamicStyle();
    applyWallpaperVars();
  }

  function setMd3Seed(hex) {
    setStored(KEY_MD3_SEED, hex || "");
    applyMd3DynamicStyle();
    notifyChange();
  }

  function setCustomWallpaper(dataUrl, opts) {
    const o = opts || {};
    const target = o.target || "both";
    let ok = true;
    if (target === "both" || target === "light") ok = setStored(KEY_WP_LIGHT, dataUrl) && ok;
    if (target === "both" || target === "dark") ok = setStored(KEY_WP_DARK, dataUrl) && ok;
    if (!ok && typeof global.showToast === "function") {
      global.showToast("图片体积过大，已尝试压缩后保存", "error");
    }
    applyWallpaperVars();
    if (o.seed) {
      setMd3Seed(o.seed);
    } else if (o.syncMd3 !== false) {
      extractFromCurrentWallpaper(true);
    } else {
      notifyChange();
    }
  }

  function resetWallpaper() {
    setStored(KEY_WP_LIGHT, "");
    setStored(KEY_WP_DARK, "");
    setStored(KEY_MD3_SEED, "");
    applyCurrent();
    notifyChange();
    if (typeof global.showToast === "function") {
      global.showToast("已恢复默认动漫壁纸与浅绿莫奈色盘", "ok");
    }
  }

  function extractFromCurrentWallpaper(silent) {
    const dark = document.documentElement.dataset.theme === "dark";
    const src = getWallpaperUrl(dark ? "dark" : "light");
    const img = new Image();
    img.crossOrigin = "anonymous";
    img.onload = () => {
      const seed = extractSeedFromSource(img);
      setMd3Seed(seed);
      if (!silent && typeof global.showToast === "function") {
        global.showToast("已从当前壁纸提取莫奈主题色 " + seed, "ok");
      }
    };
    img.src = src;
  }

  // --- Interactive 16:9 Wallpaper Cropper Modal ---
  function loadImageFromInput(fileOrUrl) {
    return new Promise((resolve, reject) => {
      if (!fileOrUrl) {
        reject(new Error("未选择图片"));
        return;
      }
      if (fileOrUrl instanceof Blob) {
        const reader = new FileReader();
        reader.onload = () => {
          const img = new Image();
          img.onload = () => resolve(img);
          img.onerror = () => reject(new Error("无法解码所选图片"));
          img.src = reader.result;
        };
        reader.onerror = () => reject(new Error("读取图片失败"));
        reader.readAsDataURL(fileOrUrl);
      } else {
        const img = new Image();
        img.crossOrigin = "anonymous";
        img.onload = () => resolve(img);
        img.onerror = () => reject(new Error("无法加载图片"));
        img.src = String(fileOrUrl);
      }
    });
  }

  function openCropper(fileOrUrl, options) {
    const opts = options || {};
    if (!fileOrUrl) {
      const input = document.createElement("input");
      input.type = "file";
      input.accept = "image/*";
      input.addEventListener("change", () => {
        if (input.files && input.files[0]) openCropper(input.files[0], opts);
      });
      input.click();
      return;
    }

    loadImageFromInput(fileOrUrl).then((img) => {
      const backdrop = document.createElement("div");
      backdrop.className = "modal-backdrop";
      backdrop.setAttribute("role", "dialog");
      backdrop.setAttribute("aria-modal", "true");

      const box = document.createElement("div");
      box.className = "modal-box cropper-modal";

      const title = document.createElement("h3");
      title.className = "modal-title";
      title.textContent = "裁剪自定义壁纸（16:9）";

      const hint = document.createElement("div");
      hint.className = "muted";
      hint.style.marginBottom = "12px";
      hint.textContent = "拖拽移动画面，滚轮或滑块缩放；自动提取莫奈动态配色用于 MD3";

      const stage = document.createElement("div");
      stage.className = "cropper-stage";
      const canvas = document.createElement("canvas");
      const viewW = 544, viewH = 306;
      canvas.width = viewW;
      canvas.height = viewH;
      stage.appendChild(canvas);
      const ctx = canvas.getContext("2d");

      // Compute base cover scale so the 16:9 frame is always 100% filled
      const baseScale = Math.max(viewW / img.naturalWidth, viewH / img.naturalHeight);
      let zoom = 1;
      let offsetX = 0;
      let offsetY = 0;

      const clampOffsets = () => {
        const dw = img.naturalWidth * baseScale * zoom;
        const dh = img.naturalHeight * baseScale * zoom;
        const maxX = Math.max(0, (dw - viewW) / 2);
        const maxY = Math.max(0, (dh - viewH) / 2);
        offsetX = clamp(offsetX, -maxX, maxX);
        offsetY = clamp(offsetY, -maxY, maxY);
      };

      const swatchRow = document.createElement("div");
      swatchRow.className = "monet-swatch-row";
      let currentSeed = DEFAULT_LIGHT_SEED;
      let swatchTimer = 0;

      const renderSwatches = (seed) => {
        currentSeed = seed;
        const tok = buildMonetTokens(seed, document.documentElement.dataset.theme === "dark");
        swatchRow.textContent = "";
        const lbl = document.createElement("span");
        lbl.className = "muted";
        lbl.textContent = "莫奈取色 " + seed;
        swatchRow.appendChild(lbl);
        for (const col of tok.swatches) {
          const dot = document.createElement("span");
          dot.className = "monet-dot";
          dot.style.background = col;
          dot.title = col;
          swatchRow.appendChild(dot);
        }
      };

      const draw = (updateSeed) => {
        clampOffsets();
        const dw = img.naturalWidth * baseScale * zoom;
        const dh = img.naturalHeight * baseScale * zoom;
        const dx = (viewW - dw) / 2 + offsetX;
        const dy = (viewH - dh) / 2 + offsetY;
        ctx.clearRect(0, 0, viewW, viewH);
        ctx.drawImage(img, dx, dy, dw, dh);
        // Subtle rule-of-thirds guide
        ctx.save();
        ctx.strokeStyle = "rgba(255,255,255,0.28)";
        ctx.lineWidth = 1;
        for (let i = 1; i <= 2; i++) {
          ctx.beginPath(); ctx.moveTo((viewW * i) / 3, 0); ctx.lineTo((viewW * i) / 3, viewH); ctx.stroke();
          ctx.beginPath(); ctx.moveTo(0, (viewH * i) / 3); ctx.lineTo(viewW, (viewH * i) / 3); ctx.stroke();
        }
        ctx.restore();
        if (updateSeed) {
          clearTimeout(swatchTimer);
          swatchTimer = setTimeout(() => renderSwatches(extractSeedFromSource(canvas)), 60);
        }
      };

      // Controls row: zoom slider + target select + sync MD3 checkbox
      const controls = document.createElement("div");
      controls.className = "cropper-controls";

      const zoomWrap = document.createElement("label");
      zoomWrap.className = "cropper-zoom";
      const zoomLbl = document.createElement("span");
      zoomLbl.textContent = "缩放 1.00×";
      const zoomInput = document.createElement("input");
      zoomInput.type = "range";
      zoomInput.min = "1";
      zoomInput.max = "3";
      zoomInput.step = "0.02";
      zoomInput.value = "1";
      zoomInput.addEventListener("input", () => {
        zoom = Number(zoomInput.value) || 1;
        zoomLbl.textContent = "缩放 " + zoom.toFixed(2) + "×";
        draw(true);
      });
      zoomWrap.appendChild(zoomLbl);
      zoomWrap.appendChild(zoomInput);

      const targetSelect = document.createElement("select");
      targetSelect.style.width = "auto";
      for (const [val, text] of [["both", "明暗主题共用"], ["light", "仅浅色壁纸"], ["dark", "仅深色壁纸"]]) {
        const opt = document.createElement("option");
        opt.value = val;
        opt.textContent = text;
        targetSelect.appendChild(opt);
      }

      const syncLabel = document.createElement("label");
      syncLabel.className = "cropper-check";
      const syncCheck = document.createElement("input");
      syncCheck.type = "checkbox";
      syncCheck.checked = opts.syncMd3 !== false;
      syncLabel.appendChild(syncCheck);
      syncLabel.appendChild(document.createTextNode("同步 MD3 莫奈动态取色"));

      controls.appendChild(zoomWrap);
      controls.appendChild(targetSelect);
      controls.appendChild(syncLabel);

      // Drag to pan + wheel to zoom
      let dragging = false, lastX = 0, lastY = 0;
      canvas.addEventListener("pointerdown", (e) => {
        dragging = true;
        lastX = e.clientX;
        lastY = e.clientY;
        canvas.setPointerCapture(e.pointerId);
      });
      canvas.addEventListener("pointermove", (e) => {
        if (!dragging) return;
        const rect = canvas.getBoundingClientRect();
        const sx = viewW / Math.max(1, rect.width);
        const sy = viewH / Math.max(1, rect.height);
        offsetX += (e.clientX - lastX) * sx;
        offsetY += (e.clientY - lastY) * sy;
        lastX = e.clientX;
        lastY = e.clientY;
        draw(false);
      });
      const stopDrag = () => {
        if (!dragging) return;
        dragging = false;
        draw(true);
      };
      canvas.addEventListener("pointerup", stopDrag);
      canvas.addEventListener("pointercancel", stopDrag);
      canvas.addEventListener("wheel", (e) => {
        e.preventDefault();
        zoom = clamp(zoom - e.deltaY * 0.0012, 1, 3);
        zoomInput.value = zoom.toFixed(2);
        zoomLbl.textContent = "缩放 " + zoom.toFixed(2) + "×";
        draw(true);
      }, { passive: false });

      const foot = document.createElement("div");
      foot.className = "modal-foot";
      foot.style.justifyContent = "space-between";
      foot.style.alignItems = "center";

      const btnGroup = document.createElement("div");
      btnGroup.style.display = "flex";
      btnGroup.style.gap = "8px";
      const cancelBtn = document.createElement("button");
      cancelBtn.type = "button";
      cancelBtn.className = "secondary";
      cancelBtn.textContent = "取消";
      const applyBtn = document.createElement("button");
      applyBtn.type = "button";
      applyBtn.textContent = "确认裁剪并应用";

      const close = () => {
        document.removeEventListener("keydown", onKey, true);
        backdrop.classList.add("is-closing");
        setTimeout(() => backdrop.remove(), 125);
      };
      const onKey = (e) => { if (e.key === "Escape") { e.preventDefault(); close(); } };
      document.addEventListener("keydown", onKey, true);
      cancelBtn.addEventListener("click", close);
      backdrop.addEventListener("click", (e) => { if (e.target === backdrop) close(); });

      applyBtn.addEventListener("click", () => {
        // Export crisp 1600x900 WebP (fallback JPEG) without guide lines
        const out = document.createElement("canvas");
        out.width = 1600;
        out.height = 900;
        const octx = out.getContext("2d");
        const ratio = out.width / viewW;
        const dw = img.naturalWidth * baseScale * zoom * ratio;
        const dh = img.naturalHeight * baseScale * zoom * ratio;
        const dx = (out.width - dw) / 2 + offsetX * ratio;
        const dy = (out.height - dh) / 2 + offsetY * ratio;
        octx.drawImage(img, dx, dy, dw, dh);

        let dataUrl = out.toDataURL("image/webp", 0.82);
        if (!dataUrl.startsWith("data:image/webp")) {
          dataUrl = out.toDataURL("image/jpeg", 0.84);
        }
        const seed = extractSeedFromSource(out);
        setCustomWallpaper(dataUrl, {
          target: targetSelect.value,
          syncMd3: syncCheck.checked,
          seed: syncCheck.checked ? seed : "",
        });
        if (typeof global.showToast === "function") {
          global.showToast(syncCheck.checked ? "壁纸已应用，MD3 已同步莫奈取色 " + seed : "自定义壁纸已应用", "ok");
        }
        close();
      });

      btnGroup.appendChild(cancelBtn);
      btnGroup.appendChild(applyBtn);
      foot.appendChild(swatchRow);
      foot.appendChild(btnGroup);

      box.appendChild(title);
      box.appendChild(hint);
      box.appendChild(stage);
      box.appendChild(controls);
      box.appendChild(foot);
      backdrop.appendChild(box);
      document.body.appendChild(backdrop);

      draw(false);
      renderSwatches(extractSeedFromSource(canvas));
    }).catch((err) => {
      if (typeof global.showToast === "function") {
        global.showToast((err && err.message) || "加载图片失败", "error");
      }
    });
  }

  global.PDWallpaper = {
    openCropper: openCropper,
    setCustomWallpaper: setCustomWallpaper,
    setMd3Seed: setMd3Seed,
    resetWallpaper: resetWallpaper,
    extractFromCurrentWallpaper: extractFromCurrentWallpaper,
    getWallpaperUrl: getWallpaperUrl,
    getMd3Seed: getMd3Seed,
    buildMonetTokens: buildMonetTokens,
    isWallpaperOnAllStyles: isWallpaperOnAllStyles,
    setWallpaperOnAllStyles: setWallpaperOnAllStyles,
    applyCurrent: applyCurrent,
  };
})(window);
