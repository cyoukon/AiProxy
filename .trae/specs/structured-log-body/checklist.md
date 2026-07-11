# Checklist

- [x] 日志详情 Modal 标题栏存在「结构化显示」开关，且其状态读写自 `localStorage`（`aiproxy_admin_structured_view`），首次使用默认开启
- [x] 切换开关时不向后端发起新请求，已打开的日志详情面板即时按新模式重渲染
- [x] 切换开关后刷新浏览器，开关状态与对应渲染模式保持一致
- [x] 开关关闭时，请求/响应体展示与改造前完全一致（`pre.body` + 美化 JSON + 全屏按钮 + 「未记录」空态）
- [x] 开关开启且为 OpenAI 请求体时，正确展示 model / messages（角色徽标 + 字符串与 content parts 数组均支持）/ tools / 关键参数 / 其他字段折叠
- [x] 开关开启且为 OpenAI 非流式响应体时，正确展示 choices（含 tool_calls）/ usage token 栅格 / 其他字段折叠
- [x] 开关开启且为 Anthropic 请求体时，正确展示 model / system / messages（content blocks）/ tools / 关键参数 / 其他字段折叠
- [x] 开关开启且为 Anthropic 非流式响应体时，正确展示 role + stop_reason / content blocks / usage token 栅格 / 其他字段折叠
- [x] 开关开启且面板内容为流式聚合文本（非 JSON）时，以纯文本 `<pre>` 展示，不报错
- [x] 开关开启且面板内容含 `error` 对象时，醒目展示 error.type / error.message
- [x] 开关开启且 JSON 无法识别结构时，整体以可折叠 JSON 树展示
- [x] JSON 树支持逐节点展开/折叠，第 3 层及以下默认折叠，按类型语法高亮
- [x] 客户端侧面板使用 `clientFormat`、下游侧面板使用 `serviceFormat` 进行格式分发
- [x] 所有新增标签同时提供中英文案，语言切换后已打开的日志详情即时重渲染为新语言
- [x] 全屏按钮、空态文案、重放区块等既有功能无回归
- [x] 改动仅限前端四个文件（index.html / app.js / style.css / i18n.js），未触及后端与数据模型
