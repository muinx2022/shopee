/**
 * Shopee Data Runner — thin executor for Multi Brave Manager.
 * Launcher điều khiển vòng lặp dòng / log / start-stop; extension chỉ:
 * - mở tab + hiện overlay thông báo
 * - click scrape và trả kết quả
 */

const SCRAPE_SCRIPT = "content.js";
const OVERLAY_SCRIPT = "overlay.js";
const TAB_LOAD_TIMEOUT_MS = 30_000;
const SCRAPE_WAIT_TIMEOUT_MS = 15_000;
const CAPTCHA_WAIT_TIMEOUT_MS = 10 * 60_000;
const CAPTCHA_CHECK_INTERVAL_MS = 2_000;
const MAX_SCRAPE_RETRIES = 1;

let scrapeWaiters = [];
let abortRequested = false;

const readState = async () => {
  const data = await chrome.storage.local.get("runnerState");
  return data.runnerState || { running: false };
};

const writeState = async (patch) => {
  const current = await readState();
  const next = { ...current, ...patch };
  await chrome.storage.local.set({ runnerState: next });
  return next;
};

const broadcastState = async () => {
  try {
    const state = await readState();
    await chrome.runtime.sendMessage({ type: "RUNNER_STATE", state });
  } catch (_) {}
};

const isInjectableUrl = (url) => {
  if (typeof url !== "string" || !url.trim()) return false;
  const trimmed = url.trim().toLowerCase();
  return (
    trimmed.startsWith("http://") ||
    trimmed.startsWith("https://") ||
    trimmed.startsWith("file://") ||
    trimmed.startsWith("ftp://")
  );
};

const normalizeLink = (value) => {
  if (typeof value !== "string") return "";
  const trimmed = value.trim();
  if (!trimmed) return "";
  if (/^https?:\/\//i.test(trimmed)) return trimmed;
  if (trimmed.startsWith("www.")) return `https://${trimmed}`;
  return trimmed;
};

const getTabSafe = async (tabId) => {
  if (!tabId) return null;
  try {
    return await chrome.tabs.get(tabId);
  } catch (_) {
    return null;
  }
};

const openLinkInTab = async (tabId, url) => {
  const existing = await getTabSafe(tabId);
  if (existing?.id) {
    try {
      await chrome.tabs.update(existing.id, { url, active: true });
      return existing.id;
    } catch (_) {
      // tab biến mất giữa get và update
    }
  }
  const created = await chrome.tabs.create({ url, active: true });
  return created.id;
};

const waitForTabComplete = async (tabId, timeoutMs = TAB_LOAD_TIMEOUT_MS) => {
  const tab = await getTabSafe(tabId);
  if (!tab) return false;
  if (tab.status === "complete") return true;

  return new Promise((resolve) => {
    const timeout = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
      resolve(false);
    }, timeoutMs);

    const cleanup = () => {
      clearTimeout(timeout);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
    };

    const onRemoved = (removedTabId) => {
      if (removedTabId !== tabId) return;
      cleanup();
      resolve(false);
    };

    const onUpdated = (updatedTabId, changeInfo) => {
      if (updatedTabId !== tabId || changeInfo.status !== "complete") return;
      cleanup();
      resolve(true);
    };

    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.onRemoved.addListener(onRemoved);
  });
};

const getCurrentTabUrl = async (tabId, fallback = "") => {
  const tab = await getTabSafe(tabId);
  return tab?.url || fallback || "";
};

const isVerifyUrl = (url) => {
  try {
    const parsed = new URL(url);
    return /^\/verify(?:\/|$)/i.test(parsed.pathname);
  } catch (_) {
    return false;
  }
};

const isShopeeProductUrl = (url) => {
  try {
    const parsed = new URL(url);
    const host = parsed.hostname.toLowerCase();
    if (!/(^|\.)shopee\./i.test(host)) return false;
    if (isVerifyUrl(url)) return false;
    return /-i\.\d+\.\d+/i.test(parsed.pathname) || /[?&](itemid|shopid)=/i.test(parsed.search);
  } catch (_) {
    return false;
  }
};

const waitForUrlStable = async (tabId, settleMs = 2500) => {
  let lastUrl = await getCurrentTabUrl(tabId);
  const deadline = Date.now() + settleMs;
  while (Date.now() < deadline) {
    await sleep(400);
    const current = await getCurrentTabUrl(tabId, lastUrl);
    if (current !== lastUrl) {
      lastUrl = current;
      continue;
    }
  }
  return lastUrl;
};

const injectOverlayManager = async (tabId) => {
  if (!(await getTabSafe(tabId))) return false;
  try {
    await chrome.scripting.executeScript({ target: { tabId }, files: [OVERLAY_SCRIPT] });
    return true;
  } catch (_) {
    return false;
  }
};

const injectScrapeClicker = async (tabId) => {
  if (!(await getTabSafe(tabId))) return false;
  try {
    await chrome.scripting.executeScript({ target: { tabId }, files: [SCRAPE_SCRIPT] });
    return true;
  } catch (_) {
    return false;
  }
};

const showOverlay = async (tabId, text) => {
  try {
    await chrome.tabs.sendMessage(tabId, { type: "SHOW_NEXT_LINK_MESSAGE", text });
  } catch (_) {}
};

const hideOverlay = async (tabId) => {
  try {
    await chrome.tabs.sendMessage(tabId, { type: "HIDE_NEXT_LINK_MESSAGE" });
  } catch (_) {}
};

const rejectAllScrapeWaiters = (message) => {
  while (scrapeWaiters.length) {
    const waiter = scrapeWaiters.shift();
    if (waiter?.resolve) waiter.resolve({ ok: false, message });
  }
};

const waitForScrapeResult = async (timeoutMs = SCRAPE_WAIT_TIMEOUT_MS) => {
  return new Promise((resolve) => {
    const waiter = { resolve: null };
    const timeout = setTimeout(() => {
      const index = scrapeWaiters.indexOf(waiter);
      if (index >= 0) scrapeWaiters.splice(index, 1);
      resolve({ ok: false, message: "Hết thời gian chờ kết quả scrape." });
    }, timeoutMs);
    waiter.resolve = (value) => {
      clearTimeout(timeout);
      resolve(value);
    };
    scrapeWaiters.push(waiter);
  });
};

const checkPageLoadSuccess = async (tabId) => {
  try {
    const results = await chrome.scripting.executeScript({
      target: { tabId },
      func: () => {
        const body = document.body?.innerText || "";
        const isProxyError =
          body.includes("ERR_PROXY_CONNECTION_FAILED") ||
          body.includes("ERR_TUNNEL_CONNECTION_FAILED") ||
          body.includes("ERR_PROXY_AUTH_UNSUPPORTED") ||
          body.includes("ERR_SOCKS_CONNECTION_FAILED") ||
          document.title === "No internet";
        return { ok: !isProxyError };
      },
    });
    return results?.[0]?.result ?? { ok: true };
  } catch (_) {
    return { ok: false };
  }
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const detectCaptcha = async (tabId) => {
  try {
    const tabUrl = await getCurrentTabUrl(tabId);
    const results = await chrome.scripting.executeScript({
      target: { tabId },
      args: [tabUrl],
      func: (currentTabUrl) => {
        const isVisible = (element) => {
          if (!element) return false;
          const style = window.getComputedStyle(element);
          const rect = element.getBoundingClientRect();
          return (
            style &&
            style.display !== "none" &&
            style.visibility !== "hidden" &&
            style.opacity !== "0" &&
            rect.width > 0 &&
            rect.height > 0
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
        const hasVisibleScrapeButton = () => {
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
            const el = document.querySelector(selector);
            if (el && isVisible(el)) return true;
          }
          for (const el of document.querySelectorAll("button, [role='button'], input[type='button'], input[type='submit'], a")) {
            if (isVisible(el) && matchesScrape(el)) return true;
          }
          return false;
        };
        const bodyClone = document.body?.cloneNode(true);
        bodyClone?.querySelector("#shopee27052026-next-link-overlay")?.remove();
        const text = (bodyClone?.innerText || "").toLowerCase();
        const url = currentTabUrl || location.href || "";
        const scrapeReady = hasVisibleScrapeButton();
        let verifyPath = /^\/verify(?:\/|$)/i.test(location.pathname);
        try {
          verifyPath = verifyPath || /^\/verify(?:\/|$)/i.test(new URL(url).pathname);
        } catch (_) {}
        const captchaText =
          text.includes("verify to continue") ||
          text.includes("please slide to complete the puzzle");
        const captchaVisible = captchaText;
        return { detected: captchaVisible || verifyPath, scrapeReady, captchaVisible, title: document.title || "", url };
      },
    });
    return results?.[0]?.result ?? { detected: false, title: "", url: "" };
  } catch (_) {
    return { detected: false, title: "", url: "" };
  }
};

const markCaptchaTab = async (tabId, label) => {
  try {
    await chrome.scripting.executeScript({
      target: { tabId },
      args: [label],
      func: (titlePrefix) => {
        if (!window.__launcherOriginalTitle) {
          window.__launcherOriginalTitle = document.title || "";
        }
        document.title = `${titlePrefix} | ${window.__launcherOriginalTitle}`.slice(0, 120);
      },
    });
  } catch (_) {}
};

const waitForCaptchaToClear = async (tabId, context) => {
  const firstCheck = await detectCaptcha(tabId);
  if (!firstCheck.detected) return { ok: true, waited: false };

  const instanceName = String(context?.instanceName || "Profile").trim();
  const rowNumber = Number(context?.rowNumber) || 0;
  const sku = String(context?.sku || "").trim();
  const rowText = rowNumber > 0 ? `dong ${rowNumber}` : "dong hien tai";
  const skuText = sku ? `\nSKU: ${sku}` : "";
  const message =
    `CAPTCHA - ${instanceName}\n${rowText}${skuText}\nDang chuyen sang profile ke tiep.`;

  await injectOverlayManager(tabId);
  await showOverlay(tabId, message);
  await markCaptchaTab(tabId, `CAPTCHA ${instanceName} ${rowText}`);

  return {
    ok: false,
    captcha: true,
    message: `Dung vi captcha - ${instanceName}, ${rowText}.`,
  };
};

const checkCurrentLinkBeforeNext = async (tabId, context) => {
  const deadline = Date.now() + 30_000;
  let currentUrl = "";
  let stableProductSince = 0;

  while (Date.now() < deadline) {
    currentUrl = await getCurrentTabUrl(tabId, currentUrl);
    const captchaCheck = await detectCaptcha(tabId);
    if (isVerifyUrl(currentUrl) || captchaCheck.detected) {
      break;
    }

    if (isShopeeProductUrl(currentUrl)) {
      stableProductSince ||= Date.now();
      if (Date.now() - stableProductSince >= 3000) {
        return { ok: true, waited: false, pageUrl: currentUrl };
      }
    } else {
      stableProductSince = 0;
    }

    await sleep(700);
  }

  currentUrl = await getCurrentTabUrl(tabId, currentUrl);
  const captchaCheck = await detectCaptcha(tabId);
  if (isVerifyUrl(currentUrl) || captchaCheck.detected) {
    const captchaWait = await waitForCaptchaToClear(tabId, context);
    if (!captchaWait.ok) {
      return {
        ok: false,
        captcha: true,
        aborted: Boolean(captchaWait.aborted),
        message: captchaWait.message || `Dang dung vi captcha - ${context?.instanceName || "Profile"}, dong ${context?.rowNumber || ""}.`,
        pageUrl: await getCurrentTabUrl(tabId, currentUrl),
      };
    }
    return {
      ok: true,
      waited: true,
      pageUrl: await getCurrentTabUrl(tabId, currentUrl),
    };
  }

  if (!isShopeeProductUrl(currentUrl)) {
    return {
      ok: false,
      captcha: false,
      message: `URL hien tai khong phai link san pham Shopee: ${currentUrl || "(trong)"}`,
      pageUrl: currentUrl,
    };
  }

  return { ok: true, waited: false, pageUrl: currentUrl };
};

/** Một bước: mở link → overlay → click scrape. Launcher xử lý video / log / vòng lặp. */
globalThis.__launcherExecuteScrapeStep = async (payload) => {
  try {
    abortRequested = false;
    const link = normalizeLink(payload?.link);
    const rowNumber = Number(payload?.rowNumber) || 0;
    const statusText =
      payload?.statusText ||
      (rowNumber > 0 ? `Đang xử lý dòng ${rowNumber}…` : "Đang xử lý…");
    const instanceName = String(payload?.instanceName || "Profile").trim();
    const sku = String(payload?.sku || "").trim();

    if (!link) {
      return { ok: false, scrapeOk: false, message: "Thiếu link.", tabId: null, pageUrl: "" };
    }

    let tabId = payload?.tabId ? Number(payload.tabId) : null;
    if (tabId && !(await getTabSafe(tabId))) {
      tabId = null;
    }

    tabId = await openLinkInTab(tabId, link);

    const loaded = await waitForTabComplete(tabId);
    if (!loaded) {
      tabId = await openLinkInTab(null, link);
      const reloaded = await waitForTabComplete(tabId);
      if (!reloaded) {
        return {
          ok: false,
          scrapeOk: false,
          message: "Tab đã đóng hoặc không tải được trang.",
          tabId: null,
          pageUrl: link,
        };
      }
    }

    if (abortRequested) {
      return { ok: false, scrapeOk: false, message: "Đã hủy.", tabId, pageUrl: link, aborted: true };
    }

    let currentPageUrl = await waitForUrlStable(tabId);
    if (isVerifyUrl(currentPageUrl)) {
      await injectOverlayManager(tabId);
      const verifyWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
      if (!verifyWait.ok) {
        return {
          ok: false,
          scrapeOk: false,
          captcha: true,
          aborted: Boolean(verifyWait.aborted),
          message: verifyWait.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
          tabId,
          pageUrl: currentPageUrl || link,
        };
      }
      currentPageUrl = await waitForUrlStable(tabId);
    }

    const pageCheck = await checkPageLoadSuccess(tabId);
    if (!pageCheck.ok) {
      return {
        ok: false,
        scrapeOk: false,
        proxyError: true,
        message: `Lỗi proxy / không tải được trang (dòng ${rowNumber}).`,
        tabId,
        pageUrl: currentPageUrl || link,
      };
    }

    await injectOverlayManager(tabId);
    await showOverlay(tabId, statusText);

    let captchaWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
    if (!captchaWait.ok) {
      return {
        ok: false,
        scrapeOk: false,
        captcha: true,
        aborted: Boolean(captchaWait.aborted),
        message: captchaWait.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
        tabId,
        pageUrl: await getCurrentTabUrl(tabId, currentPageUrl || link),
      };
    }

    let scrapeWaiterPromise = waitForScrapeResult();
    const injected = await injectScrapeClicker(tabId);
    if (!injected) {
      captchaWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
      if (captchaWait.ok) {
        scrapeWaiterPromise = waitForScrapeResult();
        const reinjected = await injectScrapeClicker(tabId);
        if (reinjected) {
          const retryResult = await scrapeWaiterPromise;
          if (retryResult?.ok) {
            let retryPageUrl = link;
            const retryTab = await getTabSafe(tabId);
            if (retryTab?.url) retryPageUrl = retryTab.url;
            return {
              ok: true,
              scrapeOk: true,
              captcha: Boolean(captchaWait.waited),
              message: retryResult.message || "Da click scrape sau captcha.",
              tabId,
              pageUrl: retryPageUrl,
            };
          }
        }
      }
      return {
        ok: false,
        scrapeOk: false,
        captcha: !captchaWait.ok || Boolean(captchaWait.waited),
        message: "Không inject được scrape clicker (tab có thể đã đóng).",
        tabId: null,
        pageUrl: link,
      };
    }

    let scrapeResult = await scrapeWaiterPromise;
    if (scrapeResult?.ok) {
      await waitForUrlStable(tabId, 1800);
    }
    let postScrapeCaptcha = await detectCaptcha(tabId);
    if (scrapeResult?.ok && postScrapeCaptcha.detected) {
      captchaWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
      if (!captchaWait.ok) {
        return {
          ok: false,
          scrapeOk: false,
          captcha: true,
          aborted: Boolean(captchaWait.aborted),
          message: captchaWait.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
          tabId,
          pageUrl: await getCurrentTabUrl(tabId, link),
        };
      }
      scrapeWaiterPromise = waitForScrapeResult();
      await chrome.scripting.executeScript({
        target: { tabId },
        func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
      }).catch(() => {});
      await injectScrapeClicker(tabId);
      scrapeResult = await scrapeWaiterPromise;
    }

    for (let retry = 1; retry <= MAX_SCRAPE_RETRIES && !scrapeResult?.ok && !abortRequested; retry++) {
      if (!(await getTabSafe(tabId))) {
        tabId = await openLinkInTab(null, link);
        await waitForTabComplete(tabId);
        await injectOverlayManager(tabId);
      }
      await showOverlay(tabId, `Thử lại scrape (lần ${retry}) — dòng ${rowNumber}…`);
      await chrome.scripting.executeScript({
        target: { tabId },
        func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
      }).catch(() => {});
      scrapeWaiterPromise = waitForScrapeResult();
      await injectScrapeClicker(tabId);
      scrapeResult = await scrapeWaiterPromise;
      if (!scrapeResult?.ok) {
        const retryCaptcha = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
        if (retryCaptcha.ok && retryCaptcha.waited) {
          scrapeWaiterPromise = waitForScrapeResult();
          await chrome.scripting.executeScript({
            target: { tabId },
            func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
          }).catch(() => {});
          await injectScrapeClicker(tabId);
          scrapeResult = await scrapeWaiterPromise;
        } else if (!retryCaptcha.ok) {
          return {
            ok: false,
            scrapeOk: false,
            captcha: true,
            aborted: Boolean(retryCaptcha.aborted),
            message: retryCaptcha.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
            tabId,
            pageUrl: link,
          };
        }
      }
    }

    if (abortRequested) {
      return { ok: false, scrapeOk: false, message: "Đã hủy.", tabId, pageUrl: link, aborted: true };
    }

    const beforeNext = await checkCurrentLinkBeforeNext(tabId, { instanceName, rowNumber, sku });
    if (!beforeNext.ok) {
      return {
        ok: false,
        scrapeOk: false,
        captcha: Boolean(beforeNext.captcha),
        aborted: Boolean(beforeNext.aborted),
        message: beforeNext.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
        tabId,
        pageUrl: beforeNext.pageUrl || link,
      };
    }
    if (beforeNext.waited) {
      scrapeWaiterPromise = waitForScrapeResult();
      await chrome.scripting.executeScript({
        target: { tabId },
        func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
      }).catch(() => {});
      await injectScrapeClicker(tabId);
      scrapeResult = await scrapeWaiterPromise;
    }
    let pageUrl = beforeNext.pageUrl || await getCurrentTabUrl(tabId, link);

    return {
      ok: Boolean(scrapeResult?.ok),
      scrapeOk: Boolean(scrapeResult?.ok),
      captcha: Boolean(captchaWait.waited || beforeNext.waited),
      message: scrapeResult?.message || (scrapeResult?.ok ? "Đã click scrape." : "Không tìm thấy nút scrape."),
      tabId,
      pageUrl,
    };
  } catch (error) {
    return {
      ok: false,
      scrapeOk: false,
      message: error?.message || String(error),
      tabId: null,
      pageUrl: payload?.link || "",
    };
  }
};

globalThis.__launcherCheckBeforeNextLink = async (payload) => {
  const tabId = payload?.tabId ? Number(payload.tabId) : null;
  if (!tabId || !(await getTabSafe(tabId))) {
    return { ok: true, waited: false, tabId: null, pageUrl: "" };
  }

  const context = {
    instanceName: String(payload?.instanceName || "Profile").trim(),
    rowNumber: Number(payload?.rowNumber) || 0,
    sku: String(payload?.sku || "").trim(),
  };
  const result = await checkCurrentLinkBeforeNext(tabId, context);
  return { ...result, tabId, pageUrl: result.pageUrl || await getCurrentTabUrl(tabId, "") };
};

globalThis.__launcherShowOverlay = async ({ tabId, text }) => {
  if (!tabId || !(await getTabSafe(tabId))) return { ok: false };
  try {
    await injectOverlayManager(tabId);
    await showOverlay(tabId, text || "");
    return { ok: true };
  } catch (_) {
    return { ok: false };
  }
};

globalThis.__launcherHideOverlay = async ({ tabId }) => {
  if (!tabId || !(await getTabSafe(tabId))) return { ok: false };
  try {
    await hideOverlay(tabId);
    return { ok: true };
  } catch (_) {
    return { ok: false };
  }
};

globalThis.__launcherAbortStep = async () => {
  abortRequested = true;
  rejectAllScrapeWaiters("Đã hủy từ launcher.");
  return { ok: true };
};

/** Launcher ghi trạng thái hiển thị cho popup (read-only). */
globalThis.__launcherSetDisplayState = async (state) => {
  await chrome.storage.local.set({ runnerState: state || { running: false } });
  await broadcastState();
  return { ok: true };
};

globalThis.__launcherGetRunnerState = async () => {
  const data = await chrome.storage.local.get(["runnerState", "lastRunConfig"]);
  return {
    runnerState: data.runnerState || { running: false },
    lastRunConfig: data.lastRunConfig || {},
  };
};

globalThis.__launcherApplyFormConfig = async (config) => {
  const sheetName = String(config?.sheetName || "").trim();
  const startRow = Number(config?.startRow);
  const endRow = Number(config?.endRow);
  const lastRunConfig = {
    sheetName,
    startRow: Number.isInteger(startRow) && startRow > 0 ? startRow : "",
    endRow: Number.isInteger(endRow) && endRow > 0 ? endRow : "",
  };
  await chrome.storage.local.set({ lastRunConfig });

  const state = await readState();
  await writeState({
    sheetName: sheetName || state.sheetName,
    startRow: lastRunConfig.startRow || state.startRow,
    endRow: lastRunConfig.endRow || state.endRow,
  });
  await broadcastState();
  return { ok: true, lastRunConfig };
};

/** Giữ tương thích cũ — chỉ hủy bước scrape đang chạy. */
globalThis.__launcherStopRun = async () => {
  await globalThis.__launcherAbortStep();
  const state = await readState();
  const next = await writeState({
    running: false,
    phase: "stopped",
    lastMessage: "Đã dừng từ Multi Brave Manager.",
  });
  await broadcastState();
  return {
    ok: true,
    lastCompletedRow: next.lastCompletedRow ?? null,
    currentRow: next.currentRow ?? null,
    sheetName: next.sheetName || next.lastSheetName || "",
    phase: next.phase,
  };
};

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "LAUNCHER_INVOKE") return undefined;

  const invoke = async () => {
    if (message.method === "probe") {
      return {
        ok: true,
        result: {
          hasScrapeStep: typeof globalThis.__launcherExecuteScrapeStep === "function",
        },
      };
    }

    const handlers = {
      executeScrapeStep: globalThis.__launcherExecuteScrapeStep,
      setDisplayState: globalThis.__launcherSetDisplayState,
      getRunnerState: globalThis.__launcherGetRunnerState,
      applyFormConfig: globalThis.__launcherApplyFormConfig,
      showOverlay: globalThis.__launcherShowOverlay,
      hideOverlay: globalThis.__launcherHideOverlay,
      abortStep: globalThis.__launcherAbortStep,
      stopRun: globalThis.__launcherStopRun,
      notifyRunnerUi: async () => {
        await broadcastState();
        return { ok: true };
      },
    };

    const fn = handlers[message.method];
    if (typeof fn !== "function") {
      return { ok: false, error: `Extension chưa hỗ trợ ${message.method}.` };
    }

    const result = await fn(message.payload);
    return { ok: true, result };
  };

  invoke()
    .then((response) => sendResponse(response))
    .catch((err) => sendResponse({ ok: false, error: String(err?.message || err) }));
  return true;
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "SCRAPE_RESULT") return undefined;

  const waiter = scrapeWaiters.shift();
  if (waiter) {
    waiter.resolve(message.detail || { ok: false, message: "Không có kết quả scrape." });
  }
  sendResponse({ ok: true });
  return false;
});
