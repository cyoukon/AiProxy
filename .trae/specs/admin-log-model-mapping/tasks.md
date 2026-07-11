# Tasks

- [x] Task 1: 后端 — 模型映射配置模型与 CRUD
  - [x] SubTask 1.1: 在 `AiProxy/Config/AiServiceOptions.cs` 新增 `ModelMappingOptions`（`Pattern`/`Replacement`/`Enabled=true`） sealed 类，并在 `AiServiceOptions` 新增 `List<ModelMappingOptions> ModelMappings`（默认空列表）
  - [x] SubTask 1.2: 在 `AiProxy/Admin/AdminDtos.cs` 的 `AiServiceConfigViewDto` 与 `AiServiceInputDto` 新增 `List<ModelMappingOptions> ModelMappings` 字段
  - [x] SubTask 1.3: 在 `AiProxy/Services/ConfigService.cs` 的 `AddServiceAsync` / `UpdateServiceAsync` 中处理 `ModelMappings`（新增时按输入写入；更新时整体覆盖；校验 Pattern 合法正则、非空）
  - [x] SubTask 1.4: 在 `AiProxy/Admin/AdminEndpoints.cs` 的 `ConfigHandler` 中将 `s.ModelMappings` 映射进 `AiServiceConfigViewDto.ModelMappings`

- [x] Task 2: 后端 — 转发链路应用映射 + 实际模型记录
  - [x] SubTask 2.1: 在 `ForwardingEndpoint.ConvertRequestBodyAsync` 格式转换之后追加模型映射：解析转换后请求体 `model`，按 `service.ModelMappings` 顺序对启用项 `Regex.IsMatch`，首次命中执行 `Regex.Replace` 替换 model，写回 body；存 `context.Items["__AiProxy_ModelMapped"]=bool` 与最终下游请求体
  - [x] SubTask 2.2: 在 `RequestLoggingMiddleware` 中将 `Model` 提取源由 `requestBodyText`（客户端体）改为「下游最终请求体」（从 `__AiProxy_ConvertedRequestBody` 读取，无则回退客户端体）
  - [x] SubTask 2.3: 在 `RequestLoggingMiddleware` 中扩展下游请求体记录条件：`isConverted || isModelMapped` 时记录下游最终请求体，否则记录客户端体（保持 identity 无映射时行为不变）
  - [x] SubTask 2.4: 构建通过（`dotnet build`），确认无编译错误

- [x] Task 3: 前端 — 页签持久化
  - [x] SubTask 3.1: 在 `app.js` 新增 `ACTIVE_TAB_KEY='aiproxy_admin_active_tab'`，页签切换时写入当前 `data-tab`；页面初始化时读取并激活对应页签（无记录默认 overview），并触发 `refreshActiveTab`

- [x] Task 4: 前端 — 日志详情请求/响应区分 + 悬浮复制
  - [x] SubTask 4.1: 修改 `renderBodyPanel` 在面板根节点加 `data-kind="request"|"response"`；在 `style.css` 以左侧色条 + 标题色 + 方向图标区分请求（蓝 →）与响应（绿 ←）
  - [x] SubTask 4.2: 在 `renderBodyPanel` 面板头部右侧、全屏按钮旁新增复制按钮（悬浮显示），点击复制面板原始文本（结构化开启时复制 `body` 原始 JSON 文本，关闭时复制美化 JSON；空态禁用）；用 `navigator.clipboard.writeText` + toast 提示
  - [x] SubTask 4.3: 在 `i18n.js` 新增 `logDetail.copy` / `logDetail.copied` 等文案（中/英）

- [x] Task 5: 前端 — 服务编辑模型映射 UI
  - [x] SubTask 5.1: 在 `index.html` 服务 Modal 表单新增「请求模型映射」区块（标题 + 列表容器 + 「+ 新增映射」按钮 + 测试输入框 + 测试结果区）
  - [x] SubTask 5.2: 在 `app.js` 实现映射行渲染：每行含 Pattern 输入、Replacement 输入、启用 checkbox、上移/下移按钮、删除按钮；支持增删与排序
  - [x] SubTask 5.3: 实现正则匹配测试：监听测试输入框 input，按当前表单中的映射顺序对启用项 `new RegExp(pattern).test(model)`，首次命中高亮该行并显示 `model.replace(new RegExp(pattern,'g'), replacement)` 结果；无匹配显示「无匹配」；正则非法时提示错误
  - [x] SubTask 5.4: `openServiceModal` 编辑模式回填 `s.modelMappings`；`saveService` 将映射列表收集进 payload；新增模式默认空列表
  - [x] SubTask 5.5: 在 `i18n.js` 新增映射区相关文案（`service.modelMappings` / `service.mappingsPattern` / `service.mappingsReplacement` / `service.mappingsEnabled` / `service.mappingsAdd` / `service.mappingsTest` / `service.mappingsTestPlaceholder` / `service.mappingsNoMatch` / `service.mappingsInvalidRegex` / `service.mappingsMoveUp` / `service.mappingsMoveDown` / `service.mappingsDelete` / `service.mappingsResult` 等，中/英）

- [x] Task 6: 前端 — `app.js` 模块拆分
  - [x] SubTask 6.1: 将 `app.js` 按职责拆分为：`core.js`（$/$$、api、toast、fmtDate、escapeHtml、tryPrettyJson、页签、鉴权、时区、语言、启动入口）、`logs.js`（日志列表 + 日志详情 + 重放）、`structured.js`（JSON 树 + OpenAI/Anthropic 语义渲染器 + pickBodyRenderer）、`services.js`（配置管理 + 服务 Modal + 模型映射 UI + 全局密钥）、`test.js`（服务测试）、`stats.js`（统计）。共享全局变量集中在 `core.js` 暴露
  - [x] SubTask 6.2: 更新 `index.html` `<script>` 引用顺序（core → structured → logs → services → test → stats），移除旧 `app.js` 引用
  - [x] SubTask 6.3: 在 `AdminEndpoints.cs` 的 `StaticAssets` 字典注册新增 JS 资源（资源名 `AiProxy.Admin.wwwroot.<name>.js`），确保 `?v=hash` 注入与 ETag 计算覆盖新文件；确认 `wwwroot/` 下 `.csproj` EmbeddedResource 通配符已覆盖新文件（若未覆盖则补充）
  - [x] SubTask 6.4: 删除原 `app.js`（内容已迁移），确认无残留引用

- [x] Task 7: 文档 + 评审 + 测试
  - [x] SubTask 7.1: 更新 `README.md` / `README.en.md`：新增「请求模型映射」章节（配置示例 JSON、正则说明、匹配规则、面板操作说明）；在特性表补充模型映射、页签持久化、悬浮复制等
  - [x] SubTask 7.2: 执行代码评审（用 TRAE-code-review skill 审查本次 diff），按反馈修复
  - [x] SubTask 7.3: 构建并启动服务，端到端验证：配置映射后转发命中替换、日志 Model 显示实际模型、页签刷新保持、悬浮复制可用、请求/响应区分清晰、拆分后各功能正常

# Task Dependencies
- Task 2 依赖 Task 1（配置模型与 DTO 先行）
- Task 5 依赖 Task 1（DTO 字段）与 Task 4/3 同属前端可并行，但 Task 5 的 i18n 依赖其自身 SubTask 5.5
- Task 6（拆分）须在 Task 3/4/5 完成后进行（对最终完整的前端代码做机械拆分，避免反复迁移）
- Task 7 依赖全部前端/后端任务完成
