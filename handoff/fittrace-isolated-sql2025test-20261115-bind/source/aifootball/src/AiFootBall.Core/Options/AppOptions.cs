namespace AiFootBall.Core.Options;

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = "";
    public string Database { get; set; } = "AiFootBall";
}

public sealed class CollectorOptions
{
    /// <summary>每次请求间隔（毫秒），避免对站点造成压力</summary>
    public int RequestDelayMs { get; set; } = 800;
    public int TimeoutSeconds { get; set; } = 60;
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    /// <summary>足球回溯天数（含今天）</summary>
    public int FootballBackfillDays { get; set; } = 7;

    /// <summary>赔率请求随机间隔下限（毫秒）</summary>
    public int OddsDelayMinMs { get; set; } = 400;
    /// <summary>赔率请求随机间隔上限（毫秒）</summary>
    public int OddsDelayMaxMs { get; set; } = 900;
    /// <summary>赔率采集并发数（同时处理的赛事数）</summary>
    public int OddsConcurrency { get; set; } = 3;
    /// <summary>赔率批量落库的赛事数</summary>
    public int OddsBatchSize { get; set; } = 150;
}

/// <summary>定时作业定义</summary>
public sealed class ScheduledJob
{
    public string Name { get; set; } = "";
    /// <summary>标准 5 段 cron：分 时 日 月 周</summary>
    public string Cron { get; set; } = "";
    /// <summary>lotto / football / odds-upcoming / odds-sample / odds-sample-late / odds-backfill / exchange</summary>
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>lotto：留空表示全部彩种</summary>
    public string? LotteryCode { get; set; }

    /// <summary>football：回溯天数</summary>
    public int Days { get; set; } = 3;

    /// <summary>odds-upcoming / odds-sample / exchange：抓取未来多少小时内的赛事</summary>
    public int Hours { get; set; } = 24;

    /// <summary>odds-sample / exchange：同时包含多少小时前已开赛的场次（捕获开赛瞬间与滚球初段）</summary>
    public double HoursBehind { get; set; } = 1;

    /// <summary>odds-sample / exchange：同一场两次采样的最小间隔（分钟）</summary>
    public int RecheckMinutes { get; set; } = 25;

    /// <summary>单次处理场次上限（0=不限）</summary>
    public int Limit { get; set; }
}

public sealed class JobsOptions
{
    /// <summary>是否在 API 进程内启用调度</summary>
    public bool Enabled { get; set; } = true;
    public List<ScheduledJob> Jobs { get; set; } = new();
}
