// ─── AI Proxy Admin - Service Test ─────────────────────────────────────────────
// Depends on: core.js (api, escapeHtml, toast, t(), tryPrettyJson, getApiKey,
//                currentConfig, currentLang),
//             services.js (displayHost), i18n.js (t(), currentLang).

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
    try { document.execCommand('copy'); toast(t('test.copied')); if (model) renderTestModelList(); }
    catch { toast(t('test.copyFailed'), true); }
    document.body.removeChild(ta);
  }
}
$('#tCopyScript').addEventListener('click', copyJsScript);
