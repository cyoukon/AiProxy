# 日志详情结构化展示 Spec

## Why
当前日志详情界面（`renderBodyPanel`）仅以 `tryPrettyJson` 美化后的 JSON 文本展示请求/响应体，长消息数组与嵌套结构难以快速阅读。对于 AI 代理这类以聊天负载为主的场景，缺少对 `messages`、`choices`、`usage`、`tools` 等关键字段的语义化呈现，排查与重放效率低。

## What Changes
- 在日志详情 Modal 标题栏新增「结构化显示」开关，控制全部请求/响应体面板的渲染模式。
- 开关值持久化到 `localStorage`（key：`aiproxy_admin_structured_view`），默认开启。
- 开关切换时基于已缓存的当前日志数据即时重渲染，不重新请求后端。
- 开关关闭时：保持现有行为（`pre.body` + 美化 JSON）。
- 开关开启时：采用**混合视图**渲染——
  - 请求/响应体先尝试 `JSON.parse`；失败（如流式聚合文本、HTML 错误页）则回退为纯文本 `<pre>`。
  - 解析成功后按「格式(OpenAI/Anthropic) + 方向(请求/响应)」分发到语义化渲染器，提取 `model`、`messages`/`system`、`tools`、`choices`/`content`、`usage`、`stop_reason`/`finish_reason` 等字段做友好展示。
  - 语义化渲染器未消费的剩余字段统一交由可折叠 JSON 树兜底展示。
  - 无法识别结构的 JSON 整体以可折叠 JSON 树展示。
- 新增可复用的可折叠 JSON 树组件（展开/折叠、语法高亮、深层级懒折叠）。
- 新增对应 CSS 与 i18n 文案（中/英）。

## Impact
- Affected specs: 无既有 spec（本项目首次引入 spec-driven 流程）。
- Affected code（纯前端，无后端/数据模型变更）：
  - `AiProxy/Admin/wwwroot/index.html` — Modal 标题栏新增开关 UI。
  - `AiProxy/Admin/wwwroot/app.js` — `renderBodyPanel` 改造、新增结构化渲染器与 JSON 树、开关状态与重渲染逻辑。
  - `AiProxy/Admin/wwwroot/style.css` — 结构化视图、JSON 树、消息卡片、用量栅格、开关样式。
  - `AiProxy/Admin/wwwroot/i18n.js` — 新增中英文案 key。
- 数据来源不变：`RequestLog` 的 `ClientRequestBody`/`ClientResponseBody`/`DownstreamRequestBody`/`DownstreamResponseBody` 字段已含完整内容；流式响应体为 `SseAggregator` 聚合后的纯文本（非 SSE 原始分片），结构化视图对其走纯文本回退。

## ADDED Requirements

### Requirement: 结构化显示开关
日志详情 Modal SHALL 在标题栏提供一个「结构化显示」开关，其状态持久化于 `localStorage`（`aiproxy_admin_structured_view`），默认开启。

#### Scenario: 首次打开日志详情
- **WHEN** 用户从未设置过该开关并打开任意日志详情
- **THEN** 开关处于「开启」状态，请求/响应体以混合结构化视图展示

#### Scenario: 切换开关并刷新页面
- **WHEN** 用户将开关切换为「关闭」后刷新浏览器
- **THEN** 重新打开日志详情时开关仍为「关闭」，请求/响应体以美化 JSON 文本展示

#### Scenario: 切换开关即时生效
- **WHEN** 用户在已打开的日志详情中切换开关
- **THEN** 当前已展示的全部请求/响应体面板立即按新模式重渲染，且不向后端发起请求

### Requirement: 混合结构化渲染
开关开启时，每个请求/响应体面板 SHALL 按混合策略渲染：JSON 解析失败→纯文本；解析成功→按格式+方向分发语义化渲染，剩余字段以可折叠 JSON 树兜底。

#### Scenario: OpenAI 请求体
- **WHEN** 面板内容为 OpenAI Chat 请求 JSON（含 `model`、`messages`）
- **THEN** 结构化视图展示 model、messages 列表（角色徽标 + 内容，支持字符串与 content parts 数组）、tools 列表、关键参数（stream/temperature/max_tokens 等），其余字段折叠于「其他字段」

#### Scenario: OpenAI 非流式响应体
- **WHEN** 面板内容为 OpenAI Chat 响应 JSON（含 `choices`、`usage`）
- **THEN** 结构化视图展示 choices（index + role + content + finish_reason + tool_calls）、usage token 栅格，其余字段折叠于「其他字段」

#### Scenario: Anthropic 请求体
- **WHEN** 面板内容为 Anthropic Messages 请求 JSON（含 `model`、`messages`、可选 `system`）
- **THEN** 结构化视图展示 model、system、messages 列表（角色 + content blocks）、tools、关键参数，其余字段折叠于「其他字段」

#### Scenario: Anthropic 非流式响应体
- **WHEN** 面板内容为 Anthropic Messages 响应 JSON（含 `content`、`role`、`stop_reason`、`usage`）
- **THEN** 结构化视图展示 role + stop_reason、content blocks 列表（type + text）、usage token 栅格，其余字段折叠于「其他字段」

#### Scenario: 流式响应体
- **WHEN** 面板内容为流式响应的聚合文本（非 JSON）
- **THEN** 以纯文本 `<pre>` 展示，不强行结构化

#### Scenario: 错误响应体
- **WHEN** 面板内容为含 `error` 对象的 JSON
- **THEN** 结构化视图醒目展示 error.type / error.message，其余字段折叠

#### Scenario: 无法识别的 JSON
- **WHEN** 面板内容为合法 JSON 但不匹配任何已知聊天负载结构
- **THEN** 整体以可折叠 JSON 树展示

### Requirement: 可折叠 JSON 树组件
系统 SHALL 提供可复用的 JSON 树渲染组件，支持逐层展开/折叠、按类型语法高亮、深层级默认折叠。

#### Scenario: 折叠/展开节点
- **WHEN** 用户点击树中对象/数组节点的展开箭头
- **THEN** 该节点的子节点在展开与折叠状态间切换，不影响其他节点

#### Scenario: 深层级默认折叠
- **WHEN** JSON 树渲染嵌套深度超过 2 层的对象/数组
- **THEN** 第 3 层及以下的节点默认折叠，用户可手动展开

### Requirement: 结构化视图的国际化
所有新增的结构化视图标签（开关名、分区标题、角色名、字段名等）SHALL 同时提供中英文案，并随语言切换即时生效。

## MODIFIED Requirements

### Requirement: 请求/响应体面板渲染
`renderBodyPanel(title, body, panelId)` SHALL 扩展为 `renderBodyPanel(title, body, panelId, format, kind)`，其中 `format` 取自所属面板侧的客户端/下游格式，`kind` 为 `'request'` | `'response'`。当结构化开关关闭时，沿用 `escapeHtml(tryPrettyJson(body))` 渲染；开启时按混合策略渲染。保留全屏按钮与「未记录」空态文案不变。
