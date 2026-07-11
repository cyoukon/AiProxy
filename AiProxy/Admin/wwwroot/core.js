// ─── AI Proxy Admin - Core ────────────────────────────────────────────────────
// Depends on: i18n.js (t(), setLang, currentLang, applyI18n, detectLang)
// Contains: constants, helpers, auth, timezone, tab switching, shared state, ESC listener.

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));
const STORAGE_KEY = 'aiproxy_admin_apikey';
const TZ_STORAGE_KEY = 'aiproxy_admin_tz';
const TEST_MODEL_HISTORY_KEY = 'aiproxy_test_model_history'; // 模型历史列表
const TEST_LAST_MODEL_KEY = 'aiproxy_test_last_model'; // 上次使用的模型
const ACTIVE_TAB_KEY = 'aiproxy_admin_active_tab'; // 当前激活页签

// ─── Structured View Toggle (state) ─────────────────────────────────────────
const STRUCTURED_VIEW_KEY = 'aiproxy_admin_structured_view';
function getStructuredView() { const v = localStorage.getItem(STRUCTURED_VIEW_KEY); return v === null ? true : v === '1'; }
function setStructuredView(on) { localStorage.setItem(STRUCTURED_VIEW_KEY, on ? '1' : '0'); }
let lastLogDetail = null; // cached raw log row for re-render without re-fetch

// ─── Shared Global State (cross-module) ─────────────────────────────────────
let currentLogId = null;
let currentConfig = null;
let lastServices = [];
let logsState = { page: 1, pageSize: 50, total: 0 };
// 保存各 body-panel 的原始文本（renderBodyPanel 的 body 参数原文），供复制按钮读取
const panelRawText = {};

function getApiKey() { return localStorage.getItem(STORAGE_KEY) || ''; }
function setApiKey(k) { localStorage.setItem(STORAGE_KEY, k); }

// ─── Language Switcher ──────────────────────────────────────────────────────
(function initLangSwitch() {
  const sel = $('#langSelect');
  sel.innerHTML = '<option value="zh">中文</option><option value="en">English</option>';
  sel.value = currentLang;
  sel.addEventListener('change', () => {
    setLang(sel.value);
    // Re-render dynamic elements that use t() in their generation
    renderTzSelector();
    renderAuthStatus();
    renderTableHeaders();
    refreshActiveTab();
    // Re-render an open log detail so semantic labels follow the new language
    if (lastLogDetail && !$('#logModal').classList.contains('hidden')) renderLogDetail(lastLogDetail);
  });
  // Apply initial i18n
  setLang(currentLang);
})();

// ─── Timezone Management ────────────────────────────────────────────────────
function getSavedTz() { return localStorage.getItem(TZ_STORAGE_KEY) || ''; }
function setSavedTz(tz) { localStorage.setItem(TZ_STORAGE_KEY, tz); }

function getActiveTz() {
  const saved = getSavedTz();
  if (saved === 'UTC') return 'UTC';
  if (saved) return saved;
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

function renderTzSelector() {
  const sel = $('#tzSelect');
  const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const savedValue = getSavedTz();
  const zones = [
    { value: '', label: `${t('tz.local')} (${browserTz})` },
    { value: 'UTC', label: 'UTC' },
    { value: 'Asia/Shanghai', label: 'Asia/Shanghai (CST)' },
    { value: 'Asia/Tokyo', label: 'Asia/Tokyo (JST)' },
    { value: 'America/New_York', label: 'America/New_York (ET)' },
    { value: 'America/Los_Angeles', label: 'America/Los_Angeles (PT)' },
    { value: 'Europe/London', label: 'Europe/London (GMT/BST)' },
  ];
  sel.innerHTML = zones.map(z => `<option value="${z.value}">${z.label}</option>`).join('');
  sel.value = savedValue;
}

(function initTzSelector() {
  renderTzSelector();
  $('#tzSelect').addEventListener('change', () => { setSavedTz($('#tzSelect').value); refreshActiveTab(); });
})();

// ─── Auth Status Rendering ──────────────────────────────────────────────────
function renderAuthStatus() {
  const saved = getApiKey();
  if (saved) {
    $('#authStatus').textContent = t('auth.saved');
    $('#authStatus').style.color = 'var(--success)';
  } else {
    $('#authStatus').textContent = t('auth.notConfigured');
    $('#authStatus').style.color = '';
  }
}

// ─── Utility Functions ──────────────────────────────────────────────────────
function fmtDate(d) {
  if (!d) return '';
  let raw = String(d);
  if (/^\d{4}-\d{2}-\d{2}T/.test(raw) && !/[Z+]/.test(raw) && !/T.+-.+$/.test(raw)) raw += 'Z';
  const dt = new Date(raw);
  if (isNaN(dt)) return String(d);
  const tz = getActiveTz();
  try {
    return dt.toLocaleString('sv-SE', { timeZone: tz, year:'numeric', month:'2-digit', day:'2-digit', hour:'2-digit', minute:'2-digit', second:'2-digit' }).replace(',','');
  } catch { return dt.toLocaleString('sv-SE').replace(',',''); }
}

function toast(msg, isError) {
  const el = $('#toast');
  el.textContent = msg;
  el.classList.toggle('error', !!isError);
  el.classList.add('show');
  setTimeout(() => el.classList.remove('show'), isError ? 4000 : 2200);
}

async function api(path, opts) {
  opts = opts || {};
  const headers = Object.assign({}, opts.headers || {});
  const key = getApiKey();
  if (key) headers['Authorization'] = 'Bearer ' + key;
  const fetchOpts = { method: opts.method || 'GET', headers };
  if (opts.body != null) {
    headers['Content-Type'] = 'application/json';
    fetchOpts.body = typeof opts.body === 'string' ? opts.body : JSON.stringify(opts.body);
  }
  const resp = await fetch(path, fetchOpts);
  let body = null;
  const ct = resp.headers.get('content-type') || '';
  if (ct.includes('application/json')) body = await resp.json();
  else body = await resp.text();
  if (resp.status === 401) {
    $('#authStatus').textContent = t('auth.failed');
    $('#authStatus').style.color = 'var(--failure)';
  }
  return { ok: resp.ok, status: resp.status, body };
}

function escapeHtml(s) {
  if (s == null) return '';
  return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}
function tryPrettyJson(text) {
  if (!text) return text;
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
}
function fromLocalInput(v) {
  if (!v) return null;
  const d = new Date(v);
  return isNaN(d) ? null : d.toISOString();
}

// ─── Dynamic Table Headers (re-rendered on language change) ─────────────────
function renderTableHeaders() {
  $('#overviewHead').innerHTML = `
    <th>${t('overview.name')}</th><th>${t('overview.prefix')}</th><th>${t('overview.baseUrl')}</th><th>${t('overview.apiKey')}</th>
    <th>${t('overview.logReq')}</th><th>${t('overview.logResp')}</th><th>${t('overview.status')}</th><th>${t('overview.todayCalls')}</th>`;
  $('#logsHead').innerHTML = `
    <th>${t('logs.id')}</th><th>${t('logs.time')}</th><th>${t('logs.service')}</th><th>${t('logs.method')}</th><th>${t('logs.path')}</th>
    <th>${t('logs.statusCode')}</th><th>${t('logs.duration')}</th><th>${t('logs.model')}</th><th>${t('logs.stream')}</th><th>${t('logs.replay')}</th><th>${t('logs.result')}</th><th>${t('logs.tokens')}</th>`;
  $('#statsHead').innerHTML = `
    <th>${t('stats.group')}</th><th>${t('stats.totalCalls')}</th><th>${t('stats.success')}</th><th>${t('stats.failure')}</th>
    <th>Prompt Tokens</th><th>Completion Tokens</th><th>Total Tokens</th><th>${t('stats.avgDuration')}</th>`;
  $('#configServicesHead').innerHTML = `
    <th>${t('config.name')}</th><th>${t('config.prefix')}</th><th>${t('config.format')}</th><th>${t('config.clientFormat')}</th><th>${t('config.baseUrl')}</th><th>${t('config.apiKey')}</th>
    <th>${t('config.logReq')}</th><th>${t('config.logResp')}</th><th>${t('config.allowSsl')}</th><th>${t('config.actions')}</th>`;
}
renderTableHeaders();

// ─── Tab Switching ──────────────────────────────────────────────────────────
function refreshActiveTab() {
  const activeBtn = document.querySelector('.tab-btn.active');
  if (!activeBtn) return;
  const tab = activeBtn.dataset.tab;
  switch (tab) {
    case 'overview': loadOverview(); break;
    case 'logs': loadLogs(); break;
    case 'stats': loadStats(); break;
    case 'config': loadConfigManagement(); break;
  }
}

// 激活指定页签：移除其他 active、给目标加 active、显示对应 .tab-content
function activateTab(tabName) {
  $$('.tab-btn').forEach(b => b.classList.remove('active'));
  $$('.tab-content').forEach(s => s.classList.remove('active'));
  const btn = document.querySelector(`.tab-btn[data-tab="${tabName}"]`);
  const content = $('#tab-' + tabName);
  if (btn && content) {
    btn.classList.add('active');
    content.classList.add('active');
  }
}

$$('.tab-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    activateTab(btn.dataset.tab);
    localStorage.setItem(ACTIVE_TAB_KEY, btn.dataset.tab);
    refreshActiveTab();
  });
});

// 页面加载时恢复上次激活的页签；不存在或对应 tab-btn 不存在时默认激活 overview
function restoreActiveTab() {
  const saved = localStorage.getItem(ACTIVE_TAB_KEY);
  const exists = saved && document.querySelector(`.tab-btn[data-tab="${saved}"]`);
  activateTab(exists ? saved : 'overview');
  refreshActiveTab();
}

// ─── Auth Input ─────────────────────────────────────────────────────────────
(function initAuth() {
  const saved = getApiKey();
  if (saved) { $('#apiKey').value = saved; }
  renderAuthStatus();
  $('#saveKey').addEventListener('click', () => {
    setApiKey($('#apiKey').value.trim());
    renderAuthStatus();
    toast(t('auth.keySaved'));
    loadOverview();
    loadConfigManagement();
  });
})();

// ─── ESC to close modals / exit fullscreen ──────────────────────────────────
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    // 优先退出全屏面板
    const fullscreenPanel = document.querySelector('.body-panel.fullscreen');
    if (fullscreenPanel) {
      fullscreenPanel.classList.remove('fullscreen');
      return;
    }
    // 然后关闭模态框
    if (!$('#logModal').classList.contains('hidden')) $('#logModal').classList.add('hidden');
    else if (!$('#serviceModal').classList.contains('hidden')) $('#serviceModal').classList.add('hidden');
    else if (!$('#testModal').classList.contains('hidden')) $('#testModal').classList.add('hidden');
    else if (!$('#globalKeyModal').classList.contains('hidden')) $('#globalKeyModal').classList.add('hidden');
  }
});
