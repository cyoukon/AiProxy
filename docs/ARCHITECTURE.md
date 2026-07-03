# 架构设计文档

## 技术栈

| 组件 | 选型 |
|------|------|
| 核心框架 | ASP.NET Core 10 Minimal API |
| Web 服务器 | Kestrel（单端口，`ListenAddress` 可配置） |
| 反向代理 | YARP（微软官方反向代理组件 `IHttpForwarder`） |
| 数据存储 | EF Core + SQLite |
| 管理面板 | 单页 HTML + vanilla JS + Chart.js（CDN），嵌入资源 |
| 配置管理 | 强类型绑定 + `IOptionsMonitor` 热更新 + `JsonNode` 写回 |
| 部署 | 单文件自包含（`PublishSingleFile` + `SelfContained`） |

---

## 项目结构

```
AiProxy/
├── Program.cs                  # 入口：命令行解析、Kestrel、URL 前缀分支、管道装配
├── Config/                     # 强类型配置模型
│   ├── AppOptions.cs           #   根配置（Proxy + AiServices）
│   ├── ProxyOptions.cs         #   GlobalApiKey / LogDbPath / ListenAddress / ListenPort
│   └── AiServiceOptions.cs     #   单下游服务配置（含 ServiceFormat 枚举）
├── Data/                       # EF Core + SQLite 数据层
│   ├── RequestLog.cs           #   日志实体
│   └── LogDbContext.cs         #   DbContext + 连接字符串构建
├── Forwarding/                 # 转发核心
│   ├── ServiceRegistry.cs      #   按路径前缀解析下游服务
│   ├── ForwardingEndpoint.cs   #   YARP 转发 + 路径剥离 + 鉴权注入
│   ├── Converters/             #   OpenAI ↔ Anthropic 格式转换
│   │   ├── IFormatConverters.cs        # 转换器接口 + FormatConverterRegistry
│   │   ├── EndpointPathMapper.cs       # 端点路径映射（messages ↔ chat/completions）
│   │   ├── ClientFormatResolver.cs     # 客户端格式解析（Auto 推断 / 显式配置）
│   │   ├── SseFrameReader.cs           # SSE 帧切分
│   │   ├── ConvertingStream.cs         # 响应转换包装流（流式/非流式）
│   │   ├── AnthropicToOpenAiRequestConverter.cs
│   │   ├── OpenAiToAnthropicRequestConverter.cs
│   │   ├── AnthropicToOpenAiResponseConverter.cs
│   │   └── OpenAiToAnthropicResponseConverter.cs
│   ├── SseAggregator.cs        #   SSE 流式聚合（用于日志）
│   ├── OpenAiParser.cs         #   请求 model / 响应 usage 解析
│   ├── ReplayService.cs        #   请求重放
│   └── RequestLogHelpers.cs    #   共享工具（错误归类等）
├── Middleware/                 # 中间件
│   ├── GlobalAuthMiddleware.cs #   全局鉴权（常数时间比较、Bearer 提取）
│   └── RequestLoggingMiddleware.cs # 日志记录 + 前缀匹配 / 400
├── Services/
│   ├── StatisticsService.cs    #   用量统计聚合
│   ├── ConfigService.cs        #   配置持久化（JsonNode 部分替换 + 写回）
│   ├── ConfigFilePathProvider.cs # 配置文件路径（单例）
│   └── AppConfigRoot.cs        #   IConfigurationRoot 包装（强制 Reload）
├── Admin/                      # Web 管理面板
│   ├── AdminEndpoints.cs       #   管理路由
│   ├── AdminDtos.cs            #   DTO
│   └── wwwroot/index.html      #   单页前端
├── Dtos/
│   └── StatisticsDtos.cs       #   统计 DTO
└── Util/
    ├── KeyMasker.cs            #   密钥脱敏
    └── ConsoleReporter.cs      #   控制台摘要
```

---

## 单端口 URL 前缀路由

整个应用只监听 `ListenAddress:ListenPort` 一个端口，通过 `MapWhen(IsAdminPath)` 在管道层分支：

| 路径 | 分支 | 行为 |
|------|------|------|
| `GET /` | 管理 | 管理面板 HTML |
| `/api/*` | 管理 | 管理 API |
| `/favicon.ico` | 管理 | 204 |
| `/<prefix>/*` | 业务 | 前缀匹配 → 剥离 → 转发 BaseUrl |
| 未匹配前缀 | 业务 | 400 + 可用前缀列表 |

保留前缀 `api`、`v1` 不可作为 PathPrefix。

---

## 中间件管道

```
管理分支：
  GlobalAuthMiddleware → UseRouting → AdminEndpoints

业务分支：
  GlobalAuthMiddleware → RequestLoggingMiddleware → ForwardingEndpoint (YARP)
```

- **GlobalAuthMiddleware**：GlobalApiKey 非空时校验 Authorization（支持 Bearer 与裸 key），失败 401 不转发。GET / 与 /favicon.ico 免鉴权（管理面板外壳加载需要）。
- **RequestLoggingMiddleware**：前缀解析 → 端点路径映射（格式转换场景）→ 缓冲请求体 → ConvertingStream 双写（含格式转换）→ 响应聚合 → fire-and-forget 持久化。
- **ForwardingEndpoint**：YARP `IHttpForwarder` 直接转发（非 InMemoryConfig），自定义 `HttpTransformer` 实现路径剥离、鉴权注入、Accept-Encoding 移除。

---

## 转发机制

不使用 YARP 路由配置（InMemoryConfig），而是用 `IHttpForwarder.SendAsync` 直接转发，实现更细粒度控制：

1. `ServiceRegistry.FindByPath` 按首段匹配，返回 `AiServiceOptions` + 剩余路径
2. `EndpointPathMapper.Map`（`RequestLoggingMiddleware` 中，见下节）按需重写剩余路径
3. `DownstreamTransformer.TransformRequestAsync` 重写 `RequestUri` = BaseUrl + 剩余路径 + QueryString
4. `ApplyAuthorization` 根据 `ServiceFormat` 注入对应鉴权头（Bearer / x-api-key）
5. 移除 Accept-Encoding 确保下游返回未压缩内容，便于 `ConvertingStream` 旁路捕获

共用一个 `HttpMessageInvoker`（SocketsHttpHandler），连接池复用。SSL 回调根据 `AllowInvalidSslCertificates` 按 host 判断是否跳过验证。

---

## OpenAI ↔ Anthropic 格式转换

每个下游服务的 `ServiceFormat` 描述下游接口协议；客户端实际格式由 `ClientFormat` 决定
（`null` = 按请求头自动推断，显式配置且与 `ServiceFormat` 不同则启用双向转换）。转换分三层，
均由 `RequestLoggingMiddleware` 在包装 `Response.Body` 前统一发起：

1. **端点路径映射**（`EndpointPathMapper`）：OpenAI 与 Anthropic 的 API 路径本身不同
   （如 `chat/completions` vs `messages`），转换体前必须先重写路径，否则请求会打到
   下游不存在的端点。按尾段匹配已知端点对照表（不依赖是否有 `/v1/` 版本前缀），
   命中后保留前导部分不变仅替换尾段，未命中原样透传。
2. **请求体转换**（`ForwardingEndpoint.ConvertRequestBodyAsync`）：`IRequestConverter` 将
   客户端格式 JSON 树改写为下游格式（`AnthropicToOpenAiRequestConverter` /
   `OpenAiToAnthropicRequestConverter`），替换 `Request.Body` 并修正 `Content-Length`。
3. **响应体转换**（`ConvertingStream`）：下游格式转回客户端格式。流式（SSE）逐块喂
   `IStreamingResponseConverter`（内部用 `SseFrameReader` 重组完整事件后转换，保持实时性）；
   非流式缓冲完整响应后整体调用 `INonStreamingResponseConverter.Convert`。转换后字节写回
   客户端，同时旁路捕获供日志/Token 解析。

三层均 fail-open：解析失败或结构不识别时原样透传，不会因转换异常阻塞请求。
`ClientFormat == ServiceFormat`（含 identity 透传场景）时 `FormatConverterRegistry` 返回
无状态 `IdentityConverter`，三层均等价于原样转发，性能与转换前一致。

`ReplayService` 复用同一套转换器（`ConvertStreamResponse` 对完整字节一次性 `Process + Flush`），
保持重放行为与业务转发一致。

---

## 配置热更新

```
管理面板 CRUD → ConfigService 写回 appsettings.json
                → AppConfigRoot.Reload()
                → IOptionsMonitor<AppOptions> 触发 Change Token
                → ServiceRegistry / GlobalAuthMiddleware 读 .CurrentValue 获取最新配置
```

- `IOptionsMonitor`（非 `IOptionsSnapshot`）确保 Singleton 服务安全使用
- `ConfigService` 使用 `JsonNode` 仅替换 `Proxy` + `AiServices` 节点，保留其他顶层配置
- `reloadOnChange: true` 确保外部编辑也能触发热更新

运行时可改：`GlobalApiKey`、`AiServices` 全部属性。
启动时固定（需重启）：`ListenAddress`、`ListenPort`、`LogDbPath`。

---

## 日志与统计

- **RequestLog** 实体：时间、服务、路径、方法、状态码、耗时、请求/响应体、Token 用量、模型、流式标记、重放标记
- **持久化策略**：fire-and-forget `Task.Run` + 独立 scope，DB 失败不影响主请求
- **统计聚合**：EF Core GroupBy 在 SQLite 侧完成，避免全表加载
- **索引**：RequestTime / ServiceName / Model / ListenPort

---

## SSL 证书验证

`SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback` 按目标主机名查找服务配置：

- 证书无错误 → 放行
- 有错误 + 服务配置 `AllowInvalidSslCertificates=true` → 放行（打印警告）
- 有错误 + 未配置跳过 → 拒绝

`PooledConnectionLifetime=2min` 确保配置变更后连接回收，新连接重新执行回调。

---

## 请求重放

`ReplayService` 基于历史 `RequestLog`：

1. 按 `ServiceName` 定位当前服务配置（支持热更新后的新配置）
2. 构造下游请求（复用原 Method / Path / Body）
3. 注入当前下游鉴权头（与业务转发同规则）
4. 执行并解析响应（流式/非流式）
5. 写入新 `RequestLog`（IsReplay=true）

不走 YARP IHttpForwarder（需要包装为 JSON 返回管理端）。

---

## 性能参考

| 指标 | 目标 | 实测 |
|------|------|------|
| 单请求额外耗时 | < 10ms | ~1.3ms |
| SSE 流式延迟 | 与原生一致 | 首块立即到达 |
| 空闲内存 | < 50MB | ~83MB（.NET 10 自包含基线） |
