(function () {
  const MAX_POINTS = 61;
  const POLL_MS = 1000;

  const chart = document.getElementById("trafficChart");
  const line = document.getElementById("linePath");
  const area = document.getElementById("areaPath");
  const downRateEl = document.getElementById("downRate");
  const upRateEl = document.getElementById("upRate");
  const totalRateEl = document.getElementById("totalRate");
  const totalDownNice = document.getElementById("totalDownNice");
  const totalUpNice = document.getElementById("totalUpNice");
  const usageDown = document.getElementById("usageDown");
  const usageUp = document.getElementById("usageUp");
  const usageTotal = document.getElementById("usageTotal");
  const usedTotalNice = document.getElementById("usedTotalNice");
  const updateState = document.getElementById("updateState");

  let prevIn = Number(window.MT_INITIAL && window.MT_INITIAL.bytesIn) || 0;
  let prevOut = Number(window.MT_INITIAL && window.MT_INITIAL.bytesOut) || 0;
  let lastAt = performance.now();
  const values = Array(MAX_POINTS).fill(0);

  function humanBytes(n) {
    n = Math.max(0, Number(n) || 0);
    const u = ["B","KB","MB","GB","TB"];
    let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return (i === 0 ? Math.round(n) : n.toFixed(n >= 100 ? 0 : n >= 10 ? 1 : 2)) + " " + u[i];
  }

  function humanRate(bytesPerSec) {
    let bps = Math.max(0, bytesPerSec) * 8;
    if (bps >= 1e9) return (bps/1e9).toFixed(2) + " Gbps";
    if (bps >= 1e6) return (bps/1e6).toFixed(2) + " Mbps";
    if (bps >= 1e3) return (bps/1e3).toFixed(1) + " Kbps";
    return Math.round(bps) + " bps";
  }

  function draw() {
    const w = 640, h = 220, topPad = 12, bottomPad = 10;
    const max = Math.max(1024, ...values) * 1.15;
    const pts = values.map((v, i) => {
      const x = (i / (MAX_POINTS - 1)) * w;
      const y = h - bottomPad - (v / max) * (h - topPad - bottomPad);
      return [x, y];
    });
    const points = pts.map(p => p[0].toFixed(1) + "," + p[1].toFixed(1)).join(" ");
    line.setAttribute("points", points);
    const areaD = "M " + pts[0][0].toFixed(1) + " " + (h-bottomPad) +
      " L " + pts.map(p => p[0].toFixed(1) + " " + p[1].toFixed(1)).join(" L ") +
      " L " + pts[pts.length-1][0].toFixed(1) + " " + (h-bottomPad) + " Z";
    area.setAttribute("d", areaD);
  }

  async function poll() {
    try {
      const res = await fetch("status-data.html?_=" + Date.now(), {
        cache: "no-store",
        credentials: "same-origin"
      });
      if (!res.ok) throw new Error("HTTP " + res.status);
      const data = await res.json();

      const now = performance.now();
      const dt = Math.max(0.25, (now - lastAt) / 1000);
      lastAt = now;

      const bytesIn = Number(data.bytesIn) || 0;
      const bytesOut = Number(data.bytesOut) || 0;

      const upBps = Math.max(0, (bytesIn - prevIn) / dt);
      const downBps = Math.max(0, (bytesOut - prevOut) / dt);

      prevIn = bytesIn;
      prevOut = bytesOut;

      values.push(upBps + downBps);
      while (values.length > MAX_POINTS) values.shift();
      draw();

      downRateEl.textContent = humanRate(downBps);
      upRateEl.textContent = humanRate(upBps);
      totalRateEl.textContent = humanRate(upBps + downBps);

      const downNice = data.bytesOutNice || humanBytes(bytesOut);
      const upNice = data.bytesInNice || humanBytes(bytesIn);
      const totalNice = humanBytes(bytesIn + bytesOut);

      totalDownNice.textContent = downNice;
      totalUpNice.textContent = upNice;
      usageDown.textContent = downNice;
      usageUp.textContent = upNice;
      usageTotal.textContent = totalNice;
      usedTotalNice.textContent = totalNice;

      updateState.textContent = "آخرین بروزرسانی: " + new Date().toLocaleTimeString("fa-IR");
    } catch (err) {
      updateState.textContent = "دریافت آمار زنده ناموفق بود؛ اتصال یا دسترسی status-data.html را بررسی کنید.";
    } finally {
      setTimeout(poll, POLL_MS);
    }
  }

  draw();
  setTimeout(poll, 650);
})();
