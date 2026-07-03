using Microsoft.Extensions.Configuration;

namespace AiProxy.Services;

/// <summary>
/// 应用配置根的包装（单例）。
/// 持有 IOptionsMonitor 绑定的 IConfigurationRoot 实例，供 ConfigService 写配置后强制 Reload。
/// 使用独立类型避免与 ASP.NET Core 框架自带的 IConfigurationRoot 注册冲突。
/// </summary>
public sealed class AppConfigRoot
{
    public IConfigurationRoot Root { get; }

    public AppConfigRoot(IConfigurationRoot root)
    {
        Root = root;
    }

    /// <summary>强制重载配置，使 IOptionsMonitor 立即反映最新值</summary>
    public void Reload() => Root.Reload();
}
