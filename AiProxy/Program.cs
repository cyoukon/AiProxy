using System.Net;
using System.Text;
using AiProxy.Admin;
using AiProxy.Config;
using AiProxy.Data;
using AiProxy.Forwarding;
using AiProxy.Forwarding.Converters;
using AiProxy.Middleware;
using AiProxy.Services;
using AiProxy.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

// ─────────────────────────────────────────────────────────────────────────────
// 1. 构造 WebApplication：手动装配 Configuration 以便支持自定义配置文件路径与热更新
// ─────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(op =>
{
    op.IncludeScopes = true;
    op.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";
    op.UseUtcTimestamp = true;
    op.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
    op.SingleLine = false;
});

// ─────────────────────────────────────────────────────────────────────────────
// 2. 解析命令行参数：--config <path> / -c <path> 指定配置文件路径
// ─────────────────────────────────────────────────────────────────────────────
var configPath = ParseConfigPath(args);
if (configPath == null)
{
    configPath = $"appsettings.{builder.Environment.EnvironmentName}.json";
}

// 替换默认 appsettings.json：用 --config 指定的路径，启用 reloadOnChange 支持热更新
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// 绑定强类型配置：IOptionsMonitor 支持热更新（Singleton 服务安全读取 .CurrentValue）
builder.Services.Configure<AppOptions>(builder.Configuration);
builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection("Proxy"));
builder.Services.Configure<List<AiServiceOptions>>(builder.Configuration.GetSection("AiServices"));

// 注册 IConfigurationRoot 供 ConfigService 写配置后强制 Reload
builder.Services.AddSingleton(new AppConfigRoot((IConfigurationRoot)builder.Configuration));

// ─────────────────────────────────────────────────────────────────────────────
// 3. 注册服务：YARP IHttpForwarder + 自定义 HttpMessageInvoker + EF Core + 配置持久化
// ─────────────────────────────────────────────────────────────────────────────
// 共用一个 HttpMessageInvoker（基于 SocketsHttpHandler，连接池复用，性能优于 HttpClient）
// 自定义 SSL 证书验证：根据下游服务配置决定是否跳过验证
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton(sp =>
{
    var serviceRegistry = sp.GetRequiredService<ServiceRegistry>();
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var socketsHandler = new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        // 限制连接池生命周期，确保 SSL 配置变更后连接会在此周期后被回收，
        // 新连接将重新执行证书验证回调，使 AllowInvalidSslCertificates 变更生效。
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                // 默认严格验证
                if (errors == System.Net.Security.SslPolicyErrors.None)
                {
                    return true;
                }

                // SocketsHttpHandler 的 SSL 回调没有 HttpRequestMessage 上下文，
                // 从 SslStream.TargetHostName 获取目标主机名
                string? targetHost = null;
                if (sender is System.Net.Security.SslStream sslStream)
                {
                    targetHost = sslStream.TargetHostName;
                }

                if (!string.IsNullOrEmpty(targetHost))
                {
                    var service = serviceRegistry.GetServiceByHost(targetHost);
                    if (service != null && service.AllowInvalidSslCertificates)
                    {
                        logger.LogWarning("Skipping SSL validation for service={Service} host={Host} errors={Errors}",
                            service.Name, targetHost, errors);
                        return true;
                    }
                }

                // 未找到匹配服务或服务未配置跳过验证，拒绝连接
                return false;
            }
        }
    };
    return new HttpMessageInvoker(socketsHandler);
});

// 路由服务（管理分支用 UseRouting/UseEndpoints）
builder.Services.AddRouting();

// 注册 ServiceRegistry（IOptionsMonitor 解析下游服务，配置热更新即时生效）
builder.Services.AddSingleton<ServiceRegistry>();

// 注册 ForwardingEndpoint 为瞬态（每次请求一个实例，依赖注入 IHttpForwarder）
builder.Services.AddTransient<ForwardingEndpoint>();

// 注册格式转换器（无状态单例）+ 注册表（请求/响应 OpenAI↔Anthropic 互转）
builder.Services.AddSingleton<AnthropicToOpenAiRequestConverter>();
builder.Services.AddSingleton<OpenAiToAnthropicRequestConverter>();
builder.Services.AddSingleton<AnthropicToOpenAiResponseConverter>();
builder.Services.AddSingleton<OpenAiToAnthropicResponseConverter>();
builder.Services.AddSingleton<FormatConverterRegistry>();

// 注册 LogDbContext（SQLite），连接字符串由 IOptionsMonitor<AppOptions>.CurrentValue 决定
// 注意：用 IOptionsMonitor 而非 IOptionsSnapshot，避免 AddDbContext 工厂被 Singleton 服务捕获造成 captive dependency
builder.Services.AddDbContext<LogDbContext>((sp, options) =>
{
    var appOpts = sp.GetRequiredService<IOptionsMonitor<AppOptions>>().CurrentValue;
    var connStr = LogDbContext.BuildConnectionString(appOpts.Proxy.LogDbPath);
    options.UseSqlite(connStr);
});

// 注册用量统计服务
builder.Services.AddScoped<StatisticsService>();

// 注册请求重放服务：复用同一 HttpMessageInvoker 与下游鉴权注入逻辑
builder.Services.AddScoped<ReplayService>();

// 注册配置持久化：ConfigFilePathProvider（单例，启动时固定配置文件绝对路径）
//                          + ConfigService（Scoped，CRUD 写回 appsettings.json，IOptionsMonitor 自动 reload）
builder.Services.AddSingleton(new ConfigFilePathProvider(Path.GetFullPath(configPath)));
builder.Services.AddScoped<ConfigService>();

// 注册 ConsoleReporter（Singleton，用于请求摘要日志）
builder.Services.AddSingleton<ConsoleReporter>();

// ─────────────────────────────────────────────────────────────────────────────
// 4. 配置 Kestrel：单端口监听 Proxy.ListenAddress:Proxy.ListenPort
//    业务请求与管理请求共享同一端口，通过 URL 前缀区分（/、/api/* 为管理；其余为业务转发）
//    监听地址/端口在启动时固定，修改需重启进程；下游服务属性（BaseUrl/ApiKey/PathPrefix 等）支持热更新
// ─────────────────────────────────────────────────────────────────────────────
var appOptionsAtStartup = builder.Configuration.Get<AppOptions>() ?? new AppOptions();
var listenAddress = string.IsNullOrWhiteSpace(appOptionsAtStartup.Proxy.ListenAddress)
    ? "localhost"
    : appOptionsAtStartup.Proxy.ListenAddress;
var listenPort = appOptionsAtStartup.Proxy.ListenPort > 0
    ? appOptionsAtStartup.Proxy.ListenPort
    : 8000;

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    if (IsLoopback(listenAddress))
    {
        serverOptions.ListenLocalhost(listenPort);
    }
    else if (IsAnyIp(listenAddress))
    {
        serverOptions.ListenAnyIP(listenPort);
    }
    else
    {
        // 具体 IP（IPv4 或 IPv6，含 [::1] 形式）：去括号后解析
        var ipStr = listenAddress.Trim('[', ']');
        if (!IPAddress.TryParse(ipStr, out var ip))
        {
            throw new InvalidOperationException(
                $"无法解析 Proxy.ListenAddress='{listenAddress}'。" +
                "请使用 localhost / 127.0.0.1 / ::1 / * / 0.0.0.0 / [::] 或具体 IP 地址。");
        }
        serverOptions.Listen(ip, listenPort);
    }
});

var app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 5. 启动时初始化 SQLite（EnsureCreated 自动建表）
// ─────────────────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LogDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. 路由分支（按 URL 前缀，非按端口）
//    管理分支：GET / → 管理面板 HTML；/api/* → 管理 API；/favicon.ico → 204
//    业务分支：/<servicePrefix>/* → 全局鉴权 → 日志记录 → YARP 转发（catch-all）
//    两分支均挂 GlobalAuthMiddleware（行为一致：GlobalApiKey 为空时无鉴权，非空时校验）
// ─────────────────────────────────────────────────────────────────────────────
app.MapWhen(ctx => IsAdminPath(ctx.Request.Path), adminApp =>
{
    adminApp.UseMiddleware<GlobalAuthMiddleware>();
    adminApp.UseRouting();
    adminApp.UseEndpoints(endpoints => AdminEndpoints.Map(endpoints));
});

app.MapWhen(ctx => !IsAdminPath(ctx.Request.Path), businessApp =>
{
    businessApp.UseMiddleware<GlobalAuthMiddleware>();
    businessApp.UseMiddleware<RequestLoggingMiddleware>();
    // 兜底路由：所有路径都走转发（catch-all）
    businessApp.Run(async ctx =>
    {
        var endpoint = ctx.RequestServices.GetRequiredService<ForwardingEndpoint>();
        await endpoint.InvokeAsync(ctx);
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// 7. 启动日志：监听地址:端口、下游服务路由概览（密钥脱敏）
// ─────────────────────────────────────────────────────────────────────────────
var lifetime = app.Lifetime;
lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var displayHost = IsAnyIp(listenAddress) ? "localhost" : listenAddress.Trim('[', ']');

    var logStringBuilder = new StringBuilder()
        .AppendLine("AI Proxy 已启动")
        .AppendLine($"配置文件: {Path.GetFullPath(configPath)}")
        .AppendLine($"日志数据库: {Path.GetFullPath(appOptionsAtStartup.Proxy.LogDbPath)}")
        .AppendLine($"全局鉴权: {(string.IsNullOrEmpty(appOptionsAtStartup.Proxy.GlobalApiKey) ? "已关闭" : "已开启")}")
        .AppendLine($"监听地址: {listenAddress}:{listenPort}")
        .AppendLine($"管理面板: http://{displayHost}:{listenPort}/")
        .AppendLine($"管理 API: http://{displayHost}:{listenPort}/api/...");

    if (appOptionsAtStartup.AiServices.Count == 0)
    {
        logStringBuilder.AppendLine("下游服务路由: （无已配置服务，可通过管理面板新增）");
    }
    else
    {
        logStringBuilder.AppendLine("下游服务路由（URL 前缀 → BaseUrl）：");
        foreach (var s in appOptionsAtStartup.AiServices)
        {
            logStringBuilder.AppendLine($"  /{s.PathPrefix} → {s.Name} → {s.BaseUrl}");
            logStringBuilder.AppendLine($"    ApiKey={(string.IsNullOrEmpty(s.ApiKey) ? "<empty>" : KeyMasker.Mask(s.ApiKey))}  LogReq={s.LogRequestBody} LogResp={s.LogResponseBody}");
            logStringBuilder.AppendLine($"    调用示例: http://{displayHost}:{listenPort}/{s.PathPrefix}/chat/completions");
        }
    }
    logger.LogInformation(logStringBuilder.ToString().TrimEnd());
});

await app.RunAsync();

// ─────────────────────────────────────────────────────────────────────────────
// 辅助：命令行参数解析 + 监听地址分类
// ─────────────────────────────────────────────────────────────────────────────
static string? ParseConfigPath(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if ((a == "--config" || a == "-c") && i + 1 < args.Length)
        {
            return args[i + 1];
        }
        // 支持 --config=path 形式
        if (a.StartsWith("--config=", StringComparison.Ordinal))
        {
            return a["--config=".Length..];
        }
        if (a.StartsWith("-c=", StringComparison.Ordinal))
        {
            return a["-c=".Length..];
        }
    }
    return null;
}

/// <summary>是否为 loopback 地址（localhost / 127.0.0.1 / ::1）</summary>
static bool IsLoopback(string addr) =>
    string.Equals(addr, "localhost", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(addr, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(addr, "::1", StringComparison.OrdinalIgnoreCase);

/// <summary>是否为任意 IP（通配：* / 0.0.0.0 / [::]）</summary>
static bool IsAnyIp(string addr) =>
    string.Equals(addr, "*", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(addr, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(addr, "[::]", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// 判断请求路径是否属于管理分支：
/// - "/" → 管理面板首页 HTML
/// - "/api/*" → 管理 API
/// - "/admin/*" → 管理面板静态资源（CSS/JS）
/// - "/favicon.ico" → 浏览器图标静默 204
/// 其余路径均走业务转发分支（由 RequestLoggingMiddleware 校验服务前缀）。
/// </summary>
static bool IsAdminPath(PathString path)
{
    if (path == "/" || path == "/favicon.ico")
    {
        return true;
    }
    if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }
    if (path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }
    return false;
}
