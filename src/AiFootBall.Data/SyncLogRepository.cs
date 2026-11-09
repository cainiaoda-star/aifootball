using Dapper;

namespace AiFootBall.Data;

/// <summary>同步/作业运行日志（dbo.SyncLog）。</summary>
public sealed class SyncLogRepository
{
    private readonly SqlConnectionFactory _factory;

    public SyncLogRepository(SqlConnectionFactory factory) => _factory = factory;

    public async Task<long> WriteAsync(string task, string? source, string status, int? rows, string? message,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(
            @"INSERT INTO dbo.SyncLog (TaskName, Source, FinishedAt, Status, RowsAffected, Message)
              VALUES (@task, @source, SYSDATETIME(), @status, @rows, @message);
              SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { task, source, status, rows, message });
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
