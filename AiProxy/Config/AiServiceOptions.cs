namespace AiProxy.Config;

/// <summary>
/// 模型映射规则：将请求体中的 model 字段按 <see cref="Pattern"/> 通配符匹配并替换为 <see cref="Replacement"/>。
/// 转发流程在格式转换后、写回请求体前应用：按列表顺序首次命中即替换并停止；空列表表示不替换。
/// </summary>
public sealed class ModelMappingOptions
{
    /// <summary>
    /// 通配符模式：<c>*</c> 匹配任意数量字符（含空），<c>?</c> 匹配单个字符，其余字符原义匹配。
    /// 模式始终锚定全串（不会子串命中）。空字符串视为合法但永不匹配。
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// 替换值。模式命中时将 model 字段直接替换为该值（不支持反向引用）。
    /// </summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>是否启用本条映射；false 时跳过。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 是否区分大小写（默认 true）。false 时模式与 model 字段的比较忽略大小写。
    /// </summary>
    public bool CaseSensitive { get; set; } = true;
}

/// <summary>
/// 下游 AI 服务的接口协议格式。
/// 决定鉴权方式、默认请求头、以及响应解析逻辑。
/// </summary>
public enum ServiceFormat
{
    /// <summary>
    /// OpenAI 兼容格式（默认）。
    /// 鉴权：Authorization: Bearer &lt;ApiKey&gt;
    /// 响应解析：usage.prompt_tokens / completion_tokens / total_tokens
    /// 流式：choices[0].delta.content
    /// 适用于 OpenAI / Azure / 国产大模型（智谱、通义、Moonshot 等）。
    /// </summary>
    OpenAI = 0,

    /// <summary>
    /// Anthropic 原生格式（Claude API）。
    /// 鉴权：x-api-key: &lt;ApiKey&gt;
    /// 默认注入头：anthropic-version: 2023-06-01（可通过 ExtraHeaders 覆盖）
    /// 响应解析：usage.input_tokens / output_tokens
    /// 流式：content_block_delta → delta.text，message_delta → usage.output_tokens
    /// </summary>
    Anthropic = 1
}

/// <summary>
/// 单个下游 AI 服务配置。
/// 一个 URL 路径前缀（PathPrefix）唯一对应一个下游服务（BaseUrl + ApiKey）。
/// 客户端 base_url = http://&lt;ListenAddress&gt;:&lt;ListenPort&gt;/&lt;PathPrefix&gt;，
/// 代理剥离首段前缀后转发至 BaseUrl（含版本路径前缀，如 https://api.openai.com/v1）。
/// </summary>
public sealed class AiServiceOptions
{
    /// <summary>下游服务唯一标识名称（用于日志、统计、管理面板 CRUD 主键）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL 路径前缀（路由键）。客户端请求 /&lt;PathPrefix&gt;/chat/completions 时匹配本服务。
    /// 约定：干净路径段 ^[a-zA-Z0-9_-]+$，且非保留段（api / v1）。唯一。
    /// </summary>
    public string PathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// 下游 AI 服务接口基地址（含版本路径前缀，如 https://api.openai.com/v1、
    /// https://open.bigmodel.cn/api/paas/v1、https://api.anthropic.com/v1）。
    /// 转发时拼接：BaseUrl + 剥离前缀后的剩余路径（端点名，如 /chat/completions）+ QueryString。
    /// 客户端调用示例：http://&lt;host&gt;:&lt;port&gt;/&lt;PathPrefix&gt;/chat/completions
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 下游服务真实密钥。转发时根据 ServiceFormat 注入相应鉴权头。
    /// 为空字符串时跳过鉴权头注入（如 Ollama）。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 下游服务接口格式（默认 OpenAI）。
    /// - OpenAI：鉴权 Authorization: Bearer，响应解析 OpenAI 格式
    /// - Anthropic：鉴权 x-api-key，自动注入 anthropic-version 头，响应解析 Anthropic 格式
    /// </summary>
    public ServiceFormat ServiceFormat { get; set; } = ServiceFormat.OpenAI;

    /// <summary>
    /// 客户端请求/期望响应的格式。null（默认）= 自动模式：按请求鉴权头逐请求推断
    /// （Authorization: Bearer → OpenAI，x-api-key → Anthropic，均无 → OpenAI）。
    /// 显式指定时与 <see cref="ServiceFormat"/> 不同则启用双向转换：
    ///   请求体由客户端格式转为 <see cref="ServiceFormat"/> 转发，
    ///   响应体由 <see cref="ServiceFormat"/> 转回客户端格式返回。
    /// 相同（或自动推断结果相同）时走 identity 透传。
    /// 例如 <see cref="ServiceFormat"/>=OpenAI、ClientFormat=Anthropic：
    ///   客户端发 Anthropic → 转 OpenAI 转发 → 响应转回 Anthropic 返回。
    /// </summary>
    public ServiceFormat? ClientFormat { get; set; } = null;

    /// <summary>
    /// 转发请求时附加的自定义请求头。
    /// 键值对形式，转发前注入到下游请求中。为空或 null 时不附加任何额外头。
    /// 对于 Anthropic 格式，若未在此指定 anthropic-version，将自动注入默认值 "2023-06-01"。
    /// </summary>
    public Dictionary<string, string>? ExtraHeaders { get; set; }

    /// <summary>是否记录完整请求体（默认 true）</summary>
    public bool LogRequestBody { get; set; } = true;

    /// <summary>是否记录完整响应体（默认 true）</summary>
    public bool LogResponseBody { get; set; } = true;

    /// <summary>是否允许无效 SSL 证书（默认 false）。当下游服务使用自签名证书或证书验证失败时设为 true。</summary>
    public bool AllowInvalidSslCertificates { get; set; } = false;

    /// <summary>
    /// 有序模型映射列表：转发时对请求体 model 字段按顺序首次匹配并替换。
    /// 在格式转换后、写回请求体前应用。空列表（默认）表示不替换。
    /// </summary>
    public List<ModelMappingOptions> ModelMappings { get; set; } = new();
}
