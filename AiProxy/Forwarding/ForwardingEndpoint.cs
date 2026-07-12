using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AiProxy.Config;
using AiProxy.Forwarding.Converters;
using AiProxy.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace AiProxy.Forwarding;

/// <summary>
/// 业务转发处理：使用 YARP IHttpForwarder 将请求透明转发至下游 BaseUrl。
/// 转发前：
/// - 替换 Authorization 头为下游绑定密钥（ApiKey 为空时移除该头）。
/// - 剥离 URL 路径首段前缀（/&lt;prefix&gt;/chat/completions → /chat/completions），重写 proxyRequest.RequestUri，
///   不修改 httpContext.Request.Path（保证日志记录原始路径、重放可重新解析前缀）。
/// 转发决策：不使用 YARP 路由配置（InMemoryConfig），而是用 IHttpForwarder 直接转发，
/// 这样可以更细粒度控制每服务的目标地址、密钥替换与路径剥离。
/// </summary>
public sealed class ForwardingEndpoint
{
    /// <summary>HttpContext.Items 中存放当前请求所属下游服务的键</summary>
    public const string ServiceItemKey = "__AiProxy_Service";

    /// <summary>HttpContext.Items 中存放剥离前缀后剩余路径的键（供转发重写 RequestUri）</summary>
    public const string RemainingPathItemKey = "__AiProxy_RemainingPath";

    /// <summary>HttpContext.Items 中存放转换后请求体长度的键（供 DownstreamTransformer 修正 Content-Length）</summary>
    public const string ConvertedRequestLengthItemKey = "__AiProxy_ConvertedRequestLength";

    private static readonly ForwarderRequestConfig _requestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(10), // 长会话 / 大响应场景
        AllowResponseBuffering = false // 关键：禁用响应缓冲，确保 SSE 流式低延迟透传
    };

    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _httpClient;
    private readonly FormatConverterRegistry _converterRegistry;
    private readonly ILogger<ForwardingEndpoint> _logger;

    public ForwardingEndpoint(
        IHttpForwarder forwarder,
        HttpMessageInvoker httpClient,
        FormatConverterRegistry converterRegistry,
        ILogger<ForwardingEndpoint> logger)
    {
        _forwarder = forwarder;
        _httpClient = httpClient;
        _converterRegistry = converterRegistry;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.TryGetValue(ServiceItemKey, out var svcObject) || svcObject is not AiServiceOptions service)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Service binding not found");
            return;
        }

        // 取日志中间件解析的剩余路径（剥离前缀后）
        PathString remainingPath = PathString.Empty;
        if (context.Items.TryGetValue(RemainingPathItemKey, out var rpObj) && rpObj is PathString rp)
        {
            remainingPath = rp;
        }

        // 请求格式转换：客户端格式 → 下游格式（替换 Request.Body，记录新长度供 transformer 修正 Content-Length）
        // 注意：日志中间件已预读原始请求体用于日志记录（客户端原始格式），此处替换不影响日志内容。
        // identity 场景（ClientFormat == ServiceFormat）IdentityConverter 原样返回，body 不变。
        await ConvertRequestBodyAsync(context, service);

        await ForwardAsync(context, service, remainingPath);
    }

    /// <summary>
    /// 将客户端格式请求体转为下游格式并替换 <see cref="HttpContext.Request.Body"/>。
    /// ClientFormat=null（Auto）时按鉴权头推断；identity 场景经 IdentityConverter 原样返回，body 不变。
    /// 在转换后追加模型映射步骤：按 <see cref="AiServiceOptions.ModelMappings"/> 顺序首次命中替换 model 字段。
    /// fail-open：转换/映射失败或无可转换体时原样透传。转换后新长度存入 <see cref="ConvertedRequestLengthItemKey"/>。
    /// 必须使用异步读取：请求体在 LogRequestBody=false 时未被中间件预读，可能仍是 Kestrel 原始请求流，
    /// Kestrel 默认禁用同步 IO（AllowSynchronousIO=false），同步 ReadToEnd 会抛 InvalidOperationException。
    /// </summary>
    private async Task ConvertRequestBodyAsync(HttpContext context, AiServiceOptions service)
    {
        if (!RequestLogHelpers.CanHaveBody(context.Request.Method))
        {
            return;
        }

        var clientFormat = ClientFormatResolver.Resolve(service, context);
        var reqConverter = _converterRegistry.ResolveRequest(clientFormat, service.ServiceFormat);

        string originalBody;
        try
        {
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true, bufferSize: 8192);
            originalBody = await reader.ReadToEndAsync();
        }
        catch
        {
            return; // 读取失败不阻塞转发
        }

        if (string.IsNullOrEmpty(originalBody))
        {
            return;
        }

        string converted;
        try
        {
            converted = reqConverter.Convert(originalBody);
        }
        catch
        {
            return; // 转换异常 fail-open 原样透传
        }

        // 模型映射：在转换后请求体上按顺序首次命中替换 model 字段（fail-open，异常不阻塞转发）
        var (finalBody, modelMapped) = ApplyModelMappings(converted, service);

        var bytes = Encoding.UTF8.GetBytes(finalBody);
        var ms = new MemoryStream(bytes);
        ms.Position = 0;
        context.Request.Body = ms;
        context.Items[ConvertedRequestLengthItemKey] = (long)bytes.Length;
        // 存储转换+映射后的最终请求体供日志记录（与原 __AiProxy_ConvertedRequestBody key 约定保持一致）
        context.Items["__AiProxy_ConvertedRequestBody"] = finalBody;
        // 标记是否触发了模型映射（identity 场景下也可能为 true），供日志中间件决定下游请求体来源
        context.Items["__AiProxy_ModelMapped"] = modelMapped;
    }

    /// <summary>
    /// 在转换后请求体上应用模型映射：按 <paramref name="service"/>.<see cref="AiServiceOptions.ModelMappings"/>
    /// 顺序遍历，跳过 <see cref="ModelMappingOptions.Enabled"/>=false 的项，对 model 字段做通配符匹配。
    /// 首次命中即用 <see cref="ModelMappingOptions.Replacement"/> 直接替换 model 并停止遍历。
    /// fail-open：任何异常返回原 body 与 changed=false。
    /// </summary>
    /// <returns>(mappedBody, changed)：changed=true 表示 model 被替换（请求体已变更）。</returns>
    internal static (string mappedBody, bool changed) ApplyModelMappings(string body, AiServiceOptions service)
    {
        if (service.ModelMappings is null || service.ModelMappings.Count == 0)
        {
            return (body, false);
        }

        var model = OpenAiParser.TryGetModel(body);
        if (model is null)
        {
            return (body, false); // 无 model 字段不处理
        }

        foreach (var mapping in service.ModelMappings)
        {
            if (!mapping.Enabled)
            {
                continue;
            }
            if (string.IsNullOrEmpty(mapping.Pattern))
            {
                continue; // 空 Pattern 跳过
            }

            try
            {
                if (!WildcardMatcher.IsMatch(model, mapping.Pattern, !mapping.CaseSensitive))
                {
                    continue;
                }
                var newModel = mapping.Replacement;
                if (newModel == model)
                {
                    return (body, false);
                }
                var mappedBody = ReplaceModelInBody(body, newModel);
                return (mappedBody, true);
            }
            catch
            {
                return (body, false); // 匹配异常 fail-open
            }
        }

        return (body, false); // 无命中
    }

    /// <summary>
    /// 将请求体 JSON 中的 model 字段替换为 <paramref name="newModel"/>。
    /// 通过 <see cref="JsonNode"/> 解析并设置 root["model"]，重新序列化（格式可能变化，可接受）。
    /// 解析失败则原样返回 <paramref name="body"/>（fail-open）。
    /// </summary>
    internal static string ReplaceModelInBody(string body, string newModel)
    {
        try
        {
            var root = JsonNode.Parse(body);
            if (root is JsonObject obj)
            {
                obj["model"] = newModel;
                return obj.ToJsonString();
            }
        }
        catch
        {
            // 解析失败原样返回
        }
        return body;
    }

    /// <summary>
    /// 可复用的转发核心：基于 YARP IHttpForwarder 将当前 HttpContext 透明转发至指定下游服务。
    /// 业务端口管道使用本方法；重放（ReplayService）复用同一 HttpMessageInvoker 与
    /// <see cref="ApplyAuthorization"/> 鉴权注入逻辑，保持转发规则一致（DRY）。
    /// 转发前替换 Authorization 头为下游绑定密钥（ApiKey 为空时移除该头），并剥离 URL 路径前缀。
    /// </summary>
    public async Task ForwardAsync(HttpContext context, AiServiceOptions service, PathString remainingPath)
    {
        if (string.IsNullOrWhiteSpace(service.BaseUrl))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"Service '{service.Name}' has empty BaseUrl");
            return;
        }

        // YARP IHttpForwarder.SendAsync 默认使用 HttpTransformer.Default，
        // 它会原样拷贝请求头（除 Host 外）、请求体、查询字符串、响应头、响应体，
        // 并以 destinationPrefix + Request.Path + QueryString 构造 RequestUri。
        // 我们用自定义 transformer 替换 Authorization 头 + 重写 RequestUri 为剥离前缀后的路径。
        var transformer = new DownstreamTransformer(service, remainingPath);

        var error = await _forwarder.SendAsync(
            context,
            service.BaseUrl,
            _httpClient,
            _requestConfig,
            transformer);

        if (error != ForwarderError.None)
        {
            // 转发失败：YARP 已设置相应状态码（多为 502/504），记录到日志
            _logger.LogError("Forward ERROR service={Service} path={Path} error={Error}",
                service.Name, context.Request.Path, error);
        }
    }

    /// <summary>
    /// 在已构造的 HttpRequestMessage 上注入下游鉴权头与自定义头。
    /// 根据 ServiceFormat 决定注入方式：
    /// - OpenAI：Authorization: Bearer &lt;ApiKey&gt;（OpenAI / Azure / 国产大模型）
    /// - Anthropic：x-api-key: &lt;ApiKey&gt; + anthropic-version 头（Claude 原生接口）
    /// ApiKey 为空时跳过鉴权头注入。
    /// 同时注入 ExtraHeaders 中配置的自定义头。
    /// 客户端原始 Authorization 仅用于代理层鉴权，绝不透传下游。
    /// </summary>
    internal static void ApplyAuthorization(HttpRequestMessage proxyRequest, AiServiceOptions service)
    {
        // 清除客户端原始鉴权头
        proxyRequest.Headers.Authorization = null;
        proxyRequest.Headers.Remove("x-api-key");

        if (!string.IsNullOrEmpty(service.ApiKey))
        {
            switch (service.ServiceFormat)
            {
                case ServiceFormat.OpenAI:
                    proxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", service.ApiKey);
                    break;
                case ServiceFormat.Anthropic:
                    proxyRequest.Headers.TryAddWithoutValidation("x-api-key", service.ApiKey);
                    break;
            }
        }

        // Anthropic 格式：若 ExtraHeaders 中未显式配置 anthropic-version，自动注入默认值
        if (service.ServiceFormat == ServiceFormat.Anthropic)
        {
            var hasVersion = service.ExtraHeaders != null &&
                service.ExtraHeaders.Keys.Any(k => string.Equals(k, "anthropic-version", StringComparison.OrdinalIgnoreCase));
            if (!hasVersion)
            {
                proxyRequest.Headers.Remove("anthropic-version");
                proxyRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
        }

        // 注入自定义额外请求头
        if (service.ExtraHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in service.ExtraHeaders)
            {
                proxyRequest.Headers.Remove(key);
                proxyRequest.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    /// <summary>向后兼容的重载：仅传 apiKey 时以 Bearer 模式注入（供旧调用方使用）</summary>
    internal static void ApplyAuthorization(HttpRequestMessage proxyRequest, string apiKey)
    {
        proxyRequest.Headers.Authorization = null;
        if (!string.IsNullOrEmpty(apiKey))
        {
            proxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>
    /// 自定义请求转换器：在请求转发前注入下游鉴权头与自定义头，
    /// 并重写 RequestUri 为 BaseUrl + 剥离前缀后的剩余路径 + QueryString。
    /// 鉴权注入实际逻辑委托给 <see cref="ApplyAuthorization"/>，避免与重放路径重复实现。
    /// 路径剥离用字符串拼接（与 YARP RequestUtilities.MakeDestinationAddress 行为一致），
    /// 不修改 httpContext.Request.Path，保证日志/重放读取原始路径。
    /// </summary>
    private sealed class DownstreamTransformer : HttpTransformer
    {
        private readonly AiServiceOptions _service;
        private readonly PathString _remainingPath;

        public DownstreamTransformer(AiServiceOptions service, PathString remainingPath)
        {
            _service = service;
            _remainingPath = remainingPath;
        }

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            var prefix = destinationPrefix.EndsWith('/') ? destinationPrefix[..^1] : destinationPrefix;
            var target = prefix + _remainingPath.ToString() + httpContext.Request.QueryString.ToString();
            var targetUri = new Uri(target);
            proxyRequest.RequestUri = targetUri;

            proxyRequest.Headers.Host = targetUri.Host;
            if (!targetUri.IsDefaultPort)
            {
                proxyRequest.Headers.Host += ":" + targetUri.Port;
            }

            ApplyAuthorization(proxyRequest, _service);

            // 移除 Accept-Encoding，确保下游返回未压缩响应，以便 ConvertingStream 旁路捕获纯文本用于日志
            proxyRequest.Headers.AcceptEncoding.Clear();

            proxyRequest.Headers.Remove("X-Forwarded-Host");

            // 格式转换场景：请求体长度已变化，修正 Content-Length（YARP 已拷贝客户端原始长度，此处覆盖为转换后长度）
            if (httpContext.Items.TryGetValue(ConvertedRequestLengthItemKey, out var lenObj) && lenObj is long len)
            {
                if (proxyRequest.Content is not null)
                {
                    proxyRequest.Content.Headers.ContentLength = len;
                }
            }
        }

        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            var proceed = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);

            // 格式转换场景：响应长度已变化，清除 Content-Length 改用分块传输（双保险，中间件亦已清除）
            // identity 场景（推断/显式 clientFormat == ServiceFormat）长度不变，保留 Content-Length
            var clientFormat = ClientFormatResolver.Resolve(_service, httpContext);
            if (clientFormat != _service.ServiceFormat)
            {
                httpContext.Response.Headers.ContentLength = (long?)null;
            }

            return proceed;
        }
    }
}
