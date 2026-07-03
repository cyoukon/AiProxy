using AiProxy.Config;
using AiProxy.Data;
using AiProxy.Dtos;
using AiProxy.Forwarding;
using AiProxy.Services;
using AiProxy.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiProxy.Admin;

/// <summary>
/// 管理面板路由注册。所有 /api/* 路由与管理面板 HTML（/）在管理分支内注册（按 URL 前缀隔离，非端口）。
/// 继承全局鉴权（GlobalAuthMiddleware 已在分支入口挂载）。
/// </summary>
public static class AdminEndpoints
{
    /// <summary>嵌入资源中 index.html 的资源名</summary>
    private const string IndexHtmlResource = "AiProxy.Admin.wwwroot.index.html";

    /// <summary>程序集版本号，用作静态资源缓存破坏参数（版本变化 → URL 变化 → 浏览器重新请求）</summary>
    private static readonly string _version = typeof(AdminEndpoints).Assembly
        .GetName().Version?.ToString() ?? "0";

    private static readonly Dictionary<string, (string ResourceName, string ContentType)> StaticAssets = new()
    {
        ["/admin/style.css"] = ("AiProxy.Admin.wwwroot.style.css", "text/css; charset=utf-8"),
        ["/admin/i18n.js"] = ("AiProxy.Admin.wwwroot.i18n.js", "application/javascript; charset=utf-8"),
        ["/admin/app.js"] = ("AiProxy.Admin.wwwroot.app.js", "application/javascript; charset=utf-8"),
    };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // 首页：单页 HTML（动态注入版本号到资源引用）
        endpoints.MapGet("/", IndexHandler).ExcludeFromDescription();

        // 浏览器 favicon 静默处理（避免命中业务分支 400）
        endpoints.MapGet("/favicon.ico", context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }).ExcludeFromDescription();

        // 管理面板静态资源（CSS / JS）— 长缓存，靠版本号破坏
        foreach (var (path, asset) in StaticAssets)
        {
            endpoints.MapGet(path, async context =>
            {
                context.Response.ContentType = asset.ContentType;
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                var assembly = typeof(AdminEndpoints).Assembly;
                await using var stream = assembly.GetManifestResourceStream(asset.ResourceName);
                if (stream == null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await stream.CopyToAsync(context.Response.Body);
            }).ExcludeFromDescription();
        }

        // 1. 服务概览
        endpoints.MapGet("/api/overview", OverviewHandler).ExcludeFromDescription();

        // 2. 日志查询：列表（分页）+ 详情
        endpoints.MapGet("/api/logs", LogsListHandler).ExcludeFromDescription();
        endpoints.MapGet("/api/logs/{id:long}", LogDetailHandler).ExcludeFromDescription();

        // 3. 用量统计
        endpoints.MapGet("/api/stats", StatsHandler).ExcludeFromDescription();

        // 4. 配置查看（脱敏）+ CRUD
        endpoints.MapGet("/api/config", ConfigHandler).ExcludeFromDescription();
        endpoints.MapPost("/api/ai-services", AddServiceHandler).ExcludeFromDescription();
        endpoints.MapPut("/api/ai-services/{name}", UpdateServiceHandler).ExcludeFromDescription();
        endpoints.MapDelete("/api/ai-services/{name}", DeleteServiceHandler).ExcludeFromDescription();
        endpoints.MapPut("/api/config/global-api-key", UpdateGlobalApiKeyHandler).ExcludeFromDescription();

        // 5. 请求重放：POST /api/logs/{id}/replay
        endpoints.MapPost("/api/logs/{id:long}/replay", ReplayHandler).ExcludeFromDescription();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Handlers
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task IndexHandler(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        // index.html 本身不缓存（每次加载检查资源版本），但内容极小
        ctx.Response.Headers.CacheControl = "no-cache";
        var assembly = typeof(AdminEndpoints).Assembly;
        await using var stream = assembly.GetManifestResourceStream(IndexHtmlResource);
        if (stream == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("index.html embedded resource not found");
            return;
        }
        // 读取 HTML 并注入版本号到静态资源引用（/admin/style.css → /admin/style.css?v=1.0.0）
        using var reader = new System.IO.StreamReader(stream);
        var html = await reader.ReadToEndAsync();
        html = html.Replace("/admin/style.css", $"/admin/style.css?v={_version}");
        html = html.Replace("/admin/i18n.js", $"/admin/i18n.js?v={_version}");
        html = html.Replace("/admin/app.js", $"/admin/app.js?v={_version}");
        await ctx.Response.WriteAsync(html);
    }

    private static async Task OverviewHandler(
        HttpContext ctx,
        IOptionsMonitor<AppOptions> opts,
        LogDbContext db)
    {
        var services = opts.CurrentValue.AiServices;
        var todayUtc = DateTime.UtcNow.Date;
        var tomorrowUtc = todayUtc.AddDays(1);

        // 当日每个服务的调用数（按 ServiceName 聚合，单端口部署下不再按端口）
        var todayCounts = await db.RequestLogs
            .AsNoTracking()
            .Where(r => r.RequestTime >= todayUtc && r.RequestTime < tomorrowUtc)
            .GroupBy(r => r.ServiceName)
            .Select(g => new { Service = g.Key ?? string.Empty, Count = (long)g.Count() })
            .ToDictionaryAsync(x => x.Service, x => x.Count);

        var items = services
            .Select(s => new ServiceOverviewDto
            {
                Name = s.Name,
                PathPrefix = s.PathPrefix,
                BaseUrl = s.BaseUrl,
                ApiKey = KeyMasker.Mask(s.ApiKey),
                LogRequestBody = s.LogRequestBody,
                LogResponseBody = s.LogResponseBody,
                Status = "running",
                TodayCalls = todayCounts.TryGetValue(s.Name, out var c) ? c : 0
            })
            .OrderBy(x => x.Name)
            .ToList();

        await ctx.Response.WriteAsJsonAsync(new { services = items });
    }

    private static async Task LogsListHandler(
        HttpContext ctx,
        LogDbContext db)
    {
        var q = ctx.Request.Query;
        // 解析过滤参数
        DateTime? from = ParseDate(q["from"]);
        DateTime? to = ParseDate(q["to"]);
        string? service = string.IsNullOrWhiteSpace(q["service"]) ? null : q["service"].ToString();
        string? model = string.IsNullOrWhiteSpace(q["model"]) ? null : q["model"].ToString();
        int? status = ParseInt(q["status"]);
        // status 简化语义：1=成功 (200)、0=失败 (非 200)、null=全部
        bool? successOnly = status switch
        {
            1 => true,
            0 => false,
            _ => null
        };

        int page = ParseInt(q["page"]) ?? 1;
        int pageSize = ParseInt(q["pageSize"]) ?? 50;
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 500) pageSize = 50;

        var query = db.RequestLogs.AsNoTracking();
        if (from.HasValue) query = query.Where(r => r.RequestTime >= from.Value);
        if (to.HasValue) query = query.Where(r => r.RequestTime <= to.Value);
        if (!string.IsNullOrEmpty(service)) query = query.Where(r => r.ServiceName == service);
        if (!string.IsNullOrEmpty(model)) query = query.Where(r => r.Model == model);
        if (successOnly.HasValue) query = query.Where(r => r.IsSuccess == successOnly.Value);

        var total = await query.LongCountAsync();
        var items = await query
            .OrderByDescending(r => r.RequestTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new LogListItemDto
            {
                Id = r.Id,
                RequestTime = r.RequestTime,
                ServiceName = r.ServiceName,
                Method = r.Method,
                ClientPath = r.ClientPath,
                DownstreamUrl = r.DownstreamUrl,
                StatusCode = r.StatusCode,
                DurationMs = r.DurationMs,
                Model = r.Model,
                IsStream = r.IsStream,
                IsReplay = r.IsReplay,
                IsConverted = r.IsConverted,
                IsSuccess = r.IsSuccess,
                ErrorType = r.ErrorType,
                PromptTokens = r.PromptTokens,
                CompletionTokens = r.CompletionTokens,
                TotalTokens = r.TotalTokens
            })
            .ToListAsync();

        await ctx.Response.WriteAsJsonAsync(new PagedResultDto<LogListItemDto>
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items
        });
    }

    private static async Task LogDetailHandler(
        HttpContext ctx,
        LogDbContext db,
        long id)
    {
        var log = await db.RequestLogs
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new LogDetailDto
            {
                Id = r.Id,
                RequestTime = r.RequestTime,
                ServiceName = r.ServiceName,
                Method = r.Method,
                StatusCode = r.StatusCode,
                DurationMs = r.DurationMs,
                Model = r.Model,
                IsStream = r.IsStream,
                IsReplay = r.IsReplay,
                IsConverted = r.IsConverted,
                IsSuccess = r.IsSuccess,
                ErrorType = r.ErrorType,
                PromptTokens = r.PromptTokens,
                CompletionTokens = r.CompletionTokens,
                TotalTokens = r.TotalTokens,
                ClientPath = r.ClientPath,
                ClientFormat = r.ClientFormat,
                ClientRequestBody = r.ClientRequestBody,
                ClientResponseBody = r.ClientResponseBody,
                DownstreamUrl = r.DownstreamUrl,
                ServiceFormat = r.ServiceFormat,
                DownstreamRequestBody = r.DownstreamRequestBody,
                DownstreamResponseBody = r.DownstreamResponseBody
            })
            .FirstOrDefaultAsync();

        if (log == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "log not found" });
            return;
        }
        await ctx.Response.WriteAsJsonAsync(log);
    }

    /// <summary>
    /// 请求重放：POST /api/logs/{id}/replay
    /// 复用原路径前缀下游配置重发请求，返回重放结果 JSON。
    /// 原日志不存在 → 404；原前缀已不再配置 → 409。
    /// </summary>
    private static async Task ReplayHandler(
        HttpContext ctx,
        long id,
        ReplayService replayService)
    {
        var outcome = await replayService.ReplayAsync(id, ctx.RequestAborted);

        if (!outcome.Success)
        {
            ctx.Response.StatusCode = outcome.HttpStatus;
            await ctx.Response.WriteAsJsonAsync(new { error = outcome.Error });
            return;
        }

        await ctx.Response.WriteAsJsonAsync(outcome.Result);
    }

    private static async Task StatsHandler(
        HttpContext ctx,
        StatisticsService stats)
    {
        var q = ctx.Request.Query;
        var dimension = string.Equals(q["dimension"], "model", StringComparison.OrdinalIgnoreCase)
            ? StatsDimension.Model
            : StatsDimension.Service;
        var granularity = string.Equals(q["granularity"], "daily", StringComparison.OrdinalIgnoreCase)
            ? StatsGranularity.Daily
            : StatsGranularity.Cumulative;

        var sq = new StatsQuery
        {
            Granularity = granularity,
            From = ParseDate(q["from"]),
            To = ParseDate(q["to"]),
            Port = ParseInt(q["port"]),
            Service = string.IsNullOrWhiteSpace(q["service"]) ? null : q["service"].ToString(),
            Model = string.IsNullOrWhiteSpace(q["model"]) ? null : q["model"].ToString()
        };

        var resp = dimension == StatsDimension.Service
            ? await stats.GetByServiceAsync(sq, ctx.RequestAborted)
            : await stats.GetByModelAsync(sq, ctx.RequestAborted);

        await ctx.Response.WriteAsJsonAsync(resp);
    }

    private static async Task ConfigHandler(
        HttpContext ctx,
        IOptionsMonitor<AppOptions> opts)
    {
        var appOpts = opts.CurrentValue;
        var view = new ConfigViewDto
        {
            Proxy = new ProxyConfigViewDto
            {
                GlobalApiKey = KeyMasker.Mask(appOpts.Proxy.GlobalApiKey),
                LogDbPath = appOpts.Proxy.LogDbPath,
                ListenAddress = appOpts.Proxy.ListenAddress,
                ListenPort = appOpts.Proxy.ListenPort,
                AuthEnabled = !string.IsNullOrEmpty(appOpts.Proxy.GlobalApiKey)
            },
            AiServices = appOpts.AiServices
                .Select(s => new AiServiceConfigViewDto
                {
                    Name = s.Name,
                    PathPrefix = s.PathPrefix,
                    BaseUrl = s.BaseUrl,
                    ApiKey = KeyMasker.Mask(s.ApiKey),
                    ServiceFormat = s.ServiceFormat.ToString(),
                    ClientFormat = s.ClientFormat?.ToString(),
                    ExtraHeaders = s.ExtraHeaders,
                    LogRequestBody = s.LogRequestBody,
                    LogResponseBody = s.LogResponseBody,
                    AllowInvalidSslCertificates = s.AllowInvalidSslCertificates
                })
                .OrderBy(x => x.Name)
                .ToList()
        };

        await ctx.Response.WriteAsJsonAsync(view);
    }

    /// <summary>新增服务：POST /api/ai-services</summary>
    private static async Task AddServiceHandler(HttpContext ctx, ConfigService svc)
    {
        var input = await ReadBodyAsync<AiServiceInputDto>(ctx);
        if (input == null) return; // ReadBodyAsync 已写 400
        try
        {
            await svc.AddServiceAsync(input, ctx.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status201Created;
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>更新服务：PUT /api/ai-services/{name}</summary>
    private static async Task UpdateServiceHandler(HttpContext ctx, ConfigService svc, string name)
    {
        var input = await ReadBodyAsync<AiServiceInputDto>(ctx);
        if (input == null) return;
        try
        {
            await svc.UpdateServiceAsync(name, input, ctx.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>删除服务：DELETE /api/ai-services/{name}</summary>
    private static async Task DeleteServiceHandler(HttpContext ctx, ConfigService svc, string name)
    {
        try
        {
            await svc.DeleteServiceAsync(name, ctx.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    /// <summary>更新全局密钥：PUT /api/config/global-api-key</summary>
    private static async Task UpdateGlobalApiKeyHandler(HttpContext ctx, ConfigService svc)
    {
        var input = await ReadBodyAsync<GlobalApiKeyInputDto>(ctx);
        if (input == null) return;
        try
        {
            await svc.UpdateGlobalApiKeyAsync(input, ctx.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message);
            return;
        }
        await ctx.Response.WriteAsJsonAsync(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助：参数解析 + 通用响应
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx) where T : class
    {
        T? input;
        try
        {
            input = await ctx.Request.ReadFromJsonAsync<T>();
        }
        catch
        {
            input = null;
        }
        if (input == null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid JSON body");
            return null;
        }
        return input;
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string error)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(new { error });
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // 支持 ISO 8601（含 Z / offset）与 "yyyy-MM-dd"
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
        {
            return d.ToUniversalTime();
        }
        return null;
    }

    private static int? ParseInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s, out var v)) return v;
        return null;
    }
}
