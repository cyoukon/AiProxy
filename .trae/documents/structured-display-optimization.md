# 结构化显示优化计划

## 摘要

针对 Anthropic / OpenAI 双格式 API，全面补全结构化数据显示的解析与渲染逻辑，并解决流式响应无法结构化展示的根本问题。优化分两阶段：前端渲染补全（参数/usage/元信息/content parts/错误）+ 后端转换器映射修正；流式响应结构化重建（SseAggregator 输出重构 JSON 替代纯文本）。**无需兼容旧数据**，可直接变更存储格式。

---

## 现状分析

### 已确认的关键事实

1. **存储流程**（[RequestLoggingMiddleware.cs:168-201](file:///e:/repos/AiProxy/AiProxy/Middleware/RequestLoggingMiddleware.cs#L168-L201)）：
   - 非流式：原始 JSON 字符串直接存入 `ClientResponseBody`/`DownstreamResponseBody`
   - 流式：`SseAggregator.Aggregate` 返回纯文本 `aggregated`，存入响应体字段；token 用量由聚合器独立返回，存入 `PromptTokens`/`CompletionTokens`/`TotalTokens`
   - token 提取与响应体字符串**相互独立**，改存储格式不影响 token 解析

2. **SseAggregator**（[SseAggregator.cs](file:///e:/repos/AiProxy/AiProxy/Forwarding/SseAggregator.cs)）：
   - 仅两个调用方：`RequestLoggingMiddleware`（L172/L194）、`ReplayService`（L122）
   - 已按 `type` 字段（Anthropic）/ `choices` 字段（OpenAI）分发，仅累积 `delta.text`/`delta.content`，**丢弃** tool_calls、finish_reason、block 边界、usage 结构
   - 输出 `AggregatedContent` 是纯文本，前端 `JSON.parse` 必失败 → 流式响应永远降级为 `<pre>` 纯文本

3. **前端渲染器**（[app.js:370-588](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L370-L588)）字段覆盖缺口：
   - OpenAI 请求 `paramKeys` 缺 `tool_choice`/`response_format`/`max_completion_tokens`/`reasoning_effort`/`store`/`stream_options`/`service_tier`/`parallel_tool_calls`/`logprobs`/`top_logprobs`/`stop`/`logit_bias`/`metadata`
   - Anthropic 请求 `paramKeys` 缺 `tool_choice`/`metadata`/`thinking`
   - OpenAI 响应未展示 `id`/`created`/`system_fingerprint`；usage 缺 `prompt_tokens_details.cached_tokens`/`completion_tokens_details.reasoning_tokens`
   - Anthropic 响应未展示 `id`/`type`/`model`；usage 缺 `cache_creation_input_tokens`/`cache_read_input_tokens`
   - `renderContentParts` 未语义化 `image_url`/`input_audio`/`tool_use`/`tool_result`/`thinking`/`image`/`document`
   - `renderErrorBlock` 缺 `error.param`/`error.event_id`；Anthropic 外层 `type:"error"` 冗余落入 otherFields

4. **转换器映射缺口**：
   - `AnthropicToOpenAiResponseConverter`（[L137-144](file:///e:/repos/AiProxy/AiProxy/Forwarding/Converters/AnthropicToOpenAiResponseConverter.cs#L137-L144)）：`pause_turn`/`refusal` 未映射，默认 `stop`
   - `OpenAiToAnthropicResponseConverter`（[L148-156](file:///e:/repos/AiProxy/AiProxy/Forwarding/Converters/OpenAiToAnthropicResponseConverter.cs#L148-L156)）：`content_filter → end_turn`，应改 `refusal`

### 设计决策

- **流式存储格式变更**：让 `SseAggregator.Aggregate` 重构完整响应 JSON 作为 `AggregatedContent`（替代纯文本）。统一流式/非流式存储为「完整 JSON 响应体」。理由：用户偏好简洁统一逻辑；无需兼容旧数据；重构 JSON 比纯文本片段信息更完整（含 tool_calls/usage/finish_reason）。纯文本视图将展示 pretty JSON（改进而非退化）。
- **token 返回值不变**：聚合器仍返回 `(int? Prompt, int? Completion, int? Total)`，避免改动两个调用方的 token 处理逻辑。
- **重建逻辑独立于流式转换器**：流式转换器做跨格式转换（OpenAI chunk→Anthropic event），重建做同格式还原（OpenAI chunk→OpenAI response），逻辑不同，独立实现更清晰。

---

## 拟定变更

### 阶段一：前端渲染补全 + 转换器修正（低风险）

#### 1.1 扩展 i18n 键（[i18n.js](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/i18n.js)）

zh 段（L73-119 后）与 en 段（L343-389 后）各新增：
- `logDetail.responseMeta` → 响应元信息 / Response Meta
- `logDetail.cachedTokens` → 缓存 Tokens / Cached Tokens
- `logDetail.reasoningTokens` → 推理 Tokens / Reasoning Tokens
- `logDetail.cacheCreationTokens` → 缓存写入 Tokens / Cache Creation Tokens
- `logDetail.cacheReadTokens` → 缓存读取 Tokens / Cache Read Tokens

#### 1.2 增强参数值渲染（[app.js](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js)）

新增 `paramValue(v)` 辅助函数：对象/数组返回 `JSON.stringify`，避免 `String([object])`。`svKvGrid` 的 `v` 渲染统一走 `paramValue`（对纯标量无副作用）。

#### 1.3 补全 OpenAI 请求参数（[app.js:475](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L475)）

`renderOpenAiRequest` 的 `paramKeys` 扩展为：
```javascript
['stream', 'temperature', 'max_tokens', 'max_completion_tokens', 'top_p',
 'frequency_penalty', 'presence_penalty', 'n', 'seed', 'user', 'tool_choice',
 'parallel_tool_calls', 'response_format', 'logit_bias', 'logprobs',
 'top_logprobs', 'stop', 'stream_options', 'reasoning_effort', 'store',
 'service_tier']
```

#### 1.4 补全 Anthropic 请求参数（[app.js:545](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L545)）

`renderAnthropicRequest` 的 `paramKeys` 扩展为：
```javascript
['max_tokens', 'temperature', 'top_p', 'top_k', 'stream', 'stop_sequences',
 'tool_choice', 'metadata', 'thinking']
```

#### 1.5 补全 OpenAI 响应渲染（[app.js:485-517](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L485-L517)）

- 在 choices 段前插入响应元信息段：`id`、`created`（格式化为时间）、`system_fingerprint`、`service_tier`（如有）
- usage 补 `prompt_tokens_details.cached_tokens`、`completion_tokens_details.reasoning_tokens`
- 将上述字段加入 `consumed` 集合

#### 1.6 补全 Anthropic 响应渲染（[app.js:555-588](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L555-L588)）

- 在 head 段前插入响应元信息段：`id`、`type`、`model`
- usage 补 `cache_creation_input_tokens`、`cache_read_input_tokens`
- content block 渲染：`thinking`/`redacted_thinking` 给予语义化展示（type 标签 + thinking 文本），统一走 `renderContentPart`

#### 1.7 重构 `renderContentParts`（[app.js:423-438](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L423-L438)）

拆分为 `renderContentParts(content)`（数组遍历入口）+ `renderContentPart(part)`（单 block 分发），按 type 语义化渲染：
- `text` → type 标签 + 文本
- `image_url`（OpenAI）→ type 标签 + url 截断 + detail
- `input_audio`（OpenAI）→ type 标签 + format
- `image`（Anthropic）→ type 标签 + source.type/media_type
- `tool_use`（Anthropic message block）→ type 标签 + name + input JSON 树
- `tool_result`（Anthropic）→ type 标签 + is_error 标记 + 递归 content
- `thinking`（Anthropic）→ type 标签 + thinking 文本（`.sv-thinking` 样式）
- `redacted_thinking` → type 标签 + data
- `document`（Anthropic PDF）→ type 标签 + media_type
- 默认 → 通用 JSON 树

`renderAnthropicResponse` 的 content block 渲染复用 `renderContentPart`（消除现有 `tool_use` 特判重复）。

#### 1.8 补全错误渲染（[app.js:441-449](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L441-L449)）

- `renderErrorBlock` 增补 `error.param`、`error.event_id` 展示
- Anthropic 外层 `type:"error"` 加入 `consumed`，避免冗余落入 otherFields

#### 1.9 转换器 stop_reason 映射修正

- [AnthropicToOpenAiResponseConverter.cs:137-144](file:///e:/repos/AiProxy/AiProxy/Forwarding/Converters/AnthropicToOpenAiResponseConverter.cs#L137-L144)：新增 `pause_turn → stop`、`refusal → content_filter`
- [OpenAiToAnthropicResponseConverter.cs:148-156](file:///e:/repos/AiProxy/AiProxy/Forwarding/Converters/OpenAiToAnthropicResponseConverter.cs#L148-L156)：`content_filter → refusal`

---

### 阶段二：流式响应结构化重建（核心）

#### 2.1 重构 `SseAggregator.Aggregate`（[SseAggregator.cs](file:///e:/repos/AiProxy/AiProxy/Forwarding/SseAggregator.cs)）

**目标**：返回值 `AggregatedContent` 由「纯文本」改为「重构的完整响应 JSON 字符串」。签名不变，仍返回 `(string AggregatedContent, int? PromptTokens, int? CompletionTokens, int? TotalTokens)`。

**实现**：维护重建状态，按格式分发累积：

**OpenAI 重建**（无 `type` 字段、有 `choices`）：
- 累积状态：`id`、`model`、`StringBuilder content`、`List<ToolCallAccum> toolCalls`（按 index 聚合 `function.name`+`arguments` 片段）、`finishReason`、`usage`
- 每个 chunk：取 `choices[0].delta`，累积 `content`/`tool_calls` fragments；取 `finish_reason`（末 chunk）；取 `usage`（末 chunk，如 `stream_options.include_usage`）
- 流结束：组装 `{id, object:"chat.completion", created:<now>, model, choices:[{index:0, message:{role:"assistant", content, tool_calls?}, finish_reason}], usage?}` → `ToJsonString()`

**Anthropic 重建**（有 `type` 字段）：
- 累积状态：`id`、`model`、`List<ContentBlockAccum> blocks`（按 index 聚合：text 块累积 `text_delta`；tool_use 块累积 `input_json_delta` + 记录 id/name）、`stopReason`、`stopSequence`、`inputTokens`、`outputTokens`
- 事件分发：
  - `message_start`：取 `message.id`/`message.model`/`message.usage.input_tokens`
  - `content_block_start`：按 `content_block.type` 创建新块（text/tool_use），记录 index
  - `content_block_delta`：按 `index` 累积（`text_delta`→文本，`input_json_delta`→工具参数片段）
  - `content_block_stop`：标记块完成
  - `message_delta`：取 `delta.stop_reason`/`delta.stop_sequence`/`usage.output_tokens`
  - `message_stop`：结束
- 流结束：组装 `{id, type:"message", role:"assistant", model, content:[...blocks], stop_reason, stop_sequence, usage:{input_tokens, output_tokens}}` → `ToJsonString()`

**fail-open**：任何解析异常或无可用数据时，回退当前行为（返回纯文本 fallback 或空串），保证不阻断主链路。

**降级处理**：若重建出的 JSON 为空（如非 OpenAI/Anthropic 标准流），保留现有 `fallbackBuilder` 逻辑输出原始 data 负载拼接文本。

#### 2.2 验证调用方无需改动

- [RequestLoggingMiddleware.cs:172-176](file:///e:/repos/AiProxy/AiProxy/Middleware/RequestLoggingMiddleware.cs#L172-L176)：`aggregated` 直接存入 `clientResponseBody`，token 取返回值 — **无需改动**，存储内容自然变为 JSON
- [RequestLoggingMiddleware.cs:194-195](file:///e:/repos/AiProxy/AiProxy/Middleware/RequestLoggingMiddleware.cs#L194-L195)：下游侧同理 — **无需改动**
- [ReplayService.cs:122-126](file:///e:/repos/AiProxy/AiProxy/Forwarding/ReplayService.cs#L122-L126)：`responseBodyText` 存 JSON，token 取返回值 — **无需改动**

#### 2.3 前端无需改动

- `renderStructuredBody`（[app.js:370-376](file:///e:/repos/AiProxy/AiProxy/Admin/wwwroot/app.js#L370-L376)）对重构 JSON `JSON.parse` 成功 → 走语义渲染器
- 纯文本视图 `tryPrettyJson` 对 JSON 做 pretty-print（改进：展示完整响应而非片段）

---

## 假设与决策

1. **无需数据迁移**：用户明确要求不兼容旧数据。旧流式日志仍为纯文本，前端 `renderStructuredBody` 解析失败自然降级为 `<pre>` 展示，行为一致。
2. **token 提取保持独立**：聚合器返回值不变，两个调用方的 token 处理逻辑零改动。
3. **重建范围**：仅重建客户端格式响应（聚合器输入已是转换后的客户端格式 SSE）。下游侧同理独立重建。
4. **不处理的历史数据**：旧日志的流式响应体为纯文本，结构化视图自动降级，不报错。
5. **`created` 时间戳**：OpenAI 重建时用 `DateTimeOffset.UtcNow.ToUnixTimeSeconds()`（与 `AnthropicToOpenAiResponseConverter` 一致）。

---

## 验证步骤

### 阶段一验证
1. `dotnet build` 无错误无警告
2. `node --check AiProxy/Admin/wwwroot/app.js` 通过
3. `node --check AiProxy/Admin/wwwroot/i18n.js` 通过
4. 启动服务，发起含 `tool_choice`/`response_format`/`thinking` 的请求，确认参数区完整展示
5. 发起含 prompt caching 的 Anthropic 请求，确认 usage 展示 cache 字段
6. 发起含多模态/工具调用的请求，确认 content parts 语义化渲染
7. 触发 4xx 错误，确认 `error.param` 展示

### 阶段二验证
1. 发起 OpenAI 流式请求（含工具调用 + `stream_options.include_usage`），查看日志详情：
   - 结构化视图正确渲染 choices/message/tool_calls/usage/finish_reason
   - 纯文本视图展示 pretty JSON
2. 发起 Anthropic 流式请求（含 tool_use block），查看日志详情：
   - 结构化视图正确渲染 content blocks/stop_reason/usage
3. 发起格式转换流式请求（OpenAI→Anthropic、Anthropic→OpenAI），确认两侧响应体均正确重建
4. 确认 token 用量正确填充（与请求前推断一致）
5. 重放（Replay）流式请求，确认重放结果展示正确
6. 非 OpenAI/Anthropic 标准流（fallback）仍正常降级展示

### 实施顺序
阶段一 → 阶段二。每阶段独立可验证。建议阶段一完成后先验证，再进入阶段二。
