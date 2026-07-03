// ─── AI Proxy Admin Panel - Application Logic ───────────────────────────────
// Depends on: i18n.js (t(), setLang, currentLang, applyI18n, detectLang)

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));
const STORAGE_KEY = 'aiproxy_admin_apikey';
const TZ_STORAGE_KEY = 'aiproxy_admin_tz';
const TEST_MODEL_HISTORY_KEY = 'aiproxy_test_model_history'; // 模型历史列表
const TEST_LAST_MODEL_KEY = 'aiproxy_test_last_model'; // 上次使用的模型

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

$$('.tab-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    $$('.tab-btn').forEach(b => b.classList.remove('active'));
    $$('.tab-content').forEach(s => s.classList.remove('active'));
    btn.classList.add('active');
    $('#tab-' + btn.dataset.tab).classList.add('active');
    refreshActiveTab();
  });
});

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

// ─── Service Overview ───────────────────────────────────────────────────────
let lastServices = [];
async function loadOverview() {
  const resp = await api('/api/overview');
  if (!resp.ok) {
    const msg = resp.status === 401 ? t('overview.authFailed') : `${t('overview.loadFailed')} (${resp.status})`;
    $('#overviewTable tbody').innerHTML = `<tr><td colspan="8" class="empty">${msg}</td></tr>`;
    return;
  }
  const items = (resp.body && resp.body.services) || [];
  lastServices = items;
  const serviceSelects = [$('#logsService'), $('#statsService')];
  serviceSelects.forEach(sel => {
    const cur = sel.value;
    sel.innerHTML = `<option value="">${t('common.all')}</option>` +
      items.map(s => `<option value="${escapeHtml(s.name)}">${escapeHtml(s.name)} (${escapeHtml(s.pathPrefix)})</option>`).join('');
    sel.value = cur;
  });
  if (items.length === 0) {
    $('#overviewTable tbody').innerHTML = `<tr><td colspan="8" class="empty">${t('overview.noServices')}</td></tr>`;
    return;
  }
  $('#overviewTable tbody').innerHTML = items.map(s => `
    <tr>
      <td>${escapeHtml(s.name)}</td>
      <td><code>${escapeHtml(s.pathPrefix)}</code></td>
      <td><code>${escapeHtml(s.baseUrl)}</code></td>
      <td><code>${escapeHtml(s.apiKey)}</code></td>
      <td>${s.logRequestBody ? '✔' : '✘'}</td>
      <td>${s.logResponseBody ? '✔' : '✘'}</td>
      <td><span class="badge badge-success">${escapeHtml(s.status)}</span></td>
      <td>${s.todayCalls}</td>
    </tr>`).join('');
}

// ─── Logs ───────────────────────────────────────────────────────────────────
let logsState = { page: 1, pageSize: 50, total: 0 };
async function loadLogs() {
  const params = new URLSearchParams();
  params.set('page', logsState.page);
  params.set('pageSize', logsState.pageSize);
  const from = fromLocalInput($('#logsFrom').value);
  const to = fromLocalInput($('#logsTo').value);
  if (from) params.set('from', from);
  if (to) params.set('to', to);
  const service = $('#logsService').value;
  if (service) params.set('service', service);
  const model = $('#logsModel').value.trim();
  if (model) params.set('model', model);
  const status = $('#logsStatus').value;
  if (status !== '') params.set('status', status);

  const resp = await api('/api/logs?' + params.toString());
  if (!resp.ok) { $('#logsTable tbody').innerHTML = `<tr><td colspan="12" class="empty">${t('logs.loadFailed')} (${resp.status})</td></tr>`; return; }
  const data = resp.body || {};
  logsState.total = data.total || 0;
  const items = data.items || [];
  if (items.length === 0) {
    $('#logsTable tbody').innerHTML = `<tr><td colspan="12" class="empty">${t('logs.noData')}</td></tr>`;
  } else {
    $('#logsTable tbody').innerHTML = items.map(r => `
      <tr class="row-clickable" data-id="${r.id}">
        <td>${r.id}</td>
        <td>${fmtDate(r.requestTime)}</td>
        <td>${escapeHtml(r.serviceName)}</td>
        <td>${escapeHtml(r.method)}</td>
        <td><code title="${escapeHtml(r.downstreamUrl)}">${escapeHtml(r.clientPath)}</code></td>
        <td>${r.statusCode}</td>
        <td>${r.durationMs}</td>
        <td>${escapeHtml(r.model || '')}</td>
        <td>${r.isStream ? '<span class="badge badge-stream">SSE</span>' : ''}${r.isConverted ? '<span class="badge badge-convert">CVT</span>' : ''}</td>
        <td>${r.isReplay ? '<span class="badge badge-replay">REPLAY</span>' : ''}</td>
        <td>${r.isSuccess ? '<span class="badge badge-success">OK</span>' : '<span class="badge badge-failure">' + escapeHtml(r.errorType || 'FAIL') + '</span>'}</td>
        <td>${r.promptTokens ?? '-'}/${r.completionTokens ?? '-'}/${r.totalTokens ?? '-'}</td>
      </tr>`).join('');
    $$('#logsTable tbody tr.row-clickable').forEach(tr => {
      tr.addEventListener('click', () => {
        $$('#logsTable tbody tr.row-active').forEach(r => r.classList.remove('row-active'));
        tr.classList.add('row-active');
        openLogDetail(tr.dataset.id);
      });
    });
  }
  const totalPages = Math.max(1, Math.ceil(logsState.total / logsState.pageSize));
  $('#logsPageInfo').textContent = t('logs.pageInfo', { page: logsState.page, total: totalPages, count: logsState.total });
}
$('#logsSearch').addEventListener('click', () => { logsState.pageSize = parseInt($('#logsPageSize').value, 10) || 50; logsState.page = 1; loadLogs(); });
$('#logsPrev').addEventListener('click', () => { if (logsState.page > 1) { logsState.page--; loadLogs(); } });
$('#logsNext').addEventListener('click', () => { logsState.page++; loadLogs(); });

// ─── Log Detail & Replay ────────────────────────────────────────────────────
let currentLogId = null;
function renderBodyPanel(title, body, panelId) {
  const content = escapeHtml(tryPrettyJson(body)) || `<i>${t('logDetail.notRecorded')}</i>`;
  return `<div class="body-panel" id="${panelId}">
    <div class="body-panel-header">
      <h4>${title}</h4>
      <button class="btn btn-secondary btn-sm fullscreen-btn" data-panel="${panelId}" title="全屏">⛶</button>
    </div>
    <pre class="body">${content}</pre>
  </div>`;
}

async function openLogDetail(id) {
  const resp = await api('/api/logs/' + id);
  if (!resp.ok) { toast(t('logDetail.loadFailed') + ' (' + resp.status + ')'); return; }
  const r = resp.body || {};
  currentLogId = id;
  $('#replayStatus').textContent = '';
  $('#replayStatus').style.color = '';
  $('#replayResult').style.display = 'none';
  $('#replayResult').innerHTML = '';
  const detail = $('#logDetail');

  const convertBadge = r.isConverted
    ? `<span class="badge badge-convert">${escapeHtml(r.clientFormat)} → ${escapeHtml(r.serviceFormat)}</span>`
    : `<span class="badge badge-muted">${t('logDetail.noConvert')}</span>`;

  detail.innerHTML = `
    <div class="meta-grid">
      <div><div class="k">ID</div><div class="v">${r.id}</div></div>
      <div><div class="k">${t('logDetail.requestTime')}</div><div class="v">${fmtDate(r.requestTime)}</div></div>
      <div><div class="k">${t('logDetail.serviceName')}</div><div class="v">${escapeHtml(r.serviceName)}</div></div>
      <div><div class="k">${t('logDetail.method')}</div><div class="v">${escapeHtml(r.method)}</div></div>
      <div><div class="k">${t('logDetail.statusCode')}</div><div class="v">${r.statusCode}</div></div>
      <div><div class="k">${t('logDetail.duration')}</div><div class="v">${r.durationMs}ms</div></div>
      <div><div class="k">${t('logDetail.model')}</div><div class="v">${escapeHtml(r.model || '-')}</div></div>
      <div><div class="k">${t('logDetail.stream')}</div><div class="v">${r.isStream ? t('logDetail.yes') : t('logDetail.no')}</div></div>
      <div><div class="k">${t('logDetail.result')}</div><div class="v">${r.isSuccess ? t('logDetail.success') : t('logDetail.fail') + '(' + escapeHtml(r.errorType || '') + ')'}</div></div>
      <div><div class="k">${t('logDetail.convert')}</div><div class="v">${convertBadge}</div></div>
      <div><div class="k">Prompt Tokens</div><div class="v">${r.promptTokens ?? '-'}</div></div>
      <div><div class="k">Completion/Total</div><div class="v">${r.completionTokens ?? '-'} / ${r.totalTokens ?? '-'}</div></div>
    </div>

    <div class="dual-panel">
      <div class="panel-col">
        <div class="panel-title">${t('logDetail.clientSide')} <small>(${escapeHtml(r.clientFormat || '-')})</small></div>
        <div class="panel-path"><code>${escapeHtml(r.clientPath)}</code></div>
        ${renderBodyPanel(t('logDetail.requestBody'), r.clientRequestBody, 'panelClientReq')}
        ${renderBodyPanel(t('logDetail.responseBody'), r.clientResponseBody, 'panelClientResp')}
      </div>
      <div class="panel-col">
        <div class="panel-title">${t('logDetail.downstreamSide')} <small>(${escapeHtml(r.serviceFormat || '-')})</small></div>
        <div class="panel-path"><code>${escapeHtml(r.downstreamUrl)}</code></div>
        ${renderBodyPanel(t('logDetail.requestBody'), r.downstreamRequestBody, 'panelDownReq')}
        ${renderBodyPanel(t('logDetail.responseBody'), r.downstreamResponseBody, 'panelDownResp')}
      </div>
    </div>`;

  // Fullscreen toggle for body panels
  detail.querySelectorAll('.fullscreen-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const panel = detail.querySelector('#' + btn.dataset.panel);
      if (panel) panel.classList.toggle('fullscreen');
    });
  });

  $('#logModal').classList.remove('hidden');
}
$('#modalClose').addEventListener('click', () => $('#logModal').classList.add('hidden'));

async function replayLog(id) {
  const btn = $('#replayBtn');
  const status = $('#replayStatus');
  const result = $('#replayResult');
  btn.disabled = true;
  status.style.color = '';
  status.textContent = t('replay.running');
  result.style.display = 'none';
  result.innerHTML = '';
  try {
    const resp = await api('/api/logs/' + id + '/replay', { method: 'POST' });
    if (!resp.ok) {
      status.style.color = 'var(--failure)';
      status.textContent = (resp.body && resp.body.error) ? resp.body.error : (t('replay.failed') + ' (' + resp.status + ')');
      return;
    }
    const r = resp.body || {};
    const tokens = r.tokens || {};
    status.style.color = 'var(--success)';
    status.textContent = t('replay.done');
    result.style.display = 'block';
    result.innerHTML = `
      <div class="meta-grid">
        <div><div class="k">${t('replay.logId')}</div><div class="v"><a href="#" id="replayLink">${r.replayLogId}</a>（${t('replay.viewLog')}）</div></div>
        <div><div class="k">${t('logDetail.statusCode')}</div><div class="v">${r.statusCode}</div></div>
        <div><div class="k">${t('logDetail.duration')}</div><div class="v">${r.durationMs}</div></div>
        <div><div class="k">${t('logDetail.stream')}</div><div class="v">${r.isStream ? t('logDetail.yes') : t('logDetail.no')}</div></div>
        <div><div class="k">Tokens(P/C/T)</div><div class="v">${tokens.prompt ?? '-'}/${tokens.completion ?? '-'}/${tokens.total ?? '-'}</div></div>
      </div>
      <div class="body-section">
        <h4>${t('replay.responsePreview')}</h4>
        <pre class="body">${escapeHtml(tryPrettyJson(r.responseBody)) || '<i>' + t('replay.empty') + '</i>'}</pre>
      </div>`;
    const link = document.getElementById('replayLink');
    if (link) link.addEventListener('click', (e) => { e.preventDefault(); openLogDetail(r.replayLogId); });
  } catch (e) {
    status.style.color = 'var(--failure)';
    status.textContent = t('replay.error') + ': ' + (e && e.message ? e.message : e);
  } finally { btn.disabled = false; }
}
$('#replayBtn').addEventListener('click', () => { if (currentLogId != null) replayLog(currentLogId); });

// ─── Statistics ─────────────────────────────────────────────────────────────
let statsChart = null;
async function loadStats() {
  const params = new URLSearchParams();
  params.set('dimension', $('#statsDimension').value);
  params.set('granularity', $('#statsGranularity').value);
  const from = fromLocalInput($('#statsFrom').value);
  const to = fromLocalInput($('#statsTo').value);
  if (from) params.set('from', from);
  if (to) params.set('to', to);
  const service = $('#statsService').value;
  if (service) params.set('service', service);
  const model = $('#statsModel').value.trim();
  if (model) params.set('model', model);

  const resp = await api('/api/stats?' + params.toString());
  if (!resp.ok) { $('#statsTable tbody').innerHTML = `<tr><td colspan="8" class="empty">${t('stats.loadFailed')} (${resp.status})</td></tr>`; return; }
  const data = resp.body || {};
  const groups = data.groups || [];
  const series = data.series || null;
  const dimension = data.dimension === 1 ? 'model' : 'service';
  const isDaily = data.granularity === 0;

  if (groups.length === 0) {
    $('#statsTable tbody').innerHTML = `<tr><td colspan="8" class="empty">${t('stats.noData')}</td></tr>`;
  } else {
    $('#statsTable tbody').innerHTML = groups.map(g => `
      <tr>
        <td>${escapeHtml(dimension === 'service' ? (g.serviceName || g.key) : g.key)}</td>
        <td>${g.totalCalls}</td><td>${g.successCount}</td><td>${g.failureCount}</td>
        <td>${g.promptTokensTotal}</td><td>${g.completionTokensTotal}</td><td>${g.totalTokensTotal}</td>
        <td>${g.avgDurationMs}</td>
      </tr>`).join('');
  }

  const metric = $('#statsMetric').value;
  const metricLabel = $('#statsMetric').options[$('#statsMetric').selectedIndex].text;
  const ctx = $('#statsChart').getContext('2d');
  if (statsChart) { statsChart.destroy(); statsChart = null; }
  if (typeof Chart === 'undefined') return;

  if (isDaily && series && series.length > 0) {
    const allDates = Array.from(new Set(series.flatMap(s => s.buckets.map(b => b.date)))).sort();
    const labels = allDates.map(d => new Date(d).toISOString().slice(0, 10));
    const palette = ['#2563eb','#16a34a','#d97706','#dc2626','#7c3aed','#0891b2','#db2777','#65a30d'];
    const datasets = series.map((s, i) => ({
      label: s.key,
      data: allDates.map(d => { const b = s.buckets.find(x => x.date === d); return b ? b[metric] : 0; }),
      borderColor: palette[i % palette.length],
      backgroundColor: palette[i % palette.length] + '33',
      tension: 0.25, fill: false
    }));
    statsChart = new Chart(ctx, { type: 'line', data: { labels, datasets }, options: { responsive: true, maintainAspectRatio: false, plugins: { title: { display: true, text: t('stats.chartDaily', { metric: metricLabel }) } }, scales: { y: { beginAtZero: true } } } });
  } else {
    const labels = groups.map(g => dimension === 'service' ? (g.serviceName || g.key) : g.key);
    const values = groups.map(g => g[metric]);
    statsChart = new Chart(ctx, { type: 'bar', data: { labels, datasets: [{ label: metricLabel, data: values, backgroundColor: '#2563eb88', borderColor: '#2563eb', borderWidth: 1 }] }, options: { responsive: true, maintainAspectRatio: false, plugins: { title: { display: true, text: t('stats.chartCumulative', { metric: metricLabel }) } }, scales: { y: { beginAtZero: true } } } });
  }
}
$('#statsSearch').addEventListener('click', () => loadStats());

// ─── Config Management ──────────────────────────────────────────────────────
let currentConfig = null;

function displayHost(listenAddress, listenPort) {
  const host = (!listenAddress || listenAddress === '*' || listenAddress === '0.0.0.0' || listenAddress === '[::]')
    ? 'localhost' : listenAddress;
  return `${host}:${listenPort}`;
}

async function loadConfigManagement() {
  const resp = await api('/api/config');
  if (!resp.ok) {
    const msg = resp.status === 401 ? t('overview.authFailed') : `${t('overview.loadFailed')} (${resp.status})`;
    $('#cfgListen').textContent = msg;
    $('#configServicesTable tbody').innerHTML = `<tr><td colspan="9" class="empty">${msg}</td></tr>`;
    return;
  }
  const cfg = resp.body || {};
  currentConfig = cfg;
  const proxy = cfg.proxy || {};
  const services = cfg.aiServices || [];

  const addr = proxy.listenAddress || 'localhost';
  const port = proxy.listenPort || 8000;
  const hostDisplay = displayHost(addr, port);
  $('#cfgListen').textContent = `${addr}:${port}`;
  $('#cfgSample').textContent = `http://${hostDisplay}/<prefix>`;
  $('#cfgManageUrl').textContent = `http://${hostDisplay}/`;

  const gKeyMasked = proxy.globalApiKey || '';
  $('#cfgGlobalKey').textContent = gKeyMasked || t('config.notConfigured');
  const authBadge = $('#cfgAuthBadge');
  authBadge.innerHTML = proxy.authEnabled
    ? `<span class="badge badge-success">${t('config.authEnabled')}</span>`
    : `<span class="badge badge-muted">${t('config.authDisabled')}</span>`;

  if (services.length === 0) {
    $('#configServicesTable tbody').innerHTML = `<tr><td colspan="10" class="empty">${t('config.noServices')}</td></tr>`;
  } else {
    $('#configServicesTable tbody').innerHTML = services.map(s => {
      const cf = s.clientFormat && s.clientFormat !== 'Auto' ? s.clientFormat : null;
      const sf = s.serviceFormat || 'OpenAI';
      let cfCell;
      if (!cf) {
        cfCell = `<span class="badge badge-muted" title="${escapeHtml(t('service.clientFormatHintAuto'))}">${escapeHtml(t('config.clientFormatAuto'))}</span>`;
      } else if (cf !== sf) {
        cfCell = `<span class="badge badge-stream" title="${escapeHtml(t('service.clientFormatHintConvert'))}">${escapeHtml(cf)}</span>`;
      } else {
        cfCell = `<span class="badge badge-muted" title="${escapeHtml(t('service.clientFormatHintSame'))}">${escapeHtml(cf)}</span>`;
      }
      return `
      <tr data-name="${escapeHtml(s.name)}">
        <td>${escapeHtml(s.name)}</td>
        <td><code>${escapeHtml(s.pathPrefix)}</code></td>
        <td><span class="badge ${s.serviceFormat === 'Anthropic' ? 'badge-stream' : 'badge-success'}">${escapeHtml(s.serviceFormat || 'OpenAI')}</span></td>
        <td>${cfCell}</td>
        <td><code>${escapeHtml(s.baseUrl)}</code></td>
        <td><code>${escapeHtml(s.apiKey || t('config.noKey'))}</code></td>
        <td>${s.logRequestBody ? '✔' : '✘'}</td>
        <td>${s.logResponseBody ? '✔' : '✘'}</td>
        <td>${s.allowInvalidSslCertificates ? '✔' : '✘'}</td>
        <td class="actions-cell">
          <button class="btn btn-sm" data-act="test" data-name="${escapeHtml(s.name)}">${t('test.btn')}</button>
          <button class="btn btn-secondary btn-sm" data-act="edit" data-name="${escapeHtml(s.name)}">${t('config.editBtn')}</button>
          <button class="btn btn-danger btn-sm" data-act="del" data-name="${escapeHtml(s.name)}">${t('config.deleteBtn')}</button>
        </td>
      </tr>`;
    }).join('');
    $$('#configServicesTable tbody button[data-act="test"]').forEach(btn => {
      btn.addEventListener('click', (e) => { e.stopPropagation(); openTestModal(btn.dataset.name); });
    });
    $$('#configServicesTable tbody button[data-act="edit"]').forEach(btn => {
      btn.addEventListener('click', (e) => { e.stopPropagation(); openServiceModal('edit', btn.dataset.name); });
    });
    $$('#configServicesTable tbody button[data-act="del"]').forEach(btn => {
      btn.addEventListener('click', (e) => { e.stopPropagation(); deleteService(btn.dataset.name); });
    });
  }
}

// ─── Service Modal ──────────────────────────────────────────────────────────
let serviceModalMode = 'add';
let serviceEditOriginalName = null;

function updateFormatHint() {
  const fmt = $('#fServiceFormat').value;
  $('#fFormatHint').textContent = fmt === 'Anthropic' ? t('service.formatHintAnthropic') : t('service.formatHintOpenAI');
}
$('#fServiceFormat').addEventListener('change', updateFormatHint);

function updateClientFormatHint() {
  const cf = $('#fClientFormat').value;
  const sf = $('#fServiceFormat').value;
  const hint = $('#fClientFormatHint');
  if (cf === 'Auto' || !cf) {
    hint.textContent = t('service.clientFormatHintAuto');
    hint.style.color = '';
  } else {
    const same = cf === sf;
    hint.textContent = same ? t('service.clientFormatHintSame') : t('service.clientFormatHintConvert');
    hint.style.color = same ? '' : 'var(--success)';
  }
}
$('#fClientFormat').addEventListener('change', updateClientFormatHint);
$('#fServiceFormat').addEventListener('change', updateClientFormatHint);

function openServiceModal(mode, name) {
  serviceModalMode = mode;
  serviceEditOriginalName = name;
  $('#serviceForm').reset();
  $('#fApiKeyEditBlock').style.display = 'none';
  $('#fApiKeyAddBlock').style.display = 'block';

  const addr = currentConfig?.proxy?.listenAddress || 'localhost';
  const port = currentConfig?.proxy?.listenPort || 8000;
  const host = displayHost(addr, port);
  $('#fPrefixHint').textContent = `http://${host}/<prefix>`;
  updateFormatHint();
  $('#fClientFormat').value = 'Auto';
  updateClientFormatHint();

  if (mode === 'edit' && currentConfig) {
    const s = (currentConfig.aiServices || []).find(x => x.name === name);
    if (!s) { toast(t('service.notFound') + ': ' + name, true); return; }
    $('#serviceModalTitle').textContent = t('service.editTitle') + ': ' + s.name;
    $('#fName').value = s.name;
    $('#fPrefix').value = s.pathPrefix;
    $('#fBaseUrl').value = s.baseUrl;
    $('#fServiceFormat').value = s.serviceFormat || 'OpenAI';
    $('#fClientFormat').value = (s.clientFormat && s.clientFormat !== 'Auto') ? s.clientFormat : 'Auto';
    updateFormatHint();
    updateClientFormatHint();
    $('#fLogReq').checked = s.logRequestBody;
    $('#fLogResp').checked = s.logResponseBody;
    $('#fAllowInvalidSsl').checked = !!s.allowInvalidSslCertificates;
    $('#fApiKeyCurrent').textContent = s.apiKey || t('config.noKey');
    $('#fApiKeyEditBlock').style.display = 'block';
    $('#fApiKeyAddBlock').style.display = 'none';
    document.querySelector('input[name="fKeyMode"][value="keep"]').checked = true;
    $('#fApiKeySet').value = '';
  } else {
    $('#serviceModalTitle').textContent = t('service.addTitle');
  }
  $('#serviceModal').classList.remove('hidden');
}

$('#addServiceBtn').addEventListener('click', () => openServiceModal('add', null));
$('#serviceModalClose').addEventListener('click', () => $('#serviceModal').classList.add('hidden'));
$('#serviceCancel').addEventListener('click', () => $('#serviceModal').classList.add('hidden'));

async function saveService() {
  const name = $('#fName').value.trim();
  const prefix = $('#fPrefix').value.trim();
  const baseUrl = $('#fBaseUrl').value.trim();
  if (!name) { toast(t('service.nameRequired'), true); return; }
  if (!prefix) { toast(t('service.prefixRequired'), true); return; }
  if (!baseUrl) { toast(t('service.baseUrlRequired'), true); return; }

  let apiKey = null;
  if (serviceModalMode === 'add') {
    apiKey = $('#fApiKeyAdd').value;
  } else {
    const mode = document.querySelector('input[name="fKeyMode"]:checked').value;
    if (mode === 'set') apiKey = $('#fApiKeySet').value;
    else if (mode === 'clear') apiKey = '';
  }

  const payload = {
    name, pathPrefix: prefix, baseUrl, apiKey,
    serviceFormat: $('#fServiceFormat').value,
    clientFormat: $('#fClientFormat').value,
    logRequestBody: $('#fLogReq').checked,
    logResponseBody: $('#fLogResp').checked,
    allowInvalidSslCertificates: $('#fAllowInvalidSsl').checked
  };

  const saveBtn = $('#serviceSave');
  saveBtn.disabled = true;
  saveBtn.textContent = t('service.saving');
  try {
    const resp = serviceModalMode === 'add'
      ? await api('/api/ai-services', { method: 'POST', body: payload })
      : await api('/api/ai-services/' + encodeURIComponent(serviceEditOriginalName), { method: 'PUT', body: payload });
    if (!resp.ok) { toast((resp.body && resp.body.error) || (t('service.saveFailed') + ' (' + resp.status + ')'), true); return; }
    $('#serviceModal').classList.add('hidden');
    toast(t('service.saved'));
    await loadConfigManagement();
    await loadOverview();
  } catch (e) { toast(t('service.saveError') + ': ' + (e?.message || e), true); }
  finally { saveBtn.disabled = false; saveBtn.textContent = t('service.save'); }
}
$('#serviceSave').addEventListener('click', saveService);

async function deleteService(name) {
  if (!confirm(t('delete.confirm', { name }))) return;
  const resp = await api('/api/ai-services/' + encodeURIComponent(name), { method: 'DELETE' });
  if (!resp.ok) { toast((resp.body && resp.body.error) || (t('delete.failed') + ' (' + resp.status + ')'), true); return; }
  toast(t('delete.done'));
  await loadConfigManagement();
  await loadOverview();
}

// ─── Global Key Modal ───────────────────────────────────────────────────────
$('#editGlobalKeyBtn').addEventListener('click', () => {
  $('#globalKeyForm').reset();
  $('#gKeyCurrent').textContent = currentConfig?.proxy?.globalApiKey || t('config.notConfigured');
  document.querySelector('input[name="gKeyMode"][value="keep"]').checked = true;
  $('#gKeySet').value = '';
  $('#globalKeyModal').classList.remove('hidden');
});
$('#globalKeyModalClose').addEventListener('click', () => $('#globalKeyModal').classList.add('hidden'));
$('#globalKeyCancel').addEventListener('click', () => $('#globalKeyModal').classList.add('hidden'));

async function saveGlobalKey() {
  const mode = document.querySelector('input[name="gKeyMode"]:checked').value;
  let apiKey = null;
  if (mode === 'set') apiKey = $('#gKeySet').value;
  else if (mode === 'clear') apiKey = '';

  const saveBtn = $('#globalKeySave');
  saveBtn.disabled = true;
  saveBtn.textContent = t('globalKey.saving');
  try {
    const resp = await api('/api/config/global-api-key', { method: 'PUT', body: { apiKey } });
    if (!resp.ok) { toast((resp.body && resp.body.error) || (t('globalKey.saveFailed') + ' (' + resp.status + ')'), true); return; }
    $('#globalKeyModal').classList.add('hidden');
    toast(t('globalKey.saved'));
    await loadConfigManagement();
  } catch (e) { toast(t('globalKey.saveError') + ': ' + (e?.message || e), true); }
  finally { saveBtn.disabled = false; saveBtn.textContent = t('globalKey.save'); }
}
$('#globalKeySave').addEventListener('click', saveGlobalKey);

// ─── Service Test ───────────────────────────────────────────────────────────
// 测试请求走真实代理路径 /<prefix><path>（GlobalAuthMiddleware → RequestLoggingMiddleware
// → ForwardingEndpoint 全链路），复制的 JS 脚本即真实客户端可直接运行的脚本。
// 鉴权头始终按所选客户端格式发送（值为已保存的全局 Key，无 Key 用哨兵值保证头存在以触发
// Auto 格式识别）；下游真实密钥由服务端 ApplyAuthorization 注入，前端/脚本不暴露。
let testService = null;

// ─── Test Model History ──────────────────────────────────────────────────────
function getTestModelHistory() {
  try { return JSON.parse(localStorage.getItem(TEST_MODEL_HISTORY_KEY)) || []; }
  catch { return []; }
}
function setTestModelHistory(list) {
  localStorage.setItem(TEST_MODEL_HISTORY_KEY, JSON.stringify(list));
}
function getTestLastModel() {
  return localStorage.getItem(TEST_LAST_MODEL_KEY) || '';
}
function setTestLastModel(model) {
  localStorage.setItem(TEST_LAST_MODEL_KEY, model);
}
function addTestModelToHistory(model) {
  if (!model || !model.trim()) return;
  const list = getTestModelHistory();
  const idx = list.indexOf(model);
  if (idx >= 0) list.splice(idx, 1); // 移到最前
  list.unshift(model);
  // 保留最近 20 个
  if (list.length > 20) list.length = 20;
  setTestModelHistory(list);
  setTestLastModel(model);
}
function removeTestModelFromHistory(model) {
  const list = getTestModelHistory();
  const idx = list.indexOf(model);
  if (idx >= 0) { list.splice(idx, 1); setTestModelHistory(list); }
  // 如果删除的是上次使用的，清空上次记录
  if (getTestLastModel() === model) localStorage.removeItem(TEST_LAST_MODEL_KEY);
}
function clearTestModelHistory() {
  localStorage.removeItem(TEST_MODEL_HISTORY_KEY);
  localStorage.removeItem(TEST_LAST_MODEL_KEY);
}
function renderTestModelList() {
  const sel = $('#tModelSelect');
  const history = getTestModelHistory();
  const lastModel = getTestLastModel();
  const zh = currentLang === 'zh';
  sel.innerHTML = `<option value="">${zh ? '历史...' : 'History...'}</option>` +
    history.map(m => `<option value="${escapeHtml(m)}">${escapeHtml(m)}</option>`).join('');
  // 选中上次使用的模型
  if (lastModel && history.includes(lastModel)) {
    sel.value = lastModel;
  }
  // 同步到输入框
  if (lastModel) {
    $('#tModelInput').value = lastModel;
  }
}

// 从请求体提取模型名称
function extractModelFromBody(bodyText) {
  if (!bodyText || !bodyText.trim()) return '';
  try {
    const obj = JSON.parse(bodyText);
    return obj.model || '';
  } catch { return ''; }
}

// 更新请求体中的模型字段
function updateBodyModel(bodyText, newModel) {
  if (!bodyText || !bodyText.trim()) return bodyText;
  try {
    const obj = JSON.parse(bodyText);
    if (newModel && newModel.trim()) obj.model = newModel.trim();
    else delete obj.model;
    return JSON.stringify(obj, null, 2);
  } catch { return bodyText; }
}

// 尝试将上次使用的模型应用到当前请求体
function applyLastModelToBody() {
  const lastModel = getTestLastModel();
  if (!lastModel) return;
  const body = $('#tBody').value;
  // 直接替换/设置模型，不判断当前是否有模型
  const newBody = updateBodyModel(body, lastModel);
  if (newBody !== body) {
    $('#tBody').value = newBody;
    refreshTestUi();
  }
}

const STREAM_RE = /"stream"\s*:\s*true/;
const PROXY_KEY_PLACEHOLDER = '<YOUR_PROXY_API_KEY>';

function testSampleBody(format) {
  if (format === 'Anthropic') {
    return JSON.stringify({
      model: 'claude-3-5-sonnet-20241022',
      max_tokens: 1024,
      messages: [{ role: 'user', content: 'Hello! 用一句话介绍你自己。' }]
    }, null, 2);
  }
  return JSON.stringify({
    model: 'gpt-4o-mini',
    messages: [{ role: 'user', content: 'Hello! 用一句话介绍你自己。' }]
  }, null, 2);
}

function testDefaultPath(format) {
  // Anthropic BaseUrl 通常已含 /v1，客户端路径只需 /messages
  return format === 'Anthropic' ? '/messages' : '/chat/completions';
}

function testEffectiveClientFormat(s) {
  // 显式配置优先；Auto 默认取下游格式（identity，最常见测试场景）
  const cf = s.clientFormat && s.clientFormat !== 'Auto' ? s.clientFormat : null;
  return cf || (s.serviceFormat || 'OpenAI');
}

function testHost() {
  const addr = currentConfig?.proxy?.listenAddress || 'localhost';
  const port = currentConfig?.proxy?.listenPort || 8000;
  return displayHost(addr, port);
}

function testCleanPath() {
  const p = ($('#tPath').value || '').trim();
  return p.startsWith('/') ? p : '/' + p;
}

function testTargetUrl() {
  if (!testService) return '-';
  return `http://${testHost()}/${testService.pathPrefix}${testCleanPath()}`;
}

function refreshTestUi() {
  const method = $('#tMethod').value;
  const isGet = method === 'GET';
  $('#tBodyHint').style.display = isGet ? '' : 'none';
  $('#tBody').disabled = isGet;
  $('#tTargetUrl').textContent = testTargetUrl();
  $('#tScript').textContent = buildJsScript();
}

function openTestModal(name) {
  const s = (currentConfig?.aiServices || []).find(x => x.name === name);
  if (!s) { toast(t('service.notFound') + ': ' + name, true); return; }
  testService = s;
  $('#testModalTitle').textContent = t('test.title') + ': ' + s.name;

  const cfgCf = s.clientFormat && s.clientFormat !== 'Auto' ? s.clientFormat : t('config.clientFormatAuto');
  $('#testServiceMeta').innerHTML = `
    <span class="meta-item">${t('test.serviceName')}: <code>${escapeHtml(s.name)}</code></span>
    <span class="meta-item">${t('test.prefix')}: <code>/${escapeHtml(s.pathPrefix)}</code></span>
    <span class="meta-item">${t('test.serviceFormat')}: <span class="badge ${s.serviceFormat === 'Anthropic' ? 'badge-stream' : 'badge-success'}">${escapeHtml(s.serviceFormat || 'OpenAI')}</span></span>
    <span class="meta-item">${t('test.configClientFormat')}: <span class="badge badge-muted">${escapeHtml(cfgCf)}</span></span>`;

  const cfEff = testEffectiveClientFormat(s);
  $('#tMethod').value = 'POST';
  $('#tClientFormat').value = cfEff;
  $('#tPath').value = testDefaultPath(cfEff);
  $('#tBody').value = testSampleBody(cfEff);
  $('#tStream').checked = false;

  // 渲染模型历史下拉列表，并尝试应用上次使用的模型到 body
  renderTestModelList();
  applyLastModelToBody();

  $('#tResponse').textContent = '';
  $('#tResponse').setAttribute('data-placeholder', t('test.noResponse'));
  $('#tStatusLine').innerHTML = '';
  refreshTestUi();
  $('#testModal').classList.remove('hidden');
}
$('#testModalClose').addEventListener('click', () => $('#testModal').classList.add('hidden'));
$('#testCancel').addEventListener('click', () => $('#testModal').classList.add('hidden'));
$('#tReset').addEventListener('click', () => { if (testService) openTestModal(testService.name); });

$('#tMethod').addEventListener('change', refreshTestUi);
$('#tPath').addEventListener('input', refreshTestUi);
$('#tClientFormat').addEventListener('change', () => {
  if (!testService) return;
  const fmt = $('#tClientFormat').value;
  $('#tPath').value = testDefaultPath(fmt);
  $('#tBody').value = testSampleBody(fmt);
  $('#tStream').checked = false;
  refreshTestUi();
});
$('#tBody').addEventListener('input', () => {
  $('#tStream').checked = STREAM_RE.test($('#tBody').value);
  refreshTestUi();
});
$('#tStream').addEventListener('change', () => {
  const on = $('#tStream').checked;
  const ta = $('#tBody');
  const text = ta.value.trim();
  if (text) {
    try {
      const obj = JSON.parse(text);
      if (on) obj.stream = true; else delete obj.stream;
      ta.value = JSON.stringify(obj, null, 2);
    } catch { /* 非合法 JSON，不动 body，仅靠 body 内容决定流式 */ }
  }
  refreshTestUi();
});

// 模型输入框：输入模型名并回车时应用到请求体
$('#tModelInput').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    const model = $('#tModelInput').value.trim();
    if (model) {
      $('#tBody').value = updateBodyModel($('#tBody').value, model);
      refreshTestUi();
    }
  }
});
// 输入框 change 时也应用（失焦等场景）
$('#tModelInput').addEventListener('change', () => {
  const model = $('#tModelInput').value.trim();
  if (model) {
    $('#tBody').value = updateBodyModel($('#tBody').value, model);
    refreshTestUi();
  }
});

// 从历史选择模型：同步到输入框并应用到请求体
$('#tModelSelect').addEventListener('change', () => {
  const model = $('#tModelSelect').value;
  if (model) {
    $('#tModelInput').value = model;
    $('#tBody').value = updateBodyModel($('#tBody').value, model);
    refreshTestUi();
  }
});

// 删除当前选中的模型从历史
$('#tDeleteModel').addEventListener('click', () => {
  const model = $('#tModelSelect').value;
  if (!model) { toast(t('test.noModelInput'), true); return; }
  removeTestModelFromHistory(model);
  renderTestModelList();
  toast(t('test.modelDeleted'));
});

// 清空模型历史
$('#tClearModels').addEventListener('click', () => {
  if (!confirm(t('test.confirmClearModels'))) return;
  clearTestModelHistory();
  renderTestModelList();
  $('#tModelInput').value = '';
  toast(t('test.modelsCleared'));
});

async function sendTestRequest() {
  if (!testService) return;
  const s = testService;
  const method = $('#tMethod').value;
  const path = ($('#tPath').value || '').trim();
  if (!path) { toast(t('service.prefixRequired'), true); return; }

  const url = `/${s.pathPrefix}${testCleanPath()}`;
  const clientFormat = $('#tClientFormat').value;
  const body = $('#tBody').value;
  const isGet = method === 'GET';

  // 统一规则：始终按客户端格式发送对应鉴权头，值为已保存全局 Key（无则哨兵保证头存在）
  const keyVal = getApiKey() || '__aiproxy_test_no_auth__';
  const headers = { 'Content-Type': 'application/json' };
  if (clientFormat === 'Anthropic') {
    headers['x-api-key'] = keyVal;
    headers['anthropic-version'] = '2023-06-01';
  } else {
    headers['Authorization'] = 'Bearer ' + keyVal;
  }

  const fetchOpts = { method, headers };
  if (!isGet && body.trim()) fetchOpts.body = body;

  const sendBtn = $('#tSend');
  const respEl = $('#tResponse');
  const statusEl = $('#tStatusLine');
  sendBtn.disabled = true;
  sendBtn.textContent = t('test.sending');
  respEl.textContent = '';
  respEl.removeAttribute('data-placeholder');
  statusEl.innerHTML = `<span class="streaming">${t('test.sending')}</span>`;

  const t0 = performance.now();
  try {
    const resp = await fetch(url, fetchOpts);
    if (resp.status === 401) {
      const txt = await resp.text();
      statusEl.innerHTML = `<span class="k">${t('test.status')}:</span><span class="v" style="color:var(--failure)">401</span>`;
      respEl.textContent = tryPrettyJson(txt) || t('test.authRequired');
      toast(t('test.authRequired'), true);
      return;
    }
    const ct = resp.headers.get('content-type') || '';
    const isSse = ct.includes('text/event-stream');
    if (isSse) {
      statusEl.innerHTML = `<span class="k">${t('test.status')}:</span><span class="v">${resp.status}</span> <span class="badge badge-stream">SSE</span> <span class="streaming">${t('test.streaming')}</span>`;
      await readSseStream(resp, respEl);
      const dur = Math.round(performance.now() - t0);
      statusEl.innerHTML = `<span class="k">${t('test.status')}:</span><span class="v">${resp.status}</span> <span class="badge badge-stream">SSE</span> <span class="k">${t('test.duration')}:</span><span class="v">${dur}ms</span>`;
      // SSE 流结束且成功时记录模型
      if (resp.ok && !isGet) {
        const model = extractModelFromBody(body);
        if (model) { addTestModelToHistory(model); renderTestModelList(); }
      }
    } else {
      const text = await resp.text();
      const dur = Math.round(performance.now() - t0);
      statusEl.innerHTML = `<span class="k">${t('test.status')}:</span><span class="v" style="color:${resp.ok ? 'var(--success)' : 'var(--failure)'}">${resp.status}</span> <span class="k">${t('test.duration')}:</span><span class="v">${dur}ms</span>`;
      respEl.textContent = tryPrettyJson(text) || t('test.emptyResponse');
      // 非流式请求成功时记录模型
      if (resp.ok && !isGet) {
        const model = extractModelFromBody(body);
        if (model) { addTestModelToHistory(model); renderTestModelList(); }
      }
    }
  } catch (e) {
    const dur = Math.round(performance.now() - t0);
    statusEl.innerHTML = `<span class="k">${t('test.status')}:</span><span class="v" style="color:var(--failure)">ERR</span> <span class="k">${t('test.duration')}:</span><span class="v">${dur}ms</span>`;
    respEl.textContent = (e && e.message) ? e.message : String(e);
  } finally {
    sendBtn.disabled = false;
    sendBtn.textContent = t('test.send');
  }
}
$('#tSend').addEventListener('click', sendTestRequest);

async function readSseStream(resp, respEl) {
  const reader = resp.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    respEl.textContent = buffer;
    respEl.scrollTop = respEl.scrollHeight;
  }
  const tail = decoder.decode();
  if (tail) { buffer += tail; respEl.textContent = buffer; }
}

function buildJsScript() {
  if (!testService) return '';
  const s = testService;
  const method = $('#tMethod').value;
  const path = testCleanPath();
  const url = `http://${testHost()}/${s.pathPrefix}${path}`;
  const clientFormat = $('#tClientFormat').value;
  const bodyText = $('#tBody').value.trim();
  const isGet = method === 'GET';
  const isStream = !isGet && STREAM_RE.test(bodyText);

  let bodyLiteral = 'null';
  if (!isGet && bodyText) {
    try { bodyLiteral = JSON.stringify(JSON.parse(bodyText), null, 2); }
    catch { bodyLiteral = bodyText; }
  }

  const headerLines = ["    'Content-Type': 'application/json'"];
  if (clientFormat === 'Anthropic') {
    headerLines.push("    'x-api-key': '" + PROXY_KEY_PLACEHOLDER + "'");
    headerLines.push("    'anthropic-version': '2023-06-01'");
  } else {
    headerLines.push("    'Authorization': 'Bearer " + PROXY_KEY_PLACEHOLDER + "'");
  }

  const zh = currentLang === 'zh';
  const titleSuffix = isStream ? (zh ? ' · 流式' : ' · streaming') : '';
  const header = zh
    ? `// 调用 AI 代理服务 "${s.name}"（${clientFormat} 客户端格式${titleSuffix}）\n// 将 ${PROXY_KEY_PLACEHOLDER} 替换为代理全局密钥（启用鉴权时必填；未启用鉴权可删除该头）`
    : `// Call AI proxy service "${s.name}" (${clientFormat} client format${titleSuffix})\n// Replace ${PROXY_KEY_PLACEHOLDER} with the proxy global key (required when auth enabled; remove the header when auth disabled)`;

  const payloadPart = isGet ? '' : `const payload = ${bodyLiteral};\n\n`;
  const bodyPart = isGet ? '' : ',\n  body: JSON.stringify(payload)';
  const fetchBlock = `const resp = await fetch('${url}', {\n  method: '${method}',\n  headers: {\n${headerLines.join(',\n')}\n  }${bodyPart}\n});`;
  const errBlock = zh
    ? `if (!resp.ok) {\n  console.error('HTTP', resp.status, await resp.text());\n  throw new Error('请求失败: ' + resp.status);\n}`
    : `if (!resp.ok) {\n  console.error('HTTP', resp.status, await resp.text());\n  throw new Error('Request failed: ' + resp.status);\n}`;

  let consumeBlock;
  if (isStream) {
    consumeBlock = zh
      ? `// 解析 SSE 流：逐行读取 "data: <json>" 分片\nconst reader = resp.body.getReader();\nconst decoder = new TextDecoder();\nlet buffer = '';\nwhile (true) {\n  const { done, value } = await reader.read();\n  if (done) break;\n  buffer += decoder.decode(value, { stream: true });\n  const lines = buffer.split('\\n');\n  buffer = lines.pop(); // 保留最后不完整的一行\n  for (const line of lines) {\n    if (!line.startsWith('data: ')) continue;\n    const data = line.slice(6);\n    if (data === '[DONE]') { console.log('[DONE]'); break; }\n    try { console.log(JSON.parse(data)); } catch { console.log(data); }\n  }\n}`
      : `// Parse SSE stream: read "data: <json>" chunks line by line\nconst reader = resp.body.getReader();\nconst decoder = new TextDecoder();\nlet buffer = '';\nwhile (true) {\n  const { done, value } = await reader.read();\n  if (done) break;\n  buffer += decoder.decode(value, { stream: true });\n  const lines = buffer.split('\\n');\n  buffer = lines.pop(); // keep the last incomplete line\n  for (const line of lines) {\n    if (!line.startsWith('data: ')) continue;\n    const data = line.slice(6);\n    if (data === '[DONE]') { console.log('[DONE]'); break; }\n    try { console.log(JSON.parse(data)); } catch { console.log(data); }\n  }\n}`;
  } else {
    consumeBlock = 'const data = await resp.json();\nconsole.log(data);';
  }

  return `${header}\n${payloadPart}${fetchBlock}\n\n${errBlock}\n${consumeBlock}`;
}

async function copyJsScript() {
  const script = buildJsScript();
  if (!script) return;
  // 复制时记录模型
  const body = $('#tBody').value;
  const model = extractModelFromBody(body);
  if (model) addTestModelToHistory(model);
  try {
    await navigator.clipboard.writeText(script);
    toast(t('test.copied'));
    if (model) renderTestModelList();
  } catch {
    const ta = document.createElement('textarea');
    ta.value = script;
    ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); toast(t('test.copied')); if (model) renderTestModelSelect(); }
    catch { toast(t('test.copyFailed'), true); }
    document.body.removeChild(ta);
  }
}
$('#tCopyScript').addEventListener('click', copyJsScript);

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

// ─── Startup ────────────────────────────────────────────────────────────────
loadOverview();
loadConfigManagement();
