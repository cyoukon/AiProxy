namespace AiProxy.Config;

/// <summary>
/// 全局代理配置。
/// 对应 appsettings.json 中 "Proxy" 节点。
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>
    /// 全局代理访问密钥。客户端通过 Authorization: Bearer &lt;GlobalApiKey&gt; 访问业务端口。
    /// 为空字符串时关闭全局鉴权（本地无鉴权）。
    /// 经管理面板修改后写回 appsettings.json，IOptionsMonitor 自动 reload，对新请求即时生效。
    /// </summary>
    public string GlobalApiKey { get; set; } = string.Empty;

    /// <summary>
    /// SQLite 日志文件持久化路径。默认 "./logs/ai-proxy.db"。
    /// </summary>
    public string LogDbPath { get; set; } = "./logs/ai-proxy.db";

    /// <summary>
    /// Kestrel 监听地址。默认 "localhost"（仅 loopback，本地工具定位）。
    /// 支持：
    /// - "localhost" / "127.0.0.1" / "::1" → ListenLocalhost（IPv4+IPv6 loopback）
    /// - "*" / "0.0.0.0" / "[::]" → ListenAnyIP（任意网卡，容器/局域网部署）
    /// - 具体 IP（如 "192.168.1.10"）→ Listen 该 IP
    /// 启动时固定，修改需重启进程。
    /// </summary>
    public string ListenAddress { get; set; } = "localhost";

    /// <summary>
    /// Kestrel 监听端口（业务与管理共享同一端口）。默认 8000。
    /// 业务请求：http://&lt;ListenAddress&gt;:&lt;ListenPort&gt;/&lt;prefix&gt;/v1/...
    /// 管理面板：http://&lt;ListenAddress&gt;:&lt;ListenPort&gt;/
    /// 管理 API：http://&lt;ListenAddress&gt;:&lt;ListenPort&gt;/api/...
    /// 启动时固定，修改需重启进程。
    /// </summary>
    public int ListenPort { get; set; } = 8000;
}
