using System.Diagnostics;
using System.Net.Http;
using System.Text;
using AiProxy.Config;
using AiProxy.Data;
using AiProxy.Forwarding.Converters;
using AiProxy.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiProxy.Forwarding;

/// <summary>
/// 请求重放服务：基于历史 <see cref="RequestLog"/> 复用原端口绑定的下游服务配置，
/// 原样重新发起请求；重放请求计入日志与用量统计（<see cref="RequestLog.IsReplay"/> = true）。
/// 同时支持流式（SSE 聚合）与非流式重放，行为与原生调用一致（需求 2.6）。
///
/// 与业务端口转发共享以下核心，保持 DRY：
/// - 同一 <see cref="HttpMessageInvoker"/>（连接池复用）
/// - <see cref="ForwardingEndpoint.ApplyAuthorization"/> 鉴权头注入逻辑
/// - <see cref="SseAggregator"/> / <see cref="OpenAiParser"/> 响应解析
/// - <see cref="RequestLogHelpers"/> 错误归类与请求体判定
/// - <see cref="RequestLog"/> 实体与 <see cref="ConsoleReporter"/> 控制台摘要
///
/// 重放响应以 JSON 包装返回给管理端调用方（含 replayLogId / statusCode / responseBody 等，
/// 见 <see cref="ReplayResult"/>），因此不走 YARP IHttpForwarder（避免直接写入管理端 Response.Body
/// 而无法包装为 JSON 结果）。下游调用本身仍使用与业务端口同一套 HTTP 客户端与鉴权规则。
/// </summary>
public sealed class ReplayService
{
    /// <summary>重放请求的超时上限，与业务端口 ActivityTimeout 一致</summary>
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(10);

    private readonly HttpMessageInvoker _httpClient;
    private readonly LogDbContext _db;
    private readonly ServiceRegistry _registry;
    private readonly FormatConverterRegistry _converterRegistry;
    private readonly ConsoleReporter _reporter;
    private readonly ILogger<ReplayService> _logger;

    public ReplayService(
        HttpMessageInvoker httpClient,
        LogDbContext db,
        ServiceRegistry registry,
        FormatConverterRegistry converterRegistry,
        ConsoleReporter reporter,
        ILogger<ReplayService> logger)
    {
        _httpClient = httpClient;
        _db = db;
        _registry = registry;
        _converterRegistry = converterRegistry;
        _reporter = reporter;
        _logger = logger;
    }

    /// <summary>
    /// 重放指定日志对应的请求。
    /// 返回 <see cref="ReplayOutcome"/>：成功时含 <see cref="ReplayResult"/>；
    /// 失败时含 HTTP 状态码（404/409）与错误信息，由调用方写回管理端响应。
    /// </summary>
    public async Task<ReplayOutcome> ReplayAsync(long originalLogId, CancellationToken ct)
    {
        // 1. 加载原始日志
        var original = await _db.RequestLogs.AsNoTracking()
            .Where(r => r.Id == originalLogId)
            .FirstOrDefaultAsync(ct);
        if (original == null)
        {
            return ReplayOutcome.Fail(StatusCodes.Status404NotFound, "log not found");
        }

        // 2. 按服务名定位当前下游服务配置（IOptionsMonitor 支持热更新）
        //    若服务已不再配置（如配置变更后移除），返回 409 Conflict
        var service = _registry.FindByName(original.ServiceName);
        if (service == null || string.IsNullOrWhiteSpace(service.BaseUrl))
        {
            return ReplayOutcome.Fail(
                StatusCodes.Status409Conflict,
                $"服务 \"{original.ServiceName}\" 已不再配置对应下游服务，无法重放");
        }

        // 3. 构造下游请求并执行
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        var sw = Stopwatch.StartNew();
        var startTime = DateTime.UtcNow;

        int statusCode = 0;
        bool isStream = false;
        string? responseBodyText = null; // 解析后的响应体（流式为聚合后内容）
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;

        try
        {
            request = BuildDownstreamRequest(service, original);

            // HttpMessageInvoker 不应用 SocketsHttpHandler.Timeout（仅 HttpClient 才应用），
            // 因此这里用链接 CTS 控制重放超时，与业务端口 ActivityTimeout 对齐
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            response = await _httpClient.SendAsync(request, cts.Token);

            statusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            isStream = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

            // 读取完整响应字节（流式响应需完整接收后才能聚合）
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);

            // 重放无原始客户端鉴权头，Auto 模式 fallback OpenAI（见 ClientFormatResolver.Resolve 无 context 重载）
            var clientFormat = ClientFormatResolver.Resolve(service);

            if (isStream)
            {
                // 流式：先将下游格式 SSE 字节转为客户端格式，再聚合（与业务端口日志一致）
                // identity 场景 IdentityConverter 原样返回，聚合结果不变
                var bytesToAggregate = ConvertStreamResponse(service, bytes, clientFormat);
                var (aggregated, p, c, t) = SseAggregator.Aggregate(bytesToAggregate);
                responseBodyText = aggregated;
                promptTokens = p;
                completionTokens = c;
                totalTokens = t;
            }
            else
            {
                // 非流式：解码后转为客户端格式（identity 原样返回），再尝试解析 usage
                var decoded = Encoding.UTF8.GetString(bytes);
                var nonStreamConv = _converterRegistry.ResolveNonStream(service.ServiceFormat, clientFormat);
                try { decoded = nonStreamConv.Convert(decoded); } catch { /* fail-open 透传原始 */ }
                responseBodyText = decoded;
                var (p, c, t) = OpenAiParser.TryGetUsage(decoded);
                promptTokens = p;
                completionTokens = c;
                totalTokens = t;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 重放自身超时（非调用方取消）
            statusCode = StatusCodes.Status504GatewayTimeout;
            responseBodyText = responseBodyText ?? "replay timed out";
        }
        catch (HttpRequestException ex)
        {
            // 下游连接失败（DNS / SSL / 拒绝等）→ 502
            statusCode = StatusCodes.Status502BadGateway;
            responseBodyText = $"replay request failed: {ex.GetType().Name}: {ex.Message}";
        }
        catch (Exception ex)
        {
            statusCode = StatusCodes.Status502BadGateway;
            responseBodyText = $"replay failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            request?.Dispose();
            response?.Dispose();
        }

        // 4. 构造新的 RequestLog（IsReplay=true），写入数据库计入日志与用量统计
        var log = new RequestLog
        {
            RequestTime = startTime,
            ServiceName = service.Name,
            Method = original.Method,
            StatusCode = statusCode,
            DurationMs = sw.ElapsedMilliseconds,
            IsConverted = false,
            ClientFormat = ClientFormatResolver.Resolve(service).ToString(),
            ServiceFormat = service.ServiceFormat.ToString(),
            ClientPath = original.ClientPath,
            ClientRequestBody = service.LogRequestBody ? original.ClientRequestBody : null,
            ClientResponseBody = service.LogResponseBody ? responseBodyText : null,
            DownstreamUrl = original.DownstreamUrl,
            DownstreamRequestBody = service.LogRequestBody ? original.DownstreamRequestBody : null,
            DownstreamResponseBody = service.LogResponseBody ? responseBodyText : null,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            IsSuccess = statusCode == StatusCodes.Status200OK,
            ErrorType = RequestLogHelpers.ClassifyError(statusCode),
            IsStream = isStream,
            IsReplay = true,
            Model = OpenAiParser.TryGetModel(original.ClientRequestBody)
        };

        try
        {
            _db.RequestLogs.Add(log);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // 持久化失败不影响返回给调用方的重放结果，仅日志告警
            _logger.LogError(ex, "ReplayPersist FAIL originalId={OriginalLogId}", originalLogId);
        }

        // 5. 实时摘要（与业务端口一致）
        try { _reporter.Report(log); } catch { }

        return ReplayOutcome.Ok(new ReplayResult
        {
            ReplayLogId = log.Id,
            StatusCode = statusCode,
            DurationMs = sw.ElapsedMilliseconds,
            ResponseBody = responseBodyText,
            IsStream = isStream,
            Tokens = new ReplayTokensDto
            {
                Prompt = promptTokens,
                Completion = completionTokens,
                Total = totalTokens
            }
        });
    }

    /// <summary>
    /// 构造下游 HttpRequestMessage：复用原始 Method / Path / RequestBody，
    /// 注入下游 Authorization（与业务端口转发同规则）。不透传原客户端 Authorization。
    /// 日志中的 Path 已是下游完整 URL，直接作为请求目标。
    /// 请求体由客户端格式转为下游格式（identity 原样返回；与业务端口 ForwardingEndpoint 一致）。
    /// </summary>
    private HttpRequestMessage BuildDownstreamRequest(AiServiceOptions service, RequestLog original)
    {
        var method = ParseHttpMethod(original.Method);
        // DownstreamUrl 已存储下游完整 URL
        var uri = original.DownstreamUrl;
        var request = new HttpRequestMessage(method, uri);

        // 注入下游鉴权
        ForwardingEndpoint.ApplyAuthorization(request, service);

        // 还原请求体（使用下游格式的请求体，无需再次转换）
        if (RequestLogHelpers.CanHaveBody(original.Method) && !string.IsNullOrEmpty(original.DownstreamRequestBody))
        {
            request.Content = new StringContent(original.DownstreamRequestBody, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// 将下游格式流式响应字节（完整）转为客户端格式：用流式转换器 Process 全部字节 + Flush 尾段，拼接返回。
    /// 与业务端口 ConvertingStream 行为一致（仅此处为一次性整流转换，因重放已完整读取响应）。
    /// </summary>
    private byte[] ConvertStreamResponse(AiServiceOptions service, byte[] downstreamBytes, ServiceFormat clientFormat)
    {
        var streamConv = _converterRegistry.CreateStreaming(service.ServiceFormat, clientFormat);
        try
        {
            using (streamConv)
            {
                var head = streamConv.Process(downstreamBytes);
                var tail = streamConv.Flush();
                if (tail.Length == 0) return head;
                if (head.Length == 0) return tail;
                var combined = new byte[head.Length + tail.Length];
                Buffer.BlockCopy(head, 0, combined, 0, head.Length);
                Buffer.BlockCopy(tail, 0, combined, head.Length, tail.Length);
                return combined;
            }
        }
        catch
        {
            return downstreamBytes; // fail-open 原样返回
        }
    }

    private static HttpMethod ParseHttpMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return HttpMethod.Post;
        }
        return new HttpMethod(method.ToUpperInvariant());
    }
}

/// <summary>重放结果（成功时返回给管理端调用方的 JSON 包装内容）</summary>
public sealed class ReplayResult
{
    /// <summary>新生成的重放日志 ID</summary>
    public long ReplayLogId { get; set; }
    /// <summary>重放下游响应状态码</summary>
    public int StatusCode { get; set; }
    /// <summary>重放耗时（毫秒）</summary>
    public long DurationMs { get; set; }
    /// <summary>重放响应体（流式为聚合后内容；非流式为原样响应）</summary>
    public string? ResponseBody { get; set; }
    /// <summary>是否为 SSE 流式响应</summary>
    public bool IsStream { get; set; }
    /// <summary>Token 用量</summary>
    public ReplayTokensDto Tokens { get; set; } = new();
}

/// <summary>重放响应中的 Token 用量子对象</summary>
public sealed class ReplayTokensDto
{
    public int? Prompt { get; set; }
    public int? Completion { get; set; }
    public int? Total { get; set; }
}

/// <summary>
/// 重放操作结果：成功时 <see cref="Success"/> 为 true 且 <see cref="Result"/> 含数据；
/// 失败时 <see cref="HttpStatus"/> 为应写回管理端的 HTTP 状态码（404/409），<see cref="Error"/> 为错误信息。
/// </summary>
public sealed class ReplayOutcome
{
    public bool Success { get; init; }
    public int HttpStatus { get; init; }
    public string? Error { get; init; }
    public ReplayResult? Result { get; init; }

    public static ReplayOutcome Ok(ReplayResult result) => new()
    {
        Success = true,
        HttpStatus = StatusCodes.Status200OK,
        Result = result
    };

    public static ReplayOutcome Fail(int httpStatus, string error) => new()
    {
        Success = false,
        HttpStatus = httpStatus,
        Error = error
    };
}
