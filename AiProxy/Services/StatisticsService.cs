using AiProxy.Data;
using AiProxy.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AiProxy.Services;

/// <summary>
/// 端口级用量统计服务：基于 SQLite RequestLogs 表实时聚合。
/// 支持两个维度（按服务/按模型）与两种时间粒度（按日/累计）。
/// 所有聚合尽量在数据库侧完成（GroupBy + Sum/Average/Count），避免拉全表到内存。
/// </summary>
public sealed class StatisticsService
{
    private readonly LogDbContext _db;

    public StatisticsService(LogDbContext db)
    {
        _db = db;
    }

    public Task<StatisticsResponse> GetByServiceAsync(StatsQuery q, CancellationToken ct = default)
        => GetAsync(StatsDimension.Service, q, ct);

    public Task<StatisticsResponse> GetByModelAsync(StatsQuery q, CancellationToken ct = default)
        => GetAsync(StatsDimension.Model, q, ct);

    private async Task<StatisticsResponse> GetAsync(StatsDimension dim, StatsQuery q, CancellationToken ct)
    {
        var query = _db.RequestLogs.AsNoTracking();
        if (q.From.HasValue) query = query.Where(r => r.RequestTime >= q.From.Value);
        if (q.To.HasValue) query = query.Where(r => r.RequestTime <= q.To.Value);
        if (!string.IsNullOrEmpty(q.Service)) query = query.Where(r => r.ServiceName == q.Service);
        if (!string.IsNullOrEmpty(q.Model)) query = query.Where(r => r.Model == q.Model);

        return dim == StatsDimension.Service
            ? await GetByServiceAsync(query, q.Granularity, ct)
            : await GetByModelAsync(query, q.Granularity, ct);
    }

    /// <summary>按服务维度聚合：分组键 = ServiceName（单端口部署下 ListenPort 恒定，不再作为分组键）</summary>
    private static async Task<StatisticsResponse> GetByServiceAsync(IQueryable<RequestLog> query, StatsGranularity g, CancellationToken ct)
    {
        // 累计分组汇总
        var rawGroups = await query
            .GroupBy(r => new { r.ServiceName })
            .Select(g => new
            {
                g.Key.ServiceName,
                TotalCalls = g.Count(),
                SuccessCount = g.Count(r => r.IsSuccess),
                FailureCount = g.Count(r => !r.IsSuccess),
                PromptTokensTotal = g.Sum(r => (long)(r.PromptTokens ?? 0)),
                CompletionTokensTotal = g.Sum(r => (long)(r.CompletionTokens ?? 0)),
                TotalTokensTotal = g.Sum(r => (long)(r.TotalTokens ?? 0)),
                AvgDurationMs = g.Average(r => (double)r.DurationMs)
            })
            .ToListAsync(ct);

        var groups = rawGroups
            .OrderBy(x => x.ServiceName)
            .Select(x => new StatisticsGroupDto
            {
                Key = x.ServiceName,
                ListenPort = 0,
                ServiceName = x.ServiceName,
                TotalCalls = x.TotalCalls,
                SuccessCount = x.SuccessCount,
                FailureCount = x.FailureCount,
                PromptTokensTotal = x.PromptTokensTotal,
                CompletionTokensTotal = x.CompletionTokensTotal,
                TotalTokensTotal = x.TotalTokensTotal,
                AvgDurationMs = Math.Round(x.AvgDurationMs, 2)
            })
            .ToList();

        List<DailySeriesDto>? series = null;
        if (g == StatsGranularity.Daily)
        {
            // SQLite 支持 r.RequestTime.Date 翻译为 date(...) 函数
            var rawDaily = await query
                .GroupBy(r => new { r.ServiceName, Date = r.RequestTime.Date })
                .Select(g => new
                {
                    g.Key.ServiceName,
                    g.Key.Date,
                    TotalCalls = g.Count(),
                    SuccessCount = g.Count(r => r.IsSuccess),
                    FailureCount = g.Count(r => !r.IsSuccess),
                    PromptTokensTotal = g.Sum(r => (long)(r.PromptTokens ?? 0)),
                    CompletionTokensTotal = g.Sum(r => (long)(r.CompletionTokens ?? 0)),
                    TotalTokensTotal = g.Sum(r => (long)(r.TotalTokens ?? 0)),
                    AvgDurationMs = g.Average(r => (double)r.DurationMs)
                })
                .OrderBy(x => x.Date)
                .ToListAsync(ct);

            series = rawDaily
                .GroupBy(x => x.ServiceName)
                .OrderBy(g => g.Key)
                .Select(g => new DailySeriesDto
                {
                    Key = g.Key,
                    ListenPort = 0,
                    Buckets = g.Select(x => new DailyBucketDto
                    {
                        Date = x.Date,
                        TotalCalls = x.TotalCalls,
                        SuccessCount = x.SuccessCount,
                        FailureCount = x.FailureCount,
                        PromptTokensTotal = x.PromptTokensTotal,
                        CompletionTokensTotal = x.CompletionTokensTotal,
                        TotalTokensTotal = x.TotalTokensTotal,
                        AvgDurationMs = Math.Round(x.AvgDurationMs, 2)
                    }).ToList()
                })
                .ToList();
        }

        return new StatisticsResponse
        {
            Dimension = StatsDimension.Service,
            Granularity = g,
            Groups = groups,
            Series = series
        };
    }

    /// <summary>按模型维度聚合：分组键 = Model（空/null 归一为 "unknown"）</summary>
    private static async Task<StatisticsResponse> GetByModelAsync(IQueryable<RequestLog> query, StatsGranularity g, CancellationToken ct)
    {
        // 先投影出归一化模型键，再分组聚合。三元运算符翻译为 CASE WHEN，SQLite 支持。
        var projected = query.Select(r => new
        {
            ModelKey = (r.Model == null || r.Model == "") ? "unknown" : r.Model,
            r.IsSuccess,
            r.PromptTokens,
            r.CompletionTokens,
            r.TotalTokens,
            r.DurationMs,
            Date = r.RequestTime.Date
        });

        var rawGroups = await projected
            .GroupBy(r => r.ModelKey)
            .Select(g => new
            {
                g.Key,
                TotalCalls = g.Count(),
                SuccessCount = g.Count(r => r.IsSuccess),
                FailureCount = g.Count(r => !r.IsSuccess),
                PromptTokensTotal = g.Sum(r => (long)(r.PromptTokens ?? 0)),
                CompletionTokensTotal = g.Sum(r => (long)(r.CompletionTokens ?? 0)),
                TotalTokensTotal = g.Sum(r => (long)(r.TotalTokens ?? 0)),
                AvgDurationMs = g.Average(r => (double)r.DurationMs)
            })
            .ToListAsync(ct);

        var groups = rawGroups
            .OrderBy(x => x.Key)
            .Select(x => new StatisticsGroupDto
            {
                Key = x.Key,
                ListenPort = 0,
                ServiceName = x.Key,
                TotalCalls = x.TotalCalls,
                SuccessCount = x.SuccessCount,
                FailureCount = x.FailureCount,
                PromptTokensTotal = x.PromptTokensTotal,
                CompletionTokensTotal = x.CompletionTokensTotal,
                TotalTokensTotal = x.TotalTokensTotal,
                AvgDurationMs = Math.Round(x.AvgDurationMs, 2)
            })
            .ToList();

        List<DailySeriesDto>? series = null;
        if (g == StatsGranularity.Daily)
        {
            var rawDaily = await projected
                .GroupBy(r => new { r.ModelKey, r.Date })
                .Select(g => new
                {
                    g.Key.ModelKey,
                    g.Key.Date,
                    TotalCalls = g.Count(),
                    SuccessCount = g.Count(r => r.IsSuccess),
                    FailureCount = g.Count(r => !r.IsSuccess),
                    PromptTokensTotal = g.Sum(r => (long)(r.PromptTokens ?? 0)),
                    CompletionTokensTotal = g.Sum(r => (long)(r.CompletionTokens ?? 0)),
                    TotalTokensTotal = g.Sum(r => (long)(r.TotalTokens ?? 0)),
                    AvgDurationMs = g.Average(r => (double)r.DurationMs)
                })
                .OrderBy(x => x.Date)
                .ToListAsync(ct);

            series = rawDaily
                .GroupBy(x => x.ModelKey)
                .OrderBy(g => g.Key)
                .Select(g => new DailySeriesDto
                {
                    Key = g.Key,
                    ListenPort = 0,
                    Buckets = g.Select(x => new DailyBucketDto
                    {
                        Date = x.Date,
                        TotalCalls = x.TotalCalls,
                        SuccessCount = x.SuccessCount,
                        FailureCount = x.FailureCount,
                        PromptTokensTotal = x.PromptTokensTotal,
                        CompletionTokensTotal = x.CompletionTokensTotal,
                        TotalTokensTotal = x.TotalTokensTotal,
                        AvgDurationMs = Math.Round(x.AvgDurationMs, 2)
                    }).ToList()
                })
                .ToList();
        }

        return new StatisticsResponse
        {
            Dimension = StatsDimension.Model,
            Granularity = g,
            Groups = groups,
            Series = series
        };
    }
}
