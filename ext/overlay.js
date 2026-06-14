(() => {
  if (window.__shopee27052026OverlayInjected) {
    return;
  }
  window.__shopee27052026OverlayInjected = true;

  const OVERLAY_ID = "shopee27052026-next-link-overlay";

  const ensureOverlay = () => {
    let overlay = document.getElementById(OVERLAY_ID);
    if (overlay) {
      return overlay;
    }

    overlay = document.createElement("div");
    overlay.id = OVERLAY_ID;
    overlay.style.cssText = [
      "position: fixed",
      "left: 12px",
      "bottom: 12px",
      "z-index: 2147483647",
      "max-width: min(520px, calc(100vw - 24px))",
      "padding: 8px 10px",
      "border-radius: 10px",
      "background: rgba(15, 23, 42, 0.92)",
      "color: #fff",
      "font: 12px/1.45 Arial, sans-serif",
      "box-shadow: 0 8px 24px rgba(0,0,0,0.25)",
      "border: 1px solid rgba(255,255,255,0.12)",
      "pointer-events: none",
      "white-space: pre-line",
      "display: none",
    ].join(";");
    overlay.textContent = "";
    document.documentElement.appendChild(overlay);
    return overlay;
  };

  const setOverlayMessage = (message, visible = true) => {
    const overlay = ensureOverlay();
    overlay.textContent = message || "";
    overlay.style.display = visible && message ? "block" : "none";
  };

  chrome.runtime.onMessage.addListener((message) => {
    if (!message || !message.type) {
      return;
    }

    if (message.type === "SHOW_NEXT_LINK_MESSAGE") {
      setOverlayMessage(message.text || "", Boolean(message.text));
    }

    if (message.type === "HIDE_NEXT_LINK_MESSAGE") {
      setOverlayMessage("", false);
    }
  });

  ensureOverlay();
  setOverlayMessage("", false);
})();
