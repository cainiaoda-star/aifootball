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

    /// <summary>各作业最后一次运行时间（作业名 → 完成时间）</summary>
    public async Task<Dictionary<string, DateTime>> GetLastRunsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<LastRunRow>(
            @"SELECT TaskName, MAX(FinishedAt) AS FinishedAt
              FROM dbo.SyncLog
              WHERE TaskName LIKE 'job:%' AND FinishedAt IS NOT NULL
              GROUP BY TaskName");
        return rows.Where(r => r.FinishedAt != default)
                   .GroupBy(r => r.TaskName)
                   .ToDictionary(g => g.Key, g => g.Max(x => x.FinishedAt));
    }

    private sealed class LastRunRow
    {
        public string TaskName { get; set; } = "";
        public DateTime FinishedAt { get; set; }
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
