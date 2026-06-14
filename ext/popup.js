document.addEventListener("DOMContentLoaded", async () => {
  const sheetDisplay = document.getElementById("sheetDisplay");
  const startRowDisplay = document.getElementById("startRowDisplay");
  const endRowDisplay = document.getElementById("endRowDisplay");
  const statusEl = document.getElementById("status");
  const progressFill = document.getElementById("progressFill");
  const logSection = document.getElementById("logSection");
  const runLogEl = document.getElementById("runLog");
  const logSummary = document.getElementById("logSummary");

  const setStatus = (text) => { statusEl.textContent = text || ""; };

  const setProgress = (current, total) => {
    const pct = total > 0 ? Math.min(100, Math.round((current / total) * 100)) : 0;
    progressFill.style.width = `${pct}%`;
  };

  const formatEndRow = (value) => {
    if (value == null || value === "" || Number(value) <= 0) return "Hết file";
    return String(value);
  };

  const applyDisplay = (state, lastConfig) => {
    const sheet = state.sheetName || state.lastSheetName || lastConfig.sheetName || "—";
    const startRow = state.startRow ?? lastConfig.startRow ?? "—";
    const endRow = state.endRow ?? lastConfig.endRow;

    sheetDisplay.textContent = sheet || "—";
    startRowDisplay.textContent = startRow !== "" && startRow != null ? String(startRow) : "—";
    endRowDisplay.textContent = formatEndRow(endRow);

    const current = state.currentLinkIndex || 0;
    const total = state.totalLinks || 0;
    setProgress(current, total);
    renderRunLog(state.runLog);

    if (state.running) {
      const phase = state.phase === "waiting" ? "Đang nghỉ" : "Đang chạy";
      const currentRow = state.currentRow ?? "—";
      const end = formatEndRow(endRow);
      setStatus(
        `${phase} (launcher)\nSheet: ${sheet}\nDòng: ${currentRow}/${end}\nLink: ${current}/${total || "?"}\n${state.lastMessage || ""}`.trim()
      );
      return;
    }

    const phaseLabel =
      state.phase === "stopped" ? "Đã dừng (launcher)." :
      state.phase === "finished" ? "Hoàn tất." :
      state.lastMessage || "Đang chờ launcher.";

    const lastRow = state.lastCompletedRow ?? state.currentRow ?? "—";
    setStatus(`${phaseLabel}\nSheet: ${sheet}\nDòng cuối: ${lastRow}`);
  };

  const renderRunLog = (runLog) => {
    if (!Array.isArray(runLog) || runLog.length === 0) {
      logSection.style.display = "none";
      return;
    }

    logSection.style.display = "block";
    const okCount = runLog.filter((e) => e.scrapeOk && e.videoOk).length;
    const errCount = runLog.filter((e) => !e.scrapeOk || !e.videoOk).length;
    logSummary.textContent = `${okCount} OK · ${errCount} lỗi`;

    runLogEl.innerHTML = "";
    for (const entry of runLog.slice(-8).reverse()) {
      const allOk = entry.scrapeOk && entry.videoOk;
      const partial = entry.scrapeOk && !entry.videoOk;
      const dotClass = allOk ? "log-dot-ok" : partial ? "log-dot-warn" : "log-dot-err";
      const scrapeIcon = entry.scrapeOk ? "✓" : "✗";
      const videoIcon = entry.videoOk ? "✓" : "✗";

      const div = document.createElement("div");
      div.className = "log-entry";
      div.innerHTML = `
        <span class="log-dot ${dotClass}"></span>
        <span>Dòng ${entry.rowNumber} · ${entry.sku || "?"} · scrape:${scrapeIcon} video:${videoIcon}</span>
      `;
      runLogEl.appendChild(div);
    }
  };

  const readLastRunConfig = async () => {
    const data = await chrome.storage.local.get("lastRunConfig");
    return data.lastRunConfig || {};
  };

  const readRunnerState = async () => {
    const data = await chrome.storage.local.get("runnerState");
    return data.runnerState || { running: false };
  };

  const renderState = async () => {
    const state = await readRunnerState();
    const lastConfig = await readLastRunConfig();
    applyDisplay(state, lastConfig);
  };

  chrome.runtime.onMessage.addListener((message) => {
    if (!message || message.type !== "RUNNER_STATE") return;
    readLastRunConfig().then((cfg) => applyDisplay(message.state || {}, cfg));
  });

  chrome.storage.onChanged.addListener((changes) => {
    if (changes.runnerState || changes.lastRunConfig) {
      renderState();
    }
  });

  await renderState();
});
