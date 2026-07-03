namespace AiProxy.Config;

/// <summary>
/// 应用根配置：包含全局代理设置与下游 AI 服务列表。
/// 通过 IOptionsMonitor&lt;AppOptions&gt; 绑定（reloadOnChange: true），配置文件变更后对新请求即时生效。
/// 注意：ListenAddress / ListenPort / LogDbPath 在启动时固定（驱动 Kestrel 监听与 DB 路径），
/// 运行时仅 AiServices 与 GlobalApiKey 的变更对转发/鉴权路径生效。
/// </summary>
public sealed class AppOptions
{
    /// <summary>全局代理设置</summary>
    public ProxyOptions Proxy { get; set; } = new();

    /// <summary>下游 AI 服务列表（一 URL 前缀绑一下游）</summary>
    public List<AiServiceOptions> AiServices { get; set; } = new();
}
