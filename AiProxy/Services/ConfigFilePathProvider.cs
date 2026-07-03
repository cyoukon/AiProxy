namespace AiProxy.Services;

/// <summary>
/// 配置文件绝对路径提供者（单例）。
/// 启动时由 Program.cs 注册，存 appsettings.json 的绝对路径，供 ConfigService 读写。
/// </summary>
public sealed class ConfigFilePathProvider
{
    /// <summary>配置文件绝对路径（由 --config 参数解析，默认 appsettings.json）</summary>
    public string AbsolutePath { get; }

    public ConfigFilePathProvider(string absolutePath)
    {
        AbsolutePath = absolutePath;
    }
}
