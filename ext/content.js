(() => {
  if (window.__shopee27052026ScrapeClickerInjected) {
    return;
  }
  window.__shopee27052026ScrapeClickerInjected = true;

  const MAX_WAIT_MS = 30_000;
  const STEP_MS = 500;

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  const isVisible = (element) => {
    if (!element) return false;
    const style = window.getComputedStyle(element);
    return (
      style &&
      style.display !== "none" &&
      style.visibility !== "hidden" &&
      style.opacity !== "0"
    );
  };

  const matchesScrape = (element) => {
    const label = [
      element.getAttribute("aria-label"),
      element.getAttribute("title"),
      element.textContent,
      element.value,
      element.dataset?.tooltip,
      element.dataset?.testid,
    ]
      .filter(Boolean)
      .join(" ")
      .toLowerCase();
    return label.includes("scrape");
  };

  const searchInRoot = (root) => {
    const selectors = [
      ".crawl_trigger.crawl_btn_wrapper.big_crawl.scraped button.btn_01.crawl_text.detail",
      ".crawl_trigger.crawl_btn_wrapper.big_crawl button.btn_01.crawl_text.detail",
      ".crawl_trigger.crawl_btn_wrapper.big_crawl button",
      "#scrapeBtn",
      ".bigseller-scrape",
      "[data-testid*='scrape']",
      "[aria-label*='scrape' i]",
      "[title*='scrape' i]",
    ];

    for (const selector of selectors) {
      const el = root.querySelector(selector);
      if (el && typeof el.click === "function" && isVisible(el)) return el;
    }

    const candidates = root.querySelectorAll(
      "button, [role='button'], input[type='button'], input[type='submit'], a"
    );
    for (const el of candidates) {
      if (typeof el.click !== "function" || !isVisible(el)) continue;
      if (matchesScrape(el)) return el;
    }

    for (const host of root.querySelectorAll("*")) {
      if (host.shadowRoot) {
        const found = searchInRoot(host.shadowRoot);
        if (found) return found;
      }
    }

    return null;
  };

  const notify = async (detail) => {
    try {
      await chrome.runtime.sendMessage({ type: "SCRAPE_RESULT", detail });
    } catch (_) {}
  };

  const run = async () => {
    const startedAt = Date.now();
    while (Date.now() - startedAt < MAX_WAIT_MS) {
      const button = searchInRoot(document);
      if (button) {
        button.click();
        await notify({ ok: true, message: "Đã click nút scrape BigSeller." });
        return;
      }
      await sleep(STEP_MS);
    }
    await notify({ ok: false, message: "Không tìm thấy nút scrape." });
  };

  run().catch(async (error) => {
    await notify({ ok: false, message: error.message || String(error) });
  });
})();
