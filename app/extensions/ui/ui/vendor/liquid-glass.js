// PortableDev adapter for apple-liquid-glass-webgl v2.6.0 (MIT)
// Source: https://github.com/Oliverrr2424/webgl-apple-liquid-glass
(function (global) {
  "use strict";

  let instance = null;
  let currentTheme = "";
  let currentMode = "";
  let pointerBound = false;
  let targetAngle = 136;
  let currentAngle = 136;
  let angleRaf = 0;

  function isDark() {
    return document.documentElement.dataset.theme === "dark";
  }

  // Paint a serene, architectural Apple studio backdrop directly for WebGL2 refraction
  // (neutral silver-pearl in light mode, deep titanium-obsidian in dark mode — zero flashy colors)
  function paintAppleStudioBackdrop(ctx, region) {
    const w = window.innerWidth || 1200;
    const h = window.innerHeight || 820;
    const dark = isDark();

    ctx.save();
    const base = ctx.createLinearGradient(0, 0, w * 0.85, h);
    if (dark) {
      base.addColorStop(0, "#1d222c");
      base.addColorStop(0.5, "#0d1016");
      base.addColorStop(1, "#181d27");
    } else {
      base.addColorStop(0, "#f2f5fa");
      base.addColorStop(0.52, "#d8e0ec");
      base.addColorStop(1, "#eaeff6");
    }
    ctx.fillStyle = base;
    ctx.fillRect(region.x, region.y, region.width, region.height);

    // Soft sculpted studio light arcs so WebGL2 Snell refraction & edge dispersion look genuinely crystalline
    const arcs = dark
      ? [
          [w * 0.16, h * 0.14, w * 0.42, "rgba(255,255,255,0.13)", "rgba(255,255,255,0)"],
          [w * 0.84, h * 0.24, w * 0.36, "rgba(130,150,180,0.20)", "rgba(130,150,180,0)"],
          [w * 0.42, h * 0.86, w * 0.44, "rgba(96,114,142,0.16)", "rgba(96,114,142,0)"],
        ]
      : [
          [w * 0.14, h * 0.10, w * 0.44, "rgba(255,255,255,0.96)", "rgba(255,255,255,0)"],
          [w * 0.86, h * 0.22, w * 0.36, "rgba(148,164,188,0.44)", "rgba(148,164,188,0)"],
          [w * 0.26, h * 0.88, w * 0.40, "rgba(162,178,200,0.38)", "rgba(162,178,200,0)"],
          [w * 0.80, h * 0.82, w * 0.34, "rgba(255,255,255,0.82)", "rgba(255,255,255,0)"],
        ];

    for (const [cx, cy, r, c0, c1] of arcs) {
      const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, r);
      g.addColorStop(0, c0);
      g.addColorStop(1, c1);
      ctx.fillStyle = g;
      ctx.fillRect(region.x, region.y, region.width, region.height);
    }

    // Subtle crisp architectural hairlines behind glass so curved edges visibly bend them
    ctx.strokeStyle = dark ? "rgba(255,255,255,0.06)" : "rgba(15,23,42,0.055)";
    ctx.lineWidth = 1.25;
    for (let i = 0; i < 4; i++) {
      ctx.beginPath();
      ctx.ellipse(w * (0.2 + i * 0.24), h * (0.28 + (i % 2) * 0.38), w * 0.28, h * 0.18, -0.35, 0, Math.PI * 2);
      ctx.stroke();
    }
    ctx.restore();
  }

  function makeOptions() {
    const dark = isDark();
    const useWp = document.documentElement.dataset.wp !== "off";
    return {
      targets: ".kpi, .card, .pkg-card",
      backdrop: useWp
        ? {
            source: (global.PDWallpaper && global.PDWallpaper.getWallpaperUrl(dark ? "dark" : "light")) || (global.PD_GLASS_WALLPAPERS && global.PD_GLASS_WALLPAPERS[dark ? "dark" : "light"]) || (dark ? "vendor/glass-bg-dark.webp" : "vendor/glass-bg-light.webp"),
            fit: "cover",
            position: "50% 50%",
            anchor: "viewport",
          }
        : [paintAppleStudioBackdrop],
      frost: 0,
      tint: dark ? 0.08 : 0.04,
      tintTone: dark ? "dark" : "light",
      bleed: 24,
      maxDpr: 2,
      fallback: "css",
      material: {
        refraction: 66,
        edgeReach: 0.12,
        edgeWidth: 0.17,
        dispersion: 1.75,
        backdropBlur: 0.8,
        body: 0.42,
        absorption: 0.24,
        rim: 0.34,
        reflection: 0.42,
        highlight: 0.48,
        lightAngle: currentAngle,
        echo: 0.22,
        hairline: 0.98,
        hairWidth: 0.56,
      },
    };
  }

  let scrolling = false;
  let scrollTimer = 0;

  function bindScrollGuard() {
    if (bindScrollGuard._bound) return;
    bindScrollGuard._bound = true;
    const markScroll = () => {
      scrolling = true;
      clearTimeout(scrollTimer);
      scrollTimer = setTimeout(() => { scrolling = false; }, 150);
    };
    window.addEventListener("scroll", markScroll, { passive: true, capture: true });
    window.addEventListener("wheel", markScroll, { passive: true });
  }

  // Rock-solid pointer glow (prism_ui 120ms/360ms bloom) with ZERO transform wobble on WebGL2 targets
  function bindElasticTiles() {
    bindScrollGuard();
    document.querySelectorAll(".kpi, .quick-tile, .pkg-card, .style-option, button, .tab-btn").forEach((el) => {
      if (el._lgElasticBound) return;
      el._lgElasticBound = true;
      el.addEventListener("pointerenter", () => {
        const _st = document.documentElement.dataset.style;
        if (scrolling || (_st !== "glass" && _st !== "glass-chrome")) return;
        el.classList.add("pd-lg-lit");
      });
      el.addEventListener("pointermove", (e) => {
        const _st = document.documentElement.dataset.style;
        if (scrolling || (_st !== "glass" && _st !== "glass-chrome")) return;
        const r = el.getBoundingClientRect();
        if (!r.width || !r.height) return;
        const lx = Math.max(0, Math.min(100, ((e.clientX - r.left) / r.width) * 100));
        const ly = Math.max(0, Math.min(100, ((e.clientY - r.top) / r.height) * 100));
        el.style.setProperty("--lg-x", lx.toFixed(1) + "%");
        el.style.setProperty("--lg-y", ly.toFixed(1) + "%");
      }, { passive: true });
      el.addEventListener("pointerdown", (e) => {
        const _st = document.documentElement.dataset.style;
        if (_st !== "glass" && _st !== "glass-chrome") return;
        el.classList.add("pd-lg-pressed");
        if (_st === "glass-chrome" && !el.disabled && el.matches("button, .quick-tile, .style-option, .tab-btn")) {
          const r = el.getBoundingClientRect();
          const sz = Math.max(r.width, r.height) * 1.15;
          const sp = document.createElement("span");
          sp.className = "pd-glass-spark";
          sp.style.width = sz + "px";
          sp.style.height = sz + "px";
          sp.style.left = (e.clientX - r.left - sz / 2) + "px";
          sp.style.top = (e.clientY - r.top - sz / 2) + "px";
          el.appendChild(sp);
          setTimeout(() => sp.remove(), 440);
        }
      });
      const releaseGlow = () => { el.classList.remove("pd-lg-pressed"); };
      el.addEventListener("pointerup", releaseGlow);
      el.addEventListener("pointercancel", releaseGlow);
      el.addEventListener("pointerleave", () => {
        el.classList.remove("pd-lg-lit", "pd-lg-pressed");
      });
    });
  }

  function bindGlobalLightAngle() {
    if (pointerBound) return;
    pointerBound = true;
    const stepAngle = () => {
      angleRaf = 0;
      if (scrolling) return;
      currentAngle += (targetAngle - currentAngle) * 0.14;
      if (instance && typeof instance.setMaterial === "function") {
        try { instance.setMaterial({ lightAngle: currentAngle }); } catch (e) { /* ignore */ }
      }
      if (Math.abs(targetAngle - currentAngle) > 0.35 && instance) {
        angleRaf = requestAnimationFrame(stepAngle);
      }
    };
    document.addEventListener("pointermove", (e) => {
      const _st = document.documentElement.dataset.style;
      if (scrolling || !instance || (_st !== "glass" && _st !== "glass-chrome")) return;
      const nx = (e.clientX / Math.max(1, window.innerWidth)) - 0.5;
      const ny = (e.clientY / Math.max(1, window.innerHeight)) - 0.5;
      targetAngle = 136 + nx * 42 + ny * 20;
      if (!angleRaf) angleRaf = requestAnimationFrame(stepAngle);
    }, { passive: true });
  }

  function makeChromeOptions() {
    const dark = isDark();
    return {
      targets: ".hero-actions, .tabs",
      backdrop: "auto",
      frost: 0.03,
      tint: dark ? 0.24 : 0.20,
      tintTone: dark ? "dark" : "light",
      bleed: 20,
      maxDpr: 2,
      fallback: "css",
      material: {
        refraction: 58,
        edgeReach: 0.14,
        edgeWidth: 0.20,
        dispersion: 1.5,
        backdropBlur: 1.5,
        body: 0.56,
        absorption: 0.32,
        rim: 0.34,
        reflection: 0.38,
        highlight: 0.44,
        lightAngle: currentAngle,
        echo: 0.20,
        hairline: 0.94,
        hairWidth: 0.54,
      },
    };
  }

  function mount(force) {
    if (!document.body || !global.AppleLiquidGlass || !global.AppleLiquidGlass.LiquidGlass) return;
    const modeNow = document.documentElement.dataset.style || "glass";
    if (modeNow !== "glass" && modeNow !== "glass-chrome") {
      unmount();
      return;
    }
    const host = document.querySelector(".wrap") || document.body;
    const themeNow = isDark() ? "dark" : "light";
    if (!force && instance && currentTheme === themeNow && currentMode === modeNow) {
      bindElasticTiles();
      if (typeof instance.refresh === "function") instance.refresh();
      return;
    }
    unmount();
    currentTheme = themeNow;
    currentMode = modeNow;
    try {
      const opts = modeNow === "glass-chrome" ? makeChromeOptions() : makeOptions();
      instance = new global.AppleLiquidGlass.LiquidGlass(host, opts);
      host.setAttribute("data-pd-webgl-glass", modeNow === "glass-chrome" ? "chrome" : "1");
    } catch (err) {
      instance = null;
    }
    bindElasticTiles();
    bindGlobalLightAngle();
  }

  function unmount() {
    if (instance) {
      try { instance.destroy(); } catch (e) { /* ignore */ }
      instance = null;
    }
    document.querySelectorAll("[data-pd-webgl-glass]").forEach((el) => el.removeAttribute("data-pd-webgl-glass"));
    document.querySelectorAll(".kpi, .quick-tile, .pkg-card, .style-option").forEach((el) => {
      el.style.transform = "";
    });
  }

  global.PDLiquidGlass = { mount: mount, unmount: unmount };
})(window);
