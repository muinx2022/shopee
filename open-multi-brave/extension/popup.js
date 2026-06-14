'use strict';

const STORAGE_KEY = 'brave_instance_label';
const IP_API      = 'https://api.ipify.org?format=json';

const ipEl       = document.getElementById('ip');
const dotEl      = document.getElementById('dot');
const statusEl   = document.getElementById('statusText');
const badgeEl    = document.getElementById('badge');
const labelInput = document.getElementById('accountLabel');
const refreshBtn = document.getElementById('refreshBtn');

// ── Label persistence ──────────────────────────────────────────────────────

async function loadLabel() {
  const stored = await chrome.storage.local.get(STORAGE_KEY);
  const label  = stored[STORAGE_KEY] ?? '';
  labelInput.value = label;
  badgeEl.textContent = label || 'No label';
}

labelInput.addEventListener('input', () => {
  const label = labelInput.value.trim();
  chrome.storage.local.set({ [STORAGE_KEY]: labelInput.value });
  badgeEl.textContent = label || 'No label';
});

// ── IP / proxy check ───────────────────────────────────────────────────────

function setChecking() {
  ipEl.textContent     = '…';
  dotEl.className      = 'dot checking';
  statusEl.textContent = 'Đang kiểm tra…';
}

function setOnline(ip) {
  ipEl.textContent     = ip;
  dotEl.className      = 'dot online';
  statusEl.textContent = 'Proxy hoạt động ✓';
}

function setOffline(reason) {
  ipEl.textContent     = 'N/A';
  dotEl.className      = 'dot offline';
  statusEl.textContent = reason || 'Không kết nối được';
}

async function fetchIp() {
  setChecking();
  try {
    const res  = await fetch(IP_API, { cache: 'no-store' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    setOnline(data.ip);
  } catch (err) {
    setOffline('Proxy lỗi hoặc không hoạt động');
    console.error('[BraveInstanceInfo] IP fetch failed:', err);
  }
}

refreshBtn.addEventListener('click', fetchIp);

// ── Init ───────────────────────────────────────────────────────────────────

(async () => {
  await loadLabel();
  await fetchIp();
})();
