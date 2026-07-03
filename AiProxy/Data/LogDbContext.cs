using Microsoft.EntityFrameworkCore;

namespace AiProxy.Data;

/// <summary>
/// 日志 DbContext：单表 RequestLogs，SQLite 持久化。
/// </summary>
public sealed class LogDbContext : DbContext
{
    public LogDbContext(DbContextOptions<LogDbContext> options)
        : base(options)
    {
    }

    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var e = modelBuilder.Entity<RequestLog>();
        e.ToTable("RequestLogs");
        e.HasKey(x => x.Id);
        e.Property(x => x.RequestTime).HasColumnType("TEXT");
        e.Property(x => x.ServiceName).HasMaxLength(128);
        e.Property(x => x.ClientPath).HasMaxLength(2048);
        e.Property(x => x.DownstreamUrl).HasMaxLength(2048);
        e.Property(x => x.Method).HasMaxLength(16);
        e.Property(x => x.ErrorType).HasMaxLength(32);
        e.Property(x => x.Model).HasMaxLength(128);
        e.Property(x => x.ClientFormat).HasMaxLength(32);
        e.Property(x => x.ServiceFormat).HasMaxLength(32);

        // 关键索引
        e.HasIndex(x => x.RequestTime);
        e.HasIndex(x => x.ServiceName);
        e.HasIndex(x => x.Model);
    }

    /// <summary>根据 LogDbPath 构建 SQLite 连接字符串，并确保目录存在</summary>
    public static string BuildConnectionString(string logDbPath)
    {
        var path = string.IsNullOrWhiteSpace(logDbPath) ? "./logs/ai-proxy.db" : logDbPath;
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return $"Data Source={fullPath}";
    }
}
