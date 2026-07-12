# 管理面板日志增强与请求模型映射 Spec

## Why

当前管理面板存在若干体验与功能缺口：日志详情结构化视图中请求体与响应体视觉区分不明显、文本块缺少一键复制、刷新后页签丢失、日志模型字段记录的是客户端请求模型而非实际转发模型、下游服务缺少请求模型映射能力（无法将客户端请求的模型名按规则替换后再转发给下游）。此外前端 `app.js` 已逾 1500 行，需合理拆分以利维护。本 spec 一次性补齐上述缺口并完成文档与评审。

## What Changes

* **请求模型映射（后端 + 前端）**：在 `AiServiceOptions` 新增 `ModelMappings` 列表（每项含 `Pattern` 通配符、`Replacement` 替换值、`Enabled` 开关，Seq 顺序）；转发链路在格式转换之后、发送下游之前按配置顺序首次匹配替换 `model` 字段；管理面板服务编辑表单新增映射配置区（增删、启用、上下排序）。

* **实际模型记录**：日志 `Model` 字段改为从「发往下游的最终请求体」（转换 + 映射后）提取，反映真实转发模型；日志详情 meta 与日志列表模型列均显示该实际模型。

* **请求/响应体视觉区分**：结构化视图下请求体面板与响应体面板采用差异化样式（左侧色条 + 图标 + 标题色调），一眼可辨。

* **文本块悬浮复制**：每个请求/响应体面板在鼠标悬浮时右上角显示复制按钮，一键复制面板原始文本。

* **页签持久化**：当前页签写入 `localStorage`，刷新后自动恢复到刷新前的页签并加载其数据。

* **前端拆分**：将 `app.js` 按职责拆分为多个 `<script>` 模块（核心/日志/配置/测试/统计/结构化渲染），`AdminEndpoints` 同步注册新静态资源并计算版本哈希。

* **文档与评审**：更新 `README.md` / `README.en.md`（模型映射说明、配置示例、面板新特性），执行代码评审与构建验证。

### BREAKING

* `RequestLog.Model` 语义变更：由「客户端请求体 model」改为「发往下游的实际 model」。历史日志保留原值不动，新日志按新语义写入。无需兼容旧版本。

* `AiServiceOptions` 新增 `ModelMappings` 字段；`AiServiceInputDto` / `AiServiceConfigViewDto` 新增同名字段。配置文件缺省该字段等同空列表（无映射）。

## Impact

* Affected specs: 无（新建 spec，与已完成的 `structured-log-body` 互补）。

* Affected code:

  * 后端：`AiProxy/Config/AiServiceOptions.cs`（新增 `ModelMappingOptions` + `ModelMappings`）、`AiProxy/Admin/AdminDtos.cs`（DTO 新增字段）、`AiProxy/Services/ConfigService.cs`（CRUD 处理 `ModelMappings`）、`AiProxy/Admin/AdminEndpoints.cs`（ConfigHandler 映射 + 静态资源注册）、`AiProxy/Forwarding/ForwardingEndpoint.cs`（转换后应用映射）、`AiProxy/Middleware/RequestLoggingMiddleware.cs`（Model 提取源改为下游最终体）。

  * 前端：`wwwroot/index.html`（页签结构、服务表单映射区、script 引用）、`wwwroot/app.js`（拆分为核心模块）、新增 `logs.js` / `services.js` / `test.js` / `stats.js` / `structured.js`、`wwwroot/style.css`（请求/响应区分、复制按钮、映射区样式）、`wwwroot/i18n.js`（新文案）。

  * 文档：`README.md` / `README.en.md`。

## ADDED Requirements

### Requirement: 请求模型映射配置

下游 AI 服务 SHALL 支持配置一组有序的「请求模型映射」，每项含通配符 `Pattern`、替换值 `Replacement`、启用开关 `Enabled`。映射列表可为空（空则不做任何替换）。通配符规则：`*` 匹配任意数量字符（含空），`?` 匹配单个字符，其余字符原义匹配。

#### Scenario: 新增服务时不配置映射

* **WHEN** 用户新增服务且不添加任何模型映射

* **THEN** 该服务 `ModelMappings` 为空列表，转发时原样透传 model 字段

#### Scenario: 编辑服务时配置多条映射并调整顺序

* **WHEN** 用户在服务编辑表单中添加多条映射并用上/下按钮调整顺序后保存

* **THEN** 映射按调整后的顺序持久化到 `appsettings.json`，下次打开编辑表单时顺序一致

#### Scenario: 启用/禁用单条映射

* **WHEN** 用户切换某条映射的启用开关并保存

* **THEN** 禁用的映射在转发匹配时被跳过

### Requirement: 转发时按序首次匹配替换

转发链路 SHALL 在格式转换之后、发送下游之前，按 `ModelMappings` 顺序遍历启用的映射，对请求体 `model` 字段执行：首条通配符 Pattern 匹配命中的映射生效，新 model = `Replacement`，随后停止遍历。无命中则 model 不变。通配符匹配：`*` 匹配任意数量字符，`?` 匹配单个字符。

#### Scenario: 命中映射替换后转发

* **WHEN** 客户端请求 model=`gpt-4`，服务配置首条启用映射 Pattern=`gpt-4` Replacement=`gpt-4o`

* **THEN** 发往下游的请求体 model 被替换为 `gpt-4o`，下游收到 `gpt-4o`

#### Scenario: 无命中原样转发

* **WHEN** 客户端请求 model=`claude-3`，无任何启用映射匹配

* **THEN** 发往下游的请求体 model 保持 `claude-3`

#### Scenario: 跨格式转换后映射仍生效

* **WHEN** 客户端 OpenAI 格式 model=`gpt-4`，服务为 Anthropic 下游且配置映射

* **THEN** 先将请求体转为 Anthropic 格式（model 保留），再对转换后的 Anthropic 请求体应用映射替换 model

### Requirement: 页签持久化

当前页签 SHALL 持久化到 `localStorage`（key `aiproxy_admin_active_tab`），页面加载时自动恢复并激活对应页签、加载其数据。

#### Scenario: 切换页签后刷新

* **WHEN** 用户切换到「日志查询」页签后刷新浏览器

* **THEN** 页面恢复到「日志查询」页签并自动加载日志列表

#### Scenario: 首次访问无记录

* **WHEN** 用户首次访问（localStorage 无记录）

* **THEN** 默认激活「服务概览」页签

### Requirement: 文本块悬浮复制

日志详情中每个请求/响应体面板 SHALL 在鼠标悬浮面板时于右上角显示复制按钮，点击复制该面板的原始文本（结构化开启时复制结构化渲染前的原始 JSON 文本；关闭时复制美化 JSON 文本；空态时禁用）。

#### Scenario: 悬浮显示复制按钮

* **WHEN** 鼠标移入某个请求/响应体面板

* **THEN** 面板右上角出现复制按钮（与全屏按钮并列）

#### Scenario: 点击复制

* **WHEN** 用户点击复制按钮

* **THEN** 面板原始文本写入剪贴板，显示「已复制」提示

### Requirement: 前端模块拆分

`app.js` SHALL 按职责拆分为多个独立 `<script>` 文件，每个文件聚焦单一功能域；拆分后单文件行数合理（原则上 < 600 行），`AdminEndpoints` 为每个新静态资源注册路由并计算内容哈希版本号。

#### Scenario: 拆分后功能不回归

* **WHEN** 拆分完成并重新构建运行

* **THEN** 所有页签、日志详情结构化视图、服务 CRUD、测试、统计、重放功能均与拆分前一致

## MODIFIED Requirements

### Requirement: 请求/响应体面板渲染

`renderBodyPanel` SHALL 在面板根节点标注 `data-kind="request"` 或 `data-kind="response"`，配合 CSS 以左侧色条与图标区分请求（如蓝色 → 图标）与响应（如绿色 ← 图标）方向；结构化与非结构化模式均适用。复制按钮与全屏按钮并列于面板头部右侧。

### Requirement: 日志 Model 字段提取

`RequestLoggingMiddleware` 构造 `RequestLog` 时，`Model` 字段 SHALL 从「发往下游的最终请求体」（格式转换 + 模型映射后的请求体）提取，而非客户端原始请求体。当无请求体或提取失败时为 null。该值同时用于日志列表模型列与日志详情 meta 模型字段。

#### Scenario: 无映射无转换

* **WHEN** 请求未触发格式转换且无模型映射

* **THEN** Model = 客户端请求体 model（与改造前一致）

#### Scenario: 经映射后

* **WHEN** 请求 model 经映射由 `gpt-4` 替换为 `gpt-4o`

* **THEN** 日志 Model = `gpt-4o`，日志列表与详情均显示 `gpt-4o`

### Requirement: 下游请求体日志记录

当发生格式转换或模型映射（下游最终请求体与客户端原始请求体不同）时，`RequestLog.DownstreamRequestBody` SHALL 记录下游最终请求体（转换 + 映射后）；identity 且无映射时仍记录客户端原始请求体（与改造前一致）。

### Requirement: ConvertRequestBodyAsync

`ForwardingEndpoint.ConvertRequestBodyAsync` SHALL 在格式转换之后追加模型映射步骤：解析转换后请求体的 `model`，按服务 `ModelMappings` 顺序首次匹配替换，将最终请求体写回 `HttpContext.Request.Body` 并存入 `context.Items` 供日志记录。映射失败或无 body 时不阻塞转发（fail-open）。
