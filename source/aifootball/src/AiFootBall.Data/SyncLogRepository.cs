using Dapper;

namespace AiFootBall.Data;

/// <summary>同步/作业运行日志（dbo.SyncLog）。</summary>
public sealed class SyncLogRepository
{
    private readonly SqlConnectionFactory _factory;

    public SyncLogRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// 列宽上限：Message nvarchar(1000)、TaskName/Source nvarchar(100)。
    ///
    /// 必须在这里截断，不能指望调用方自觉。超长时 SQL Server 直接抛 2628
    /// 「字符串或二进制数据将被截断」，INSERT 整个失败——而最可能写出超长消息的时刻，
    /// 恰恰是正在拼接多个失败原因的时候（旁路结算失败 / 两段采集同时失败）。
    /// 也就是说：**最需要留下记录的那一刻，日志自己先写不进去**。
    /// </summary>
    private const int MaxTaskLen = 100;
    private const int MaxMessageLen = 1000;

    /// <summary>按列宽截断。public 是为了让另一处直写 SyncLog 的代码（LotteryRepository）复用同一份实现。</summary>
    public static string? Clip(string? s, int max)
        => s is null || s.Length <= max ? s : s[..(max - 1)] + "…";

    public static string? ClipTask(string? s) => Clip(s, MaxTaskLen);
    public static string? ClipMessage(string? s) => Clip(s, MaxMessageLen);

    public async Task<long> WriteAsync(string task, string? source, string status, int? rows, string? message,
        CancellationToken ct = default, DateTime? startedAt = null)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(
            // StartedAt 必须显式写。
            //
            // 2026-11-10 之前这里只写 FinishedAt，StartedAt 靠表上的默认约束兜底，
            // 于是每一行的 StartedAt 都精确等于 FinishedAt —— 全库 12733 行，
            // DATEDIFF 秒数全部为 0。后果不是「少一个字段」，而是
            // **作业真实耗时在任何地方都不存在**：通道被谁占住、哪一轮真的跑了 8 分钟，
            // 事后一律查不出来，只能靠 STUCK 条数猜。
            // 调度器明明手里就有开始时刻（entry.RunningSince），却没传下来。
            @"INSERT INTO dbo.SyncLog (TaskName, Source, StartedAt, FinishedAt, Status, RowsAffected, Message)
              VALUES (@task, @source, ISNULL(@startedAt, SYSDATETIME()), SYSDATETIME(), @status, @rows, @message);
              SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new
            {
                task = ClipTask(task),
                source = ClipTask(source),
                startedAt,
                status,
                rows,
                message = ClipMessage(message)
            });
    }

    /// <summary>
    /// 各作业最后一次运行（作业名 → 完成时间 + 状态 + 消息）。
    ///
    /// 2026-11-09：此前只恢复 FinishedAt，状态是纯内存字段，进程一重启就全变成 null，
    /// 「作业健康度」面板和自检 4c 段在部署后全部失明——2026-11-09 09:26 部署后
    /// /api/jobs 里 10 个作业 lastStatus 全空。状态本来就在 SyncLog 里写着，没必要丢。
    ///
    /// 用 ROW_NUMBER 而不是 MAX(FinishedAt)：同一作业可能在一秒内写多行（TIMEOUT 之后紧跟 SUCCESS），
    /// 按时间取最新会拿到不确定的一行。LogId 是 IDENTITY，严格单调。
    /// </summary>
    public async Task<Dictionary<string, LastRunInfo>> GetLastRunsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<LastRunInfo>(
            @"WITH x AS (
                  SELECT TaskName, FinishedAt, Status, Message,
                         ROW_NUMBER() OVER (PARTITION BY TaskName ORDER BY LogId DESC) AS rn
                  FROM dbo.SyncLog
                  WHERE TaskName LIKE 'job:%'
              )
              SELECT TaskName, FinishedAt, Status, Message FROM x WHERE rn = 1");
        return rows.GroupBy(r => r.TaskName).ToDictionary(g => g.Key, g => g.First());
    }

    public sealed class LastRunInfo
    {
        public string TaskName { get; set; } = "";
        public DateTime? FinishedAt { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
    }

    public async Task<List<dynamic>> GetRecentAsync(int take = 50, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync(
            @"SELECT TOP (@take) LogId, TaskName, Source, StartedAt, FinishedAt, Status, RowsAffected, Message
              FROM dbo.SyncLog ORDER BY LogId DESC", new { take });
        return rows.ToList();
    }
}
