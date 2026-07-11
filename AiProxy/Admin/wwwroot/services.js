// ─── AI Proxy Admin - Services & Config ────────────────────────────────────────
// Depends on: core.js (api, escapeHtml, toast, t(), currentConfig, lastServices),
//             logs.js (loadOverview), i18n.js (t()).

// ─── Service Overview ───────────────────────────────────────────────────────
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

// ─── Config Management ──────────────────────────────────────────────────────
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
let serviceMappings = []; // 当前编辑的模型映射列表（数组顺序即配置顺序）

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
  // 清空模型映射状态与测试输入
  serviceMappings = [];
  $('#mappingTestInput').value = '';

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
    // 回填模型映射（数组顺序即配置顺序）
    serviceMappings = (s.modelMappings || []).map(m => ({
      pattern: m.pattern || '',
      replacement: m.replacement || '',
      enabled: m.enabled !== false
    }));
  } else {
    $('#serviceModalTitle').textContent = t('service.addTitle');
  }
  renderMappingsList();
  runMappingTest();
  $('#serviceModal').classList.remove('hidden');
}

$('#addServiceBtn').addEventListener('click', () => openServiceModal('add', null));
$('#serviceModalClose').addEventListener('click', () => $('#serviceModal').classList.add('hidden'));
$('#serviceCancel').addEventListener('click', () => $('#serviceModal').classList.add('hidden'));

// ─── Service Model Mappings (edit list + regex test) ────────────────────────
// 渲染模型映射行列表（数组顺序即配置顺序）
function renderMappingsList() {
  const list = $('#mappingsList');
  if (!list) return;
  if (serviceMappings.length === 0) { list.innerHTML = ''; return; }
  list.innerHTML = serviceMappings.map((m, idx) => `
    <div class="mapping-row" data-idx="${idx}">
      <input type="text" class="m-pattern" value="${escapeHtml(m.pattern)}" placeholder="${escapeHtml(t('service.mappingsPatternPlaceholder'))}" data-i18n-placeholder="service.mappingsPatternPlaceholder">
      <span class="mapping-arrow">→</span>
      <input type="text" class="m-replacement" value="${escapeHtml(m.replacement)}" placeholder="${escapeHtml(t('service.mappingsReplacementPlaceholder'))}" data-i18n-placeholder="service.mappingsReplacementPlaceholder">
      <label class="check"><input type="checkbox" class="m-enabled" ${m.enabled ? 'checked' : ''}> <span data-i18n="service.mappingsEnabled">${escapeHtml(t('service.mappingsEnabled'))}</span></label>
      <button class="btn btn-secondary btn-sm" data-act="up" data-i18n-title="service.mappingsMoveUp" title="${escapeHtml(t('service.mappingsMoveUp'))}">↑</button>
      <button class="btn btn-secondary btn-sm" data-act="down" data-i18n-title="service.mappingsMoveDown" title="${escapeHtml(t('service.mappingsMoveDown'))}">↓</button>
      <button class="btn btn-danger btn-sm" data-act="del" data-i18n-title="service.mappingsDelete" title="${escapeHtml(t('service.mappingsDelete'))}">✕</button>
    </div>`).join('');
}

// 正则匹配测试：按顺序首次匹配替换，与后端转发逻辑一致
function runMappingTest() {
  const input = $('#mappingTestInput');
  const result = $('#mappingTestResult');
  if (!input || !result) return;
  const model = input.value;
  // 清除之前的高亮
  $$('#mappingsList .mapping-row').forEach(r => r.classList.remove('mapping-match'));
  if (!model) { result.textContent = ''; result.style.color = ''; return; }
  for (let i = 0; i < serviceMappings.length; i++) {
    const m = serviceMappings[i];
    if (!m.enabled) continue;
    let regex;
    try {
      // test 用不带 g 的新实例，避免 lastIndex 陷阱
      regex = new RegExp(m.pattern);
    } catch {
      result.textContent = t('service.mappingsInvalidRegex');
      result.style.color = 'var(--failure)';
      return;
    }
    if (regex.test(model)) {
      // replace 用带 g 的实例，全局替换
      const replaced = model.replace(new RegExp(m.pattern, 'g'), m.replacement);
      result.textContent = t('service.mappingsResult') + ': ' + replaced;
      result.style.color = 'var(--success)';
      const row = $('#mappingsList .mapping-row[data-idx="' + i + '"]');
      if (row) row.classList.add('mapping-match');
      return;
    }
  }
  // 无命中
  result.textContent = t('service.mappingsNoMatch');
  result.style.color = 'var(--muted)';
}

// 映射行 input：回写 pattern/replacement 到 serviceMappings（不 re-render，保留焦点）
$('#mappingsList').addEventListener('input', (e) => {
  const row = e.target.closest('.mapping-row');
  if (!row) return;
  const idx = parseInt(row.dataset.idx, 10);
  if (isNaN(idx) || !serviceMappings[idx]) return;
  if (e.target.classList.contains('m-pattern')) serviceMappings[idx].pattern = e.target.value;
  else if (e.target.classList.contains('m-replacement')) serviceMappings[idx].replacement = e.target.value;
  else return;
  runMappingTest();
});

// 映射行 change：回写 enabled
$('#mappingsList').addEventListener('change', (e) => {
  const row = e.target.closest('.mapping-row');
  if (!row) return;
  const idx = parseInt(row.dataset.idx, 10);
  if (isNaN(idx) || !serviceMappings[idx]) return;
  if (e.target.classList.contains('m-enabled')) {
    serviceMappings[idx].enabled = e.target.checked;
    runMappingTest();
  }
});

// 映射行 click：上移/下移/删除（操作后 re-render + 刷新测试）
$('#mappingsList').addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-act]');
  if (!btn) return;
  const row = btn.closest('.mapping-row');
  if (!row) return;
  const idx = parseInt(row.dataset.idx, 10);
  if (isNaN(idx) || !serviceMappings[idx]) return;
  const act = btn.dataset.act;
  if (act === 'up' && idx > 0) {
    [serviceMappings[idx - 1], serviceMappings[idx]] = [serviceMappings[idx], serviceMappings[idx - 1]];
  } else if (act === 'down' && idx < serviceMappings.length - 1) {
    [serviceMappings[idx + 1], serviceMappings[idx]] = [serviceMappings[idx], serviceMappings[idx + 1]];
  } else if (act === 'del') {
    serviceMappings.splice(idx, 1);
  } else return;
  renderMappingsList();
  runMappingTest();
});

// 新增映射
$('#addMappingBtn').addEventListener('click', () => {
  serviceMappings.push({ pattern: '', replacement: '', enabled: true });
  renderMappingsList();
  runMappingTest();
});

// 匹配测试输入
$('#mappingTestInput').addEventListener('input', runMappingTest);

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
    allowInvalidSslCertificates: $('#fAllowInvalidSsl').checked,
    // 模型映射：始终整体覆盖发送（空数组=清除映射，与后端「非 null=整体覆盖」约定一致）
    modelMappings: serviceMappings
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
