using AiProxy.Data;
using Microsoft.Extensions.Logging;

namespace AiProxy.Util;

/// <summary>
/// 控制台实时调用摘要输出。形如：
/// [HH:mm:ss] service=openai-official model=gpt-4o status=200 duration=1234ms tokens=10/20/30
/// </summary>
public sealed class ConsoleReporter
{
    private readonly ILogger<ConsoleReporter> _logger;

    public ConsoleReporter(ILogger<ConsoleReporter> logger)
    {
        _logger = logger;
    }

    public void Report(RequestLog log)
    {
        var tokenPart = (log.PromptTokens.HasValue || log.CompletionTokens.HasValue || log.TotalTokens.HasValue)
            ? $" tokens={log.PromptTokens ?? 0}/{log.CompletionTokens ?? 0}/{log.TotalTokens ?? 0}"
            : string.Empty;
        var modelPart = string.IsNullOrEmpty(log.Model) ? string.Empty : $" model={log.Model}";
        var convertPart = log.IsConverted ? $" [{log.ClientFormat}→{log.ServiceFormat}]" : string.Empty;

        _logger.LogInformation("service={ServiceName}{ModelPart} status={StatusCode} duration={DurationMs}ms{TokenPart}{ConvertPart}",
            log.ServiceName, modelPart, log.StatusCode, log.DurationMs, tokenPart, convertPart);
    }
}