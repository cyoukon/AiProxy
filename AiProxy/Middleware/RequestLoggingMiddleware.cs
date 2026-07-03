using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AiProxy.Config;
using AiProxy.Data;
using AiProxy.Forwarding;
using AiProxy.Forwarding.Converters;
using AiProxy.Util;
using Microsoft.Extensions.Logging;

namespace AiProxy.Middleware;

/// <summary>
/// 请求/响应日志记录中间件：包裹 YARP 转发主链路，捕获元数据、请求体、响应体、Token、错误类型。
/// 流式响应通过 ConvertingStream 边转发边捕获，流结束后一次性聚合 SSE 内容写入数据库（不保存分片）。
/// 日志持久化以 fire-and-forget 方式执行，DB 写入失败仅记录到日志，不影响主请求。
/// </summary>
public sealed class RequestLoggingMiddleware
{
    /// <summary>请求体缓冲阈值（超过此大小转为文件缓冲）</summary>
    private const int BufferThreshold = 8 * 1024 * 1024; // 8MB

    /// <summary>请求体日志记录最大长度</summary>
    private const long MaxRequestBodyLogSize = 16 * 1024 * 1024; // 16MB

    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FormatConverterRegistry _converterRegistry;
    private readonly ConsoleReporter _reporter;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(
        RequestDelegate next,
        IServiceScopeFactory scopeFactory,
        FormatConverterRegistry converterRegistry,
        ConsoleReporter reporter,
        ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _scopeFactory = scopeFactory;
        _converterRegistry = converterRegistry;
        _reporter = reporter;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var registry = context.RequestServices.GetRequiredService<ServiceRegistry>();
        var (service, remainingPath) = registry.FindByPath(context.Request.Path);
        if (service == null)
        {
            // 未匹配到任何下游服务前缀 → 400 + 可用前缀列表，便于客户端排查
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                error = "unknown service prefix",
                availablePrefixes = registry.AvailablePrefixes
            });
            await context.Response.WriteAsync(payload);
            return;
        }

        var sw = Stopwatch.StartNew();
        var startTime = DateTime.UtcNow;

        // 1. 缓冲请求体（同时供转发与日志读取，启用可重读）
        context.Request.EnableBuffering(bufferThreshold: BufferThreshold);

        string? requestBodyText = null;
        if (service.LogRequestBody && RequestLogHelpers.CanHaveBody(context.Request.Method))
        {
            // 预读请求体以便日志记录；EnableBuffering 保证后续 IHttpForwarder 仍能读取
            try
            {
                context.Request.Body.Position = 0;
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true, bufferSize: 8192);
                var sb = new StringBuilder();
                var buf = new char[8192];
                int read;
                long total = 0;
                while ((read = await reader.ReadAsync(buf)) > 0)
                {
                    sb.Append(buf, 0, read);
                    total += read;
                    if (total >= MaxRequestBodyLogSize)
                    {
                        sb.Append("…[truncated]");
                        break;
                    }
                }
                requestBodyText = sb.ToString();
            }
            catch
            {
                // 读取请求体失败不阻塞转发
                requestBodyText = null;
            }
            finally
            {
                context.Request.Body.Position = 0;
            }
        }

        // 2. 包装 Response.Body：统一走 ConvertingStream（下游格式→客户端格式）
        //    ClientFormat=null（Auto）时按鉴权头逐请求推断；from==to 时返回 IdentityConverter 原样透传
        var originalBody = context.Response.Body;
        var clientFormat = ClientFormatResolver.Resolve(service, context);
        var isConverted = clientFormat != service.ServiceFormat;

        // 保存客户端原始路径（映射前），用于日志记录
        var clientPath = remainingPath;

        // 客户端格式与下游格式不同时，OpenAI/Anthropic 端点路径本身不同（如 chat/completions vs messages），
        // 仅转换请求体无法让请求命中下游正确端点，需同步重写路径。已知端点表外的路径原样透传（fail-open）。
        remainingPath = EndpointPathMapper.Map(remainingPath, clientFormat, service.ServiceFormat);

        var streamConv = _converterRegistry.CreateStreaming(service.ServiceFormat, clientFormat);
        var nonStreamConv = _converterRegistry.ResolveNonStream(service.ServiceFormat, clientFormat);
        var captureStream = new ConvertingStream(originalBody, streamConv, nonStreamConv,
            () => (context.Response.ContentType ?? string.Empty).Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));
        // 转换会改变响应长度，清除 Content-Length 改用分块传输（DownstreamTransformer.TransformResponseAsync 亦清除，双保险）
        // identity 场景（推断/显式 clientFormat == ServiceFormat）长度不变，保留 Content-Length
        if (clientFormat != service.ServiceFormat)
        {
            context.Response.Headers.ContentLength = (long?)null;
        }
        using var captureDisposable = captureStream;
        context.Response.Body = captureStream;

        try
        {
            // 将下游服务 + 剥离前缀后的剩余路径放入 Items 供 ForwardingEndpoint 使用
            context.Items[ForwardingEndpoint.ServiceItemKey] = service;
            context.Items[ForwardingEndpoint.RemainingPathItemKey] = remainingPath;
            await _next(context);
        }
        finally
        {
            sw.Stop();
            // 还原 Response.Body 以免影响后续中间件
            context.Response.Body = originalBody;
        }

        // 关键：在读取捕获字节前终结转换（非流式模式的整体 JSON 转换在 CompleteAsync 中完成）
        // 必须 await：Kestrel 禁用同步 IO，CompleteAsync 内部对响应流的写入均为异步。
        await captureStream.CompleteAsync();

        // 3. 解析响应
        var contentType = context.Response.ContentType ?? string.Empty;
        bool isStream = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        string? clientResponseBody = null;
        string? downstreamResponseBody = null;
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;

        // 转换后字节 = 客户端所见响应
        var capturedBytes = captureStream.GetCapturedSpan();
        var decodedClientBytes = TryDecompress(capturedBytes, context.Response.Headers.ContentEncoding.ToString());

        // 下游原始字节 = 下游返回的未转换响应
        var rawCapturedBytes = captureStream.GetRawCapturedSpan();
        var decodedDownstreamBytes = TryDecompress(rawCapturedBytes, context.Response.Headers.ContentEncoding.ToString());

        // 解析 Token 用量（从客户端格式响应体解析，SseAggregator/OpenAiParser 已兼容双格式）
        if (decodedClientBytes.Length > 0)
        {
            if (isStream)
            {
                var (aggregated, p, c, t) = SseAggregator.Aggregate(decodedClientBytes);
                if (service.LogResponseBody) clientResponseBody = aggregated;
                promptTokens = p;
                completionTokens = c;
                totalTokens = t;
            }
            else
            {
                var decoded = Encoding.UTF8.GetString(decodedClientBytes);
                if (service.LogResponseBody) clientResponseBody = decoded;
                var (p, c, t) = OpenAiParser.TryGetUsage(decoded);
                promptTokens = p;
                completionTokens = c;
                totalTokens = t;
            }
        }

        // 下游原始响应体（仅转换场景需要单独记录，identity 场景同客户端响应）
        if (isConverted && service.LogResponseBody && decodedDownstreamBytes.Length > 0)
        {
            if (isStream)
            {
                var (aggregated, _, _, _) = SseAggregator.Aggregate(decodedDownstreamBytes);
                downstreamResponseBody = aggregated;
            }
            else
            {
                downstreamResponseBody = Encoding.UTF8.GetString(decodedDownstreamBytes);
            }
        }

        // 下游请求体（转换后，由 ForwardingEndpoint 存入 Items）
        string? downstreamRequestBody = null;
        if (isConverted && service.LogRequestBody &&
            context.Items.TryGetValue("__AiProxy_ConvertedRequestBody", out var convBody) && convBody is string convStr)
        {
            downstreamRequestBody = convStr;
        }

        // 4. 构造日志实体
        var downstreamUrl = BuildDownstreamUrl(service.BaseUrl, remainingPath, context.Request.QueryString);
        var statusCode = context.Response.StatusCode;
        var log = new RequestLog
        {
            RequestTime = startTime,
            ServiceName = service.Name,
            Method = context.Request.Method,
            StatusCode = statusCode,
            DurationMs = sw.ElapsedMilliseconds,
            IsConverted = isConverted,
            ClientFormat = clientFormat.ToString(),
            ServiceFormat = service.ServiceFormat.ToString(),

            // 客户端侧
            ClientPath = clientPath.ToString(),
            ClientRequestBody = service.LogRequestBody ? requestBodyText : null,
            ClientResponseBody = service.LogResponseBody ? clientResponseBody : null,

            // 下游侧
            DownstreamUrl = downstreamUrl,
            DownstreamRequestBody = service.LogRequestBody ? (isConverted ? downstreamRequestBody : requestBodyText) : null,
            DownstreamResponseBody = service.LogResponseBody ? (isConverted ? downstreamResponseBody : clientResponseBody) : null,

            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            IsSuccess = statusCode == 200,
            ErrorType = RequestLogHelpers.ClassifyError(statusCode),
            IsStream = isStream,
            IsReplay = false,
            Model = OpenAiParser.TryGetModel(requestBodyText)
        };

        // 5. 控制台实时摘要
        try
        {
            _reporter.Report(log);
        }
        catch
        {
            // 控制台输出失败忽略
        }

        // 6. fire-and-forget 持久化到 SQLite（独立 scope，错误不影响主请求）
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LogDbContext>();
                db.RequestLogs.Add(log);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LogPersist FAIL service={Service} path={Path} status={StatusCode}",
                    log.ServiceName, log.DownstreamUrl, log.StatusCode);
            }
        });
    }

    /// <summary>
    /// 构建下游服务的实际完整 URL（BaseUrl + 剥离前缀后的剩余路径 + QueryString）。
    /// 与 ForwardingEndpoint.DownstreamTransformer 中构造 RequestUri 的逻辑一致。
    /// </summary>
    private static string BuildDownstreamUrl(string baseUrl, PathString remainingPath, QueryString queryString)
    {
        var prefix = string.IsNullOrEmpty(baseUrl) ? string.Empty
            : baseUrl.EndsWith('/') ? baseUrl[..^1] : baseUrl;
        return prefix + remainingPath.ToString() + queryString.ToString();
    }

    /// <summary>
    /// 尝试根据 Content-Encoding 解压响应字节。
    /// 支持 gzip、deflate、br（Brotli）。未压缩或解压失败时原样返回。
    /// </summary>
    private static byte[] TryDecompress(ReadOnlySpan<byte> data, string? contentEncoding)
    {
        var raw = data.ToArray();
        if (data.IsEmpty || string.IsNullOrWhiteSpace(contentEncoding))
        {
            return raw;
        }

        try
        {
            var encoding = contentEncoding.Trim().ToLowerInvariant();
            Stream? decompressionStream = null;
            var inputStream = new MemoryStream(raw);

            if (encoding.Contains("gzip"))
            {
                decompressionStream = new GZipStream(inputStream, CompressionMode.Decompress);
            }
            else if (encoding.Contains("deflate"))
            {
                decompressionStream = new DeflateStream(inputStream, CompressionMode.Decompress);
            }
            else if (encoding.Contains("br"))
            {
                decompressionStream = new BrotliStream(inputStream, CompressionMode.Decompress);
            }

            if (decompressionStream == null)
            {
                return raw;
            }

            using (decompressionStream)
            {
                using var output = new MemoryStream();
                decompressionStream.CopyTo(output);
                return output.ToArray();
            }
        }
        catch
        {
            // 解压失败则原样返回（可能并非真正压缩内容）
            return raw;
        }
    }
}
