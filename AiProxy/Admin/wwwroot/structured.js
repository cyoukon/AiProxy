// ─── AI Proxy Admin - Structured Rendering ─────────────────────────────────────
// Depends on: core.js (escapeHtml, getStructuredView, setStructuredView, lastLogDetail, fmtDate),
//             i18n.js (t()).
// Contains: JSON tree, structured body renderers, semantic renderers (OpenAI/Anthropic).

// ─── Structured View Toggle (init) ──────────────────────────────────────────
(function initStructuredToggle() {
  const cb = $('#structuredToggle');
  if (!cb) return;
  cb.checked = getStructuredView();
  cb.addEventListener('change', () => {
    setStructuredView(cb.checked);
    if (lastLogDetail && !$('#logModal').classList.contains('hidden')) renderLogPanels(lastLogDetail);
  });
})();

// ─── JSON Tree Component ────────────────────────────────────────────────────
// renderJsonTree(value, depth, key) returns HTML for a single .json-node.
// Caller wraps the top-level call in <div class="json-tree">...</div>.
function renderJsonValue(v) {
  if (v === null) return `<span class="json-null">null</span>`;
  switch (typeof v) {
    case 'string': return `<span class="json-string">"${escapeHtml(v)}"</span>`;
    case 'number': return `<span class="json-number">${escapeHtml(String(v))}</span>`;
    case 'boolean': return `<span class="json-bool">${String(v)}</span>`;
    default: return `<span class="json-string">${escapeHtml(String(v))}</span>`;
  }
}

function renderJsonTree(value, depth, key) {
  const isArr = Array.isArray(value);
  const isObj = !isArr && value !== null && typeof value === 'object';
  const keyHtml = (key != null && key !== '') ? `<span class="json-key">${escapeHtml(String(key))}</span><span class="json-colon">:</span>` : '';
  if (!isArr && !isObj) {
    return `<div class="json-node json-leaf"><div class="json-row"><span class="json-toggle"></span>${keyHtml}${renderJsonValue(value)}</div></div>`;
  }
  const count = isArr ? value.length : Object.keys(value).length;
  const typeSummary = isArr ? `[${count}]` : `{${count}}`;
  if (count === 0) {
    return `<div class="json-node json-leaf"><div class="json-row"><span class="json-toggle"></span>${keyHtml}<span class="json-type">${typeSummary}</span></div></div>`;
  }
  const collapsed = depth >= 2;
  const preview = isArr ? '[…]' : '{…}';
  let children;
  if (isArr) {
    children = value.map(v => renderJsonTree(v, depth + 1, null)).join('');
  } else {
    children = Object.keys(value).map(k => renderJsonTree(value[k], depth + 1, k)).join('');
  }
  return `<div class="json-node${collapsed ? ' json-collapsed' : ''}">
    <div class="json-row"><span class="json-toggle"></span>${keyHtml}<span class="json-type">${typeSummary}</span><span class="json-preview">${preview}</span></div>
    <div class="json-children">${children}</div>
  </div>`;
}

// Event delegation for collapsing/expanding JSON tree nodes (bound once)
(function initJsonTreeToggle() {
  const root = $('#logPanels');
  if (!root) return;
  root.addEventListener('click', (e) => {
    const tog = e.target.closest('.json-toggle');
    if (!tog) return;
    const node = tog.closest('.json-node');
    if (node) node.classList.toggle('json-collapsed');
  });
})();

function renderStructuredBody(body, format, kind) {
  let obj;
  try { obj = JSON.parse(body); } catch { return { structured: false, html: body }; }
  if (obj === null || typeof obj !== 'object') return { structured: false, html: body };
  const renderer = pickBodyRenderer(obj, format, kind);
  return { structured: true, html: renderer(obj) };
}

function pickBodyRenderer(obj, format, kind) {
  if (obj && typeof obj === 'object' && obj.error) return renderErrorBlock;
  if (format === 'OpenAI' && kind === 'request') return renderOpenAiRequest;
  if (format === 'OpenAI' && kind === 'response') return renderOpenAiResponse;
  if (format === 'Anthropic' && kind === 'request') return renderAnthropicRequest;
  if (format === 'Anthropic' && kind === 'response') return renderAnthropicResponse;
  // shape-based fallback when format unknown
  if (Array.isArray(obj.choices)) return renderOpenAiResponse;
  if (Array.isArray(obj.messages)) return (obj.system !== undefined) ? renderAnthropicRequest : renderOpenAiRequest;
  if (Array.isArray(obj.content) && obj.role) return renderAnthropicResponse;
  return (o) => `<div class="json-tree">${renderJsonTree(o, 0)}</div>`;
}

// ─── Shared helpers for semantic renderers ──────────────────────────────────
function renderOtherFields(consumed, obj) {
  const keys = Object.keys(obj).filter(k => !consumed.has(k));
  if (!keys.length) return '';
  const rest = {}; keys.forEach(k => rest[k] = obj[k]);
  return `<div class="sv-section"><div class="sv-section-title">${t('logDetail.otherFields')}</div><div class="json-tree">${renderJsonTree(rest, 0)}</div></div>`;
}

function roleLabel(role) {
  const map = { system: 'roleSystem', user: 'roleUser', assistant: 'roleAssistant', tool: 'roleTool' };
  return t('logDetail.' + (map[role] || 'roleUser'));
}

function roleClass(role) {
  return ['system', 'user', 'assistant', 'tool'].includes(role) ? 'sv-role-' + role : 'sv-role-user';
}

// Determine the display role for a message. Anthropic tool_result blocks live in
// role:"user" messages but are semantically tool responses — show them as "tool".
function effectiveRole(m) {
  if (m.role === 'tool') return 'tool';
  if (Array.isArray(m.content) && m.content.some(b => b && typeof b === 'object' && b.type === 'tool_result')) {
    return 'tool';
  }
  return m.role || 'user';
}

function paramValue(v) {
  if (v == null) return '';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

function svKvGrid(pairs) {
  return `<div class="sv-kv-grid">${pairs.map(p => `<div class="sv-kv"><span class="k">${escapeHtml(p.k)}</span><span class="v">${escapeHtml(paramValue(p.v))}</span></div>`).join('')}</div>`;
}

function svUsageGrid(pairs) {
  // usage uses .sv-usage (aliased to kv-grid styling) per the class contract
  return `<div class="sv-usage sv-kv-grid">${pairs.map(p => `<div class="sv-kv"><span class="k">${escapeHtml(p.k)}</span><span class="v">${escapeHtml(paramValue(p.v))}</span></div>`).join('')}</div>`;
}

function svSection(title, innerHtml) {
  return innerHtml ? `<div class="sv-section"><div class="sv-section-title">${title}</div>${innerHtml}</div>` : '';
}

// Render content that may be a string OR an array of content parts
// (OpenAI vision / Anthropic blocks)
function renderContentParts(content) {
  if (content == null) return '';
  if (typeof content === 'string') return `<div class="sv-msg-content">${escapeHtml(content)}</div>`;
  if (Array.isArray(content)) {
    return content.map(renderContentPart).join('');
  }
  return `<div class="sv-msg-content">${escapeHtml(String(content))}</div>`;
}

// Render a single content part by type (OpenAI & Anthropic block kinds).
function renderContentPart(part) {
  if (part == null || typeof part !== 'object') {
    return `<div class="sv-msg-content">${escapeHtml(String(part))}</div>`;
  }
  const type = part.type || 'text';
  const typeTag = `<span class="sv-part-type">${escapeHtml(type)}</span>`;
  switch (type) {
    case 'text':
      return `<div class="sv-msg-content">${typeTag}${escapeHtml(String(part.text ?? ''))}</div>`;
    case 'image_url': { // OpenAI
      const url = part.image_url?.url ?? '';
      const detail = part.image_url?.detail;
      const detailTag = detail ? ` <span class="sv-part-type">detail: ${escapeHtml(String(detail))}</span>` : '';
      return `<div class="sv-msg-content">${typeTag}<code>${escapeHtml(url.length > 80 ? url.slice(0, 80) + '…' : url)}</code>${detailTag}</div>`;
    }
    case 'input_audio': { // OpenAI
      const fmt = part.input_audio?.format ?? '';
      return `<div class="sv-msg-content">${typeTag}<span class="sv-part-type">${escapeHtml(String(fmt))}</span></div>`;
    }
    case 'image': { // Anthropic {type:image, source:{type, media_type, data|url}}
      const src = part.source ?? {};
      const srcInfo = `${escapeHtml(String(src.type ?? ''))} / ${escapeHtml(String(src.media_type ?? ''))}`;
      return `<div class="sv-msg-content">${typeTag}<span class="sv-part-type">${srcInfo}</span></div>`;
    }
    case 'tool_use': { // Anthropic message block
      const idTag = part.id ? ` <span class="sv-part-type">id: ${escapeHtml(String(part.id))}</span>` : '';
      return `<div class="sv-msg-content">${typeTag}: ${escapeHtml(String(part.name ?? ''))}${idTag}<div class="json-tree">${renderJsonTree(part.input ?? {}, 0)}</div></div>`;
    }
    case 'tool_result': { // Anthropic
      const errTag = part.is_error ? ' (error)' : '';
      const idTag = part.tool_use_id ? ` <span class="sv-part-type">tool_use_id: ${escapeHtml(String(part.tool_use_id))}</span>` : '';
      return `<div class="sv-msg-content">${typeTag}${errTag}${idTag}${renderContentParts(part.content)}</div>`;
    }
    case 'thinking': // Anthropic extended thinking
      return `<div class="sv-msg-content sv-thinking">${typeTag}${escapeHtml(String(part.thinking ?? ''))}</div>`;
    case 'redacted_thinking':
      return `<div class="sv-msg-content sv-thinking">${typeTag}<span class="sv-part-type">${escapeHtml(String(part.data ?? ''))}</span></div>`;
    case 'document': { // Anthropic PDF
      const mt = part.source?.media_type ?? '';
      return `<div class="sv-msg-content">${typeTag}<span class="sv-part-type">${escapeHtml(String(mt))}</span></div>`;
    }
    default:
      return `<div class="sv-msg-content">${typeTag}<div class="json-tree">${renderJsonTree(part, 0)}</div></div>`;
  }
}

// Parse OpenAI tool_call function.arguments (a JSON string) into a node for JSON-tree rendering.
function parseToolArgs(argsStr) {
  if (typeof argsStr === 'string' && argsStr) {
    try { return JSON.parse(argsStr); } catch { return argsStr; }
  }
  if (argsStr && typeof argsStr === 'object') return argsStr;
  return {};
}

// Render an OpenAI tool_call entry: {id, type:"function", function:{name, arguments(JSON string)}}
function renderOpenAiToolCall(tc) {
  const id = tc.id ?? '';
  const name = tc.function?.name ?? '';
  const idTag = id ? ` <span class="sv-part-type">id: ${escapeHtml(String(id))}</span>` : '';
  return `<div class="sv-msg-content"><span class="sv-part-type">tool_call</span>: ${escapeHtml(name)}${idTag}<div class="json-tree">${renderJsonTree(parseToolArgs(tc.function?.arguments), 0)}</div></div>`;
}

// Render a single conversation message (unified for OpenAI & Anthropic requests).
function renderMessage(m) {
  const effRole = effectiveRole(m);
  const parts = [`<span class="sv-msg-role ${roleClass(effRole)}">${escapeHtml(roleLabel(effRole))}</span>`];
  if (m.name) parts.push(`<span class="sv-part-type">${escapeHtml(m.name)}</span>`);
  if (m.tool_call_id) parts.push(`<span class="sv-part-type">tool_call_id: ${escapeHtml(m.tool_call_id)}</span>`);
  parts.push(renderContentParts(m.content));
  if (Array.isArray(m.tool_calls)) {
    parts.push(m.tool_calls.map(renderOpenAiToolCall).join(''));
  }
  return `<div class="sv-msg">${parts.join('')}</div>`;
}

// ─── Semantic Body Renderers ────────────────────────────────────────────────
function renderErrorBlock(obj) {
  const err = obj.error || {};
  const lines = [];
  if (err.type != null) lines.push(`<div class="sv-kv"><span class="k">type</span><span class="v">${escapeHtml(String(err.type))}</span></div>`);
  if (err.code != null) lines.push(`<div class="sv-kv"><span class="k">code</span><span class="v">${escapeHtml(String(err.code))}</span></div>`);
  if (err.param != null) lines.push(`<div class="sv-kv"><span class="k">param</span><span class="v">${escapeHtml(String(err.param))}</span></div>`);
  if (err.event_id != null) lines.push(`<div class="sv-kv"><span class="k">event_id</span><span class="v">${escapeHtml(String(err.event_id))}</span></div>`);
  if (err.message != null) lines.push(`<div class="sv-kv" style="grid-column:1/-1"><span class="k">message</span><span class="v">${escapeHtml(String(err.message))}</span></div>`);
  const inner = lines.length ? `<div class="sv-kv-grid">${lines.join('')}</div>` : '';
  // Anthropic wraps errors as {type:"error", error:{...}} — consume outer type to avoid redundancy
  const consumed = new Set(['error']);
  if (obj.type === 'error') consumed.add('type');
  return `<div class="sv-error"><div class="sv-section-title">${t('logDetail.errorInfo')}</div>${inner}</div>` + renderOtherFields(consumed, obj);
}

function renderOpenAiRequest(obj) {
  const consumed = new Set();
  const sections = [];
  if (obj.model != null) { sections.push(svSection(t('logDetail.model'), svKvGrid([{ k: 'model', v: obj.model }]))); consumed.add('model'); }
  if (Array.isArray(obj.messages)) {
    sections.push(svSection(t('logDetail.messages'), obj.messages.map(renderMessage).join('')));
    consumed.add('messages');
  }
  if (Array.isArray(obj.tools)) {
    sections.push(svSection(t('logDetail.tools'), obj.tools.map(tool => {
      const fn = (tool && tool.function) || {};
      const nameHtml = `<div class="sv-tool-name">${escapeHtml(fn.name || '')}</div>`;
      const descHtml = fn.description ? `<div class="sv-tool-desc">${escapeHtml(fn.description)}</div>` : '';
      const paramsHtml = `<div class="json-tree">${renderJsonTree(fn.parameters || {}, 0)}</div>`;
      return `<div class="sv-tool">${nameHtml}${descHtml}${paramsHtml}</div>`;
    }).join('')));
    consumed.add('tools');
  }
  const paramKeys = ['stream', 'temperature', 'max_tokens', 'max_completion_tokens', 'top_p',
    'frequency_penalty', 'presence_penalty', 'n', 'seed', 'user', 'tool_choice',
    'parallel_tool_calls', 'response_format', 'logit_bias', 'logprobs',
    'top_logprobs', 'stop', 'stream_options', 'reasoning_effort', 'store', 'service_tier'];
  const paramPairs = paramKeys.filter(k => obj[k] !== undefined).map(k => ({ k, v: obj[k] }));
  if (paramPairs.length) {
    sections.push(svSection(t('logDetail.parameters'), svKvGrid(paramPairs)));
    paramPairs.forEach(p => consumed.add(p.k));
  }
  sections.push(renderOtherFields(consumed, obj));
  return sections.filter(Boolean).join('');
}

function renderOpenAiResponse(obj) {
  if (obj.error) return renderErrorBlock(obj);
  const consumed = new Set();
  const sections = [];
  // Response meta: id / created / system_fingerprint / service_tier
  const metaPairs = [];
  if (obj.id != null) metaPairs.push({ k: 'id', v: obj.id });
  if (obj.created != null) metaPairs.push({ k: 'created', v: fmtDate(new Date(Number(obj.created) * 1000)) });
  if (obj.system_fingerprint != null) metaPairs.push({ k: 'system_fingerprint', v: obj.system_fingerprint });
  if (obj.service_tier != null) metaPairs.push({ k: 'service_tier', v: obj.service_tier });
  if (metaPairs.length) {
    sections.push(svSection(t('logDetail.responseMeta'), svKvGrid(metaPairs)));
    ['id', 'created', 'system_fingerprint', 'service_tier'].forEach(k => { if (obj[k] !== undefined) consumed.add(k); });
  }
  if (Array.isArray(obj.choices)) {
    sections.push(svSection(t('logDetail.choices'), obj.choices.map(c => {
      const headParts = [];
      if (c.index != null) headParts.push(`<span class="sv-part-type">#${escapeHtml(String(c.index))}</span>`);
      if (c.message && c.message.role) headParts.push(`<span class="sv-msg-role ${roleClass(c.message.role)}">${escapeHtml(roleLabel(c.message.role))}</span>`);
      if (c.finish_reason) headParts.push(`<span class="sv-part-type">${t('logDetail.finishReason')}: ${escapeHtml(c.finish_reason)}</span>`);
      const head = `<div class="sv-choice-head">${headParts.join('')}</div>`;
      const content = c.message ? renderContentParts(c.message.content) : '';
      const toolCalls = Array.isArray(c.message && c.message.tool_calls)
        ? svSection(t('logDetail.toolCalls'), c.message.tool_calls.map(tc => {
            const name = (tc.function && tc.function.name) || '';
            const idTag = tc.id ? `<div class="sv-tool-desc">id: ${escapeHtml(tc.id)}</div>` : '';
            return `<div class="sv-tool"><div class="sv-tool-name">${escapeHtml(name)}</div>${idTag}<div class="json-tree">${renderJsonTree(parseToolArgs(tc.function?.arguments), 0)}</div></div>`;
          }).join(''))
        : '';
      return `<div class="sv-choice">${head}${content}${toolCalls}</div>`;
    }).join('')));
    consumed.add('choices');
  }
  if (obj.usage) {
    const pairs = [];
    if (obj.usage.prompt_tokens != null) pairs.push({ k: t('logDetail.promptTokens'), v: obj.usage.prompt_tokens });
    if (obj.usage.completion_tokens != null) pairs.push({ k: t('logDetail.completionTokens'), v: obj.usage.completion_tokens });
    if (obj.usage.total_tokens != null) pairs.push({ k: t('logDetail.totalTokens'), v: obj.usage.total_tokens });
    if (obj.usage.prompt_tokens_details?.cached_tokens != null) pairs.push({ k: t('logDetail.cachedTokens'), v: obj.usage.prompt_tokens_details.cached_tokens });
    if (obj.usage.completion_tokens_details?.reasoning_tokens != null) pairs.push({ k: t('logDetail.reasoningTokens'), v: obj.usage.completion_tokens_details.reasoning_tokens });
    if (pairs.length) { sections.push(svSection(t('logDetail.usage'), svUsageGrid(pairs))); consumed.add('usage'); }
  }
  if (obj.model != null) { sections.push(svSection(t('logDetail.model'), svKvGrid([{ k: 'model', v: obj.model }]))); consumed.add('model'); }
  sections.push(renderOtherFields(consumed, obj));
  return sections.filter(Boolean).join('');
}

function renderAnthropicRequest(obj) {
  const consumed = new Set();
  const sections = [];
  if (obj.model != null) { sections.push(svSection(t('logDetail.model'), svKvGrid([{ k: 'model', v: obj.model }]))); consumed.add('model'); }
  if (obj.system != null) {
    const sysInner = (typeof obj.system === 'string') ? `<div class="sv-msg-content">${escapeHtml(obj.system)}</div>` : renderContentParts(obj.system);
    sections.push(svSection(t('logDetail.system'), sysInner));
    consumed.add('system');
  }
  if (Array.isArray(obj.messages)) {
    sections.push(svSection(t('logDetail.messages'), obj.messages.map(renderMessage).join('')));
    consumed.add('messages');
  }
  if (Array.isArray(obj.tools)) {
    sections.push(svSection(t('logDetail.tools'), obj.tools.map(tool => {
      const nameHtml = `<div class="sv-tool-name">${escapeHtml(tool.name || '')}</div>`;
      const descHtml = tool.description ? `<div class="sv-tool-desc">${escapeHtml(tool.description)}</div>` : '';
      const schemaHtml = `<div class="json-tree">${renderJsonTree(tool.input_schema || {}, 0)}</div>`;
      return `<div class="sv-tool">${nameHtml}${descHtml}${schemaHtml}</div>`;
    }).join('')));
    consumed.add('tools');
  }
  const paramKeys = ['max_tokens', 'temperature', 'top_p', 'top_k', 'stream', 'stop_sequences',
    'tool_choice', 'metadata', 'thinking'];
  const paramPairs = paramKeys.filter(k => obj[k] !== undefined).map(k => ({ k, v: obj[k] }));
  if (paramPairs.length) {
    sections.push(svSection(t('logDetail.parameters'), svKvGrid(paramPairs)));
    paramPairs.forEach(p => consumed.add(p.k));
  }
  sections.push(renderOtherFields(consumed, obj));
  return sections.filter(Boolean).join('');
}

function renderAnthropicResponse(obj) {
  if (obj.error) return renderErrorBlock(obj);
  const consumed = new Set();
  const sections = [];
  // Response meta: id / type / model
  const metaPairs = [];
  if (obj.id != null) metaPairs.push({ k: 'id', v: obj.id });
  if (obj.type != null) metaPairs.push({ k: 'type', v: obj.type });
  if (obj.model != null) metaPairs.push({ k: 'model', v: obj.model });
  if (metaPairs.length) {
    sections.push(svSection(t('logDetail.responseMeta'), svKvGrid(metaPairs)));
    ['id', 'type', 'model'].forEach(k => { if (obj[k] !== undefined) consumed.add(k); });
  }
  // head: role + stop_reason (+ stop_sequence if present)
  const headPairs = [];
  if (obj.role != null) headPairs.push({ k: t('logDetail.role'), v: obj.role });
  if (obj.stop_reason != null) headPairs.push({ k: t('logDetail.stopReason'), v: obj.stop_reason });
  if (obj.stop_sequence != null) headPairs.push({ k: 'stop_sequence', v: obj.stop_sequence });
  if (headPairs.length) {
    sections.push(`<div class="sv-choice"><div class="sv-choice-head">${headPairs.map(p => `<span class="sv-part-type">${escapeHtml(p.k)}: ${escapeHtml(String(p.v))}</span>`).join('')}</div></div>`);
    ['role', 'stop_reason', 'stop_sequence'].forEach(k => { if (obj[k] !== undefined) consumed.add(k); });
  }
  if (Array.isArray(obj.content)) {
    sections.push(svSection(t('logDetail.content'), obj.content.map(renderContentPart).join('')));
    consumed.add('content');
  }
  if (obj.usage) {
    const pairs = [];
    if (obj.usage.input_tokens != null) pairs.push({ k: t('logDetail.inputTokens'), v: obj.usage.input_tokens });
    if (obj.usage.output_tokens != null) pairs.push({ k: t('logDetail.outputTokens'), v: obj.usage.output_tokens });
    if (obj.usage.input_tokens != null && obj.usage.output_tokens != null) pairs.push({ k: t('logDetail.totalTokens'), v: Number(obj.usage.input_tokens) + Number(obj.usage.output_tokens) });
    if (obj.usage.cache_creation_input_tokens != null) pairs.push({ k: t('logDetail.cacheCreationTokens'), v: obj.usage.cache_creation_input_tokens });
    if (obj.usage.cache_read_input_tokens != null) pairs.push({ k: t('logDetail.cacheReadTokens'), v: obj.usage.cache_read_input_tokens });
    if (pairs.length) { sections.push(svSection(t('logDetail.usage'), svUsageGrid(pairs))); consumed.add('usage'); }
  }
  sections.push(renderOtherFields(consumed, obj));
  return sections.filter(Boolean).join('');
}
