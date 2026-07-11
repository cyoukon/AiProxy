// ─── AI Proxy Admin - Logs ────────────────────────────────────────────────────
// Depends on: core.js (api, fmtDate, escapeHtml, tryPrettyJson, fromLocalInput, toast,
//                getStructuredView, panelRawText, lastLogDetail, currentLogId, logsState),
//             structured.js (renderStructuredBody), i18n.js (t()).

// ─── Logs ───────────────────────────────────────────────────────────────────
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

// ─── Body Panel Rendering ───────────────────────────────────────────────────
function renderBodyPanel(title, body, panelId, format, kind) {
  // 保存原始文本供复制按钮使用（body 为空时存空串，复制按钮将禁用）
  panelRawText[panelId] = body || '';
  let bodyEl;
  if (!body) {
    bodyEl = `<pre class="body"><i>${t('logDetail.notRecorded')}</i></pre>`;
  } else if (getStructuredView()) {
    const r = renderStructuredBody(body, format, kind);
    bodyEl = r.structured ? `<div class="structured-view">${r.html}</div>` : `<pre class="body">${escapeHtml(r.html)}</pre>`;
  } else {
    bodyEl = `<pre class="body">${escapeHtml(tryPrettyJson(body))}</pre>`;
  }
  // 方向图标：请求 → ，响应 ←
  const dirIcon = `<span class="panel-dir-icon">${kind === 'request' ? '→' : '←'}</span>`;
  const copyDisabled = !body ? 'disabled' : '';
  return `<div class="body-panel" id="${panelId}" data-kind="${kind}">
    <div class="body-panel-header">
      <h4>${dirIcon}${title}</h4>
      <div class="panel-actions">
        <button class="btn btn-secondary btn-sm copy-btn" data-panel="${panelId}" title="${t('logDetail.copy')}" ${copyDisabled}>⧉</button>
        <button class="btn btn-secondary btn-sm fullscreen-btn" data-panel="${panelId}" title="全屏">⛶</button>
      </div>
    </div>
    ${bodyEl}
  </div>`;
}

// 复制 body-panel 原始文本：结构化开启时复制原始 body，关闭时复制 tryPrettyJson(body)
async function copyPanelRawText(panelId) {
  const body = panelRawText[panelId];
  if (!body) return;
  const raw = getStructuredView() ? body : tryPrettyJson(body);
  try {
    await navigator.clipboard.writeText(raw);
    toast(t('logDetail.copied'));
  } catch {
    // 回退到 execCommand
    const ta = document.createElement('textarea');
    ta.value = raw;
    ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); toast(t('logDetail.copied')); }
    catch { toast(t('test.copyFailed'), true); }
    document.body.removeChild(ta);
  }
}

// ─── Log Detail Render & Open ───────────────────────────────────────────────
function renderLogDetail(r) {
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
    </div>`;

  renderLogPanels(r);
  $('#logModal').classList.remove('hidden');
}

// Renders only the dual body panels. Called on initial open, language change,
// and structured-toggle change (so the toggle itself is never destroyed/rebound).
function renderLogPanels(r) {
  const panels = $('#logPanels');
  panels.innerHTML = `
    <div class="dual-panel">
      <div class="panel-col">
        <div class="panel-title">${t('logDetail.clientSide')} <small>(${escapeHtml(r.clientFormat || '-')})</small></div>
        <div class="panel-path"><code>${escapeHtml(r.clientPath)}</code></div>
        ${renderBodyPanel(t('logDetail.requestBody'), r.clientRequestBody, 'panelClientReq', r.clientFormat, 'request')}
        ${renderBodyPanel(t('logDetail.responseBody'), r.clientResponseBody, 'panelClientResp', r.clientFormat, 'response')}
      </div>
      <div class="panel-col">
        <div class="panel-title">${t('logDetail.downstreamSide')} <small>(${escapeHtml(r.serviceFormat || '-')})</small></div>
        <div class="panel-path"><code>${escapeHtml(r.downstreamUrl)}</code></div>
        ${renderBodyPanel(t('logDetail.requestBody'), r.downstreamRequestBody, 'panelDownReq', r.serviceFormat, 'request')}
        ${renderBodyPanel(t('logDetail.responseBody'), r.downstreamResponseBody, 'panelDownResp', r.serviceFormat, 'response')}
      </div>
    </div>`;

  // Fullscreen toggle for body panels
  panels.querySelectorAll('.fullscreen-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const panel = panels.querySelector('#' + btn.dataset.panel);
      if (panel) panel.classList.toggle('fullscreen');
    });
  });
  // Copy raw text for body panels
  panels.querySelectorAll('.copy-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      if (btn.disabled) return;
      copyPanelRawText(btn.dataset.panel);
    });
  });
}

async function openLogDetail(id) {
  const resp = await api('/api/logs/' + id);
  if (!resp.ok) { toast(t('logDetail.loadFailed') + ' (' + resp.status + ')'); return; }
  const r = resp.body || {};
  currentLogId = id;
  lastLogDetail = r;
  $('#structuredToggle').checked = getStructuredView();
  $('#replayStatus').textContent = '';
  $('#replayStatus').style.color = '';
  $('#replayResult').style.display = 'none';
  $('#replayResult').innerHTML = '';
  renderLogDetail(r);
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
