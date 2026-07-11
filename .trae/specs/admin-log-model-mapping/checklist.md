# Checklist

## 后端 — 模型映射
- [x] `AiServiceOptions` 新增 `ModelMappingOptions`（Pattern/Replacement/Enabled）与 `ModelMappings` 列表字段
- [x] `AiServiceConfigViewDto` 与 `AiServiceInputDto` 新增 `ModelMappings` 字段
- [x] `ConfigService` 的 Add/Update 正确读写 `ModelMappings`（新增写入、更新整体覆盖、Pattern 非空且为合法正则）
- [x] `AdminEndpoints.ConfigHandler` 将 `ModelMappings` 映射进视图 DTO
- [x] 配置缺省 `ModelMappings` 字段时等同空列表，不报错（向后无字段兼容）

## 后端 — 转发与日志
- [x] `ForwardingEndpoint.ConvertRequestBodyAsync` 在格式转换后应用模型映射，按序首次匹配替换 model
- [x] 映射仅作用于「下游最终请求体」，客户端原始请求体日志不受影响
- [x] 无 body / 读取失败 / 无命中时 fail-open，原样转发不阻塞
- [x] `RequestLoggingMiddleware` 的 `Model` 提取源改为下游最终请求体（无则回退客户端体）
- [x] 无映射无转换时 Model 与下游请求体日志与改造前一致（无回归）
- [x] 映射生效时日志 Model 显示替换后的实际模型，日志列表与详情均一致
- [x] `dotnet build` 通过，无编译错误与警告回归（注：仅 apphost.exe 拷贝被运行中进程锁定，非编译错误）

## 前端 — 页签持久化
- [x] 切换页签后刷新浏览器，自动恢复到刷新前页签并加载数据
- [x] 首次访问（无 localStorage 记录）默认激活「服务概览」

## 前端 — 日志详情区分与复制
- [x] 结构化与非结构化模式下，请求体与响应体面板有明显视觉区分（色条/图标/标题色）
- [x] 鼠标悬浮面板时右上角显示复制按钮
- [x] 点击复制按钮将面板原始文本写入剪贴板并提示「已复制」
- [x] 空态（未记录）面板的复制按钮禁用或无操作
- [x] 全屏按钮、结构化开关、重放等既有功能无回归

## 前端 — 模型映射 UI
- [x] 服务编辑表单存在「请求模型映射」区块，支持新增/删除/启用/上下排序
- [x] 编辑模式回填已有映射，顺序与配置一致
- [x] 保存后映射持久化到 appsettings.json，重开表单顺序一致
- [x] 正则测试框输入模型后即时显示首条匹配映射（高亮）与替换结果；无匹配提示「无匹配」；非法正则提示错误
- [x] 禁用的映射在测试与实际转发中均被跳过

## 前端 — 模块拆分
- [x] `app.js` 已拆分为 core/logs/structured/services/test/stats 等模块，单文件行数合理（原则 < 600 行）
- [x] `index.html` `<script>` 引用顺序正确，旧 `app.js` 引用已移除
- [x] `AdminEndpoints.cs` 已注册全部新 JS 静态资源并计算版本哈希（`?v=` 注入）
- [x] `.csproj` EmbeddedResource 通配符覆盖新文件（资源能正常加载）
- [x] 拆分后所有页签与功能（概览/日志/统计/配置/测试/重放/结构化视图/语言切换/时区）无回归

## i18n
- [x] 所有新增 UI 文案同时提供中/英文，语言切换后即时生效

## 文档与评审
- [x] `README.md` / `README.en.md` 已更新（模型映射章节 + 配置示例 + 特性表）
- [x] 已执行代码评审并按反馈修复（test.js 拼写修复 + RequestLoggingMiddleware 去重）
- [x] 已构建并端到端验证全部 Scenario（10 项全部 PASS：构建、启动、HTML script、6 个 JS 模块 200、app.js 404、配置 API、服务持久化、正则校验 400、fail-open 转发含实际映射验证、干净关闭）
