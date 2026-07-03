namespace AiProxy.Dtos;

/// <summary>统计时间粒度：按日 / 累计</summary>
public enum StatsGranularity
{
    Daily,
    Cumulative
}

/// <summary>统计维度：按服务（端口） / 按模型</summary>
public enum StatsDimension
{
    Service,
    Model
}

/// <summary>
/// 用量统计查询参数。所有字段可空，未指定则不过滤。
/// From/To 均为 UTC。
/// </summary>
public sealed record StatsQuery
{
    public StatsGranularity Granularity { get; init; } = StatsGranularity.Cumulative;
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    /// <summary>按监听端口过滤（兼容旧前端，单端口部署下意义有限）</summary>
    public int? Port { get; init; }
    /// <summary>按服务名过滤（单端口 + 前缀路由下的主要过滤维度）</summary>
    public string? Service { get; init; }
    public string? Model { get; init; }
}

/// <summary>
/// 单个统计分组的聚合结果。
/// Service 维度：Key=ServiceName、ListenPort 真实端口、ServiceName 真实服务名。
/// Model 维度：Key=Model（空模型归一为 "unknown"）、ListenPort=0、ServiceName=Model。
/// </summary>
public sealed record StatisticsGroupDto
{
    public string Key { get; init; } = string.Empty;
    public int ListenPort { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public long TotalCalls { get; init; }
    public long SuccessCount { get; init; }
    public long FailureCount { get; init; }
    public long PromptTokensTotal { get; init; }
    public long CompletionTokensTotal { get; init; }
    public long TotalTokensTotal { get; init; }
    public double AvgDurationMs { get; init; }
}

/// <summary>单个日桶的聚合指标（不含分组键，由 DailySeriesDto 携带）</summary>
public sealed record DailyBucketDto
{
    public DateTime Date { get; init; }
    public long TotalCalls { get; init; }
    public long SuccessCount { get; init; }
    public long FailureCount { get; init; }
    public long PromptTokensTotal { get; init; }
    public long CompletionTokensTotal { get; init; }
    public long TotalTokensTotal { get; init; }
    public double AvgDurationMs { get; init; }
}

/// <summary>按日粒度时的单条时间序列：一个分组对应一组日桶</summary>
public sealed record DailySeriesDto
{
    public string Key { get; init; } = string.Empty;
    public int ListenPort { get; init; }
    public List<DailyBucketDto> Buckets { get; init; } = new();
}

/// <summary>
/// 统计接口响应。
/// - Cumulative：Groups 含按分组汇总数据，Series=null。
/// - Daily：Groups 仍为时间区间汇总（用于表格），Series 为按分组的日桶序列（用于趋势图）。
/// </summary>
public sealed record StatisticsResponse
{
    public StatsDimension Dimension { get; init; }
    public StatsGranularity Granularity { get; init; }
    public List<StatisticsGroupDto> Groups { get; init; } = new();
    public List<DailySeriesDto>? Series { get; init; }
}
