using AiProxy.Config;

namespace AiProxy.Forwarding.Converters;

/// <summary>
/// 请求体转换器：将客户端格式 JSON 转为下游格式 JSON。
/// 实现 fail-open：解析失败或结构无法识别时原样返回输入，不阻塞转发。
/// </summary>
public interface IRequestConverter
{
    /// <summary>转换请求 JSON。无法识别/无需转换时返回原字符串。</summary>
    string Convert(string requestBody);
}

/// <summary>
/// 流式响应逐块转换器（有状态，每请求一个实例）。
/// 下游 SSE 字节按任意边界到达，转换器内部维护状态机即时输出目标格式字节。
/// </summary>
public interface IStreamingResponseConverter : IDisposable
{
    /// <summary>处理一段下游字节，返回可立即写回客户端的转换后字节（可能为空数组）。</summary>
    byte[] Process(ReadOnlySpan<byte> downstreamChunk);

    /// <summary>下游流结束，返回残余缓冲的转换输出（可能为空数组）。</summary>
    byte[] Flush();
}

/// <summary>
/// 非流式响应转换器：将下游格式完整响应 JSON 转为客户端格式。
/// </summary>
public interface INonStreamingResponseConverter
{
    /// <summary>转换完整响应 JSON。无法识别时原样返回。</summary>
    string Convert(string responseBody);
}

/// <summary>
/// 同格式透传转换器（identity）：源格式与目标格式相同时使用，原样返回输入。
/// 无状态单例，实现三个转换接口，统一转换路径——调用方始终拿到非空转换器，无需对 from==to 特判。
/// </summary>
internal sealed class IdentityConverter : IRequestConverter, INonStreamingResponseConverter, IStreamingResponseConverter
{
    public static readonly IdentityConverter Instance = new();

    public string Convert(string body) => body;

    public byte[] Process(ReadOnlySpan<byte> chunk) => chunk.ToArray();

    public byte[] Flush() => Array.Empty<byte>();

    public void Dispose()
    {
        // 无状态，无资源需释放
    }
}

/// <summary>
/// 按 (源格式, 目标格式) 选择转换器。
/// 源=下游格式、目标=客户端格式（响应方向）；源=客户端格式、目标=下游格式（请求方向）。
/// from==to 时返回 <see cref="IdentityConverter"/>（透传），保证调用方始终拿到非空转换器，无需特判。
/// 跨格式转换器实例均为无状态单例，由 DI 注入；流式转换器为有状态，每次 CreateStreaming 新建实例（identity 除外，单例复用）。
/// </summary>
public sealed class FormatConverterRegistry
{
    private readonly AnthropicToOpenAiRequestConverter _a2oReq;
    private readonly OpenAiToAnthropicRequestConverter _o2aReq;
    private readonly AnthropicToOpenAiResponseConverter _a2oResp;
    private readonly OpenAiToAnthropicResponseConverter _o2aResp;

    public FormatConverterRegistry(
        AnthropicToOpenAiRequestConverter a2oReq,
        OpenAiToAnthropicRequestConverter o2aReq,
        AnthropicToOpenAiResponseConverter a2oResp,
        OpenAiToAnthropicResponseConverter o2aResp)
    {
        _a2oReq = a2oReq;
        _o2aReq = o2aReq;
        _a2oResp = a2oResp;
        _o2aResp = o2aResp;
    }

    /// <summary>请求转换器（客户端格式 → 下游格式）；from==to 返回 identity 透传器。</summary>
    public IRequestConverter ResolveRequest(ServiceFormat from, ServiceFormat to)
        => (from, to) switch
        {
            (ServiceFormat.Anthropic, ServiceFormat.OpenAI) => _a2oReq,
            (ServiceFormat.OpenAI, ServiceFormat.Anthropic) => _o2aReq,
            _ => IdentityConverter.Instance
        };

    /// <summary>非流式响应转换器（下游格式 → 客户端格式）；from==to 返回 identity 透传器。</summary>
    public INonStreamingResponseConverter ResolveNonStream(ServiceFormat from, ServiceFormat to)
        => (from, to) switch
        {
            (ServiceFormat.Anthropic, ServiceFormat.OpenAI) => _a2oResp,
            (ServiceFormat.OpenAI, ServiceFormat.Anthropic) => _o2aResp,
            _ => IdentityConverter.Instance
        };

    /// <summary>新建流式响应转换器实例（下游格式 → 客户端格式）；from==to 返回 identity 透传器（单例复用）。</summary>
    public IStreamingResponseConverter CreateStreaming(ServiceFormat from, ServiceFormat to)
        => (from, to) switch
        {
            (ServiceFormat.Anthropic, ServiceFormat.OpenAI) => _a2oResp.CreateStreaming(),
            (ServiceFormat.OpenAI, ServiceFormat.Anthropic) => _o2aResp.CreateStreaming(),
            _ => IdentityConverter.Instance
        };
}
