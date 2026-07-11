# Tasks

- [x] Task 1: 新增结构化开关 UI 与 localStorage 状态
  - [x] SubTask 1.1: 在 `index.html` 日志详情 Modal 标题栏（`<h3>` 旁）加入「结构化显示」开关（label + checkbox/toggle），id 如 `structuredToggle`
  - [x] SubTask 1.2: 在 `app.js` 新增 `STRUCTURED_VIEW_KEY = 'aiproxy_admin_structured_view'`、`getStructuredView()` / `setStructuredView(bool)` 读写 localStorage，默认开启（无值视为 true）
  - [x] SubTask 1.3: 初始化时同步 checkbox 状态；绑定 `change` 事件，触发当前日志详情重渲染（基于已缓存数据，不重新请求）

- [x] Task 2: 实现可折叠 JSON 树组件与样式
  - [x] SubTask 2.1: 在 `app.js` 新增 `renderJsonTree(value, depth)` 函数，递归生成节点 HTML：对象/数组带展开箭头与可折叠子节点，按类型高亮 key/string/number/boolean/null；深度 > 2 默认折叠
  - [x] SubTask 2.2: 在 `app.js` 通过事件委托（挂在 `#logDetail` 上）处理 `.json-toggle` 点击，切换父节点 `.collapsed` 类
  - [x] SubTask 2.3: 在 `style.css` 新增 `.json-tree` / `.json-node` / `.json-toggle` / `.json-key` / `.json-string` / `.json-number` / `.json-bool` / `.json-null` 等样式，与现有深色代码块风格协调

- [x] Task 3: 实现 OpenAI 语义化渲染器
  - [x] SubTask 3.1: `renderOpenAiRequest(obj)` — 展示 model、messages（角色徽标 + 字符串或 content parts 数组）、tools（name + description + 参数树）、关键参数（stream/temperature/max_tokens 等），剩余字段折叠于「其他字段」
  - [x] SubTask 3.2: `renderOpenAiResponse(obj)` — 展示 choices（index + message.role/content + finish_reason + tool_calls）、usage token 栅格，剩余字段折叠；含 `error` 时醒目展示

- [x] Task 4: 实现 Anthropic 语义化渲染器
  - [x] SubTask 4.1: `renderAnthropicRequest(obj)` — 展示 model、system（若有）、messages（角色 + content blocks）、tools、关键参数（max_tokens 等），剩余字段折叠
  - [x] SubTask 4.2: `renderAnthropicResponse(obj)` — 展示 role + stop_reason、content blocks（type + text）、usage token 栅格，剩余字段折叠；含 `error` 时醒目展示

- [x] Task 5: 改造 `renderBodyPanel` 为混合分发并接入开关
  - [x] SubTask 5.1: 将 `renderBodyPanel(title, body, panelId)` 扩展为 `renderBodyPanel(title, body, panelId, format, kind)`；调用点（`openLogDetail` 内四处）传入 `format`（客户端侧用 `r.clientFormat`，下游侧用 `r.serviceFormat`）与 `kind`（`'request'`/`'response'`）
  - [x] SubTask 5.2: 开关关闭时沿用 `escapeHtml(tryPrettyJson(body))`；空值沿用「未记录」空态
  - [x] SubTask 5.3: 开关开启时：`JSON.parse` 失败 → 纯文本 `<pre>`；成功 → 按 `format + kind` 分发到 Task 3/4 渲染器；未知结构 → 整体 `renderJsonTree`；保留全屏按钮与 panelId 结构
  - [x] SubTask 5.4: `openLogDetail` 缓存当前日志原始数据（如 `lastLogDetail`），供开关切换时直接重渲染；切换开关时若 Modal 已打开则重跑 `openLogDetail` 渲染逻辑（不重新 fetch）

- [x] Task 6: 新增 i18n 文案
  - [x] SubTask 6.1: 在 `i18n.js` 中/英两份字典新增 key：`logDetail.structured`、`logDetail.messages`、`logDetail.tools`、`logDetail.parameters`、`logDetail.usage`、`logDetail.choices`、`logDetail.content`、`logDetail.role`、`logDetail.finishReason`、`logDetail.stopReason`、`logDetail.system`、`logDetail.otherFields`、`logDetail.errorInfo` 及角色名（system/user/assistant/tool）
  - [x] SubTask 6.2: 语言切换时（`langSelect` change 与初始化）确保已打开的日志详情同步重渲染

- [x] Task 7: 验证与回归
  - [x] SubTask 7.1: 本地构建/运行管理面板，覆盖 spec 中全部 Scenario（OpenAI/Anthropic 请求与响应、流式、错误、无法识别 JSON、开关开关切换与刷新、语言切换、深层级折叠）
  - [x] SubTask 7.2: 确认开关关闭时与改造前行为一致（美化 JSON + 全屏 + 空态），无回归

# Task Dependencies
- Task 2 须先于 Task 3 / Task 4（语义化渲染器依赖 JSON 树兜底剩余字段）
- Task 3 / Task 4 可并行
- Task 5 依赖 Task 2/3/4（分发器调用各渲染器与树）
- Task 6 可与 Task 2/3/4 并行，但 Task 5 接入开关前需 i18n key 已存在以避免空文案
- Task 7 依赖 Task 1–6 全部完成
