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

    /// <summary>
    /// Status 是 <c>varchar(10)</c>。写成常量而不是散落的字面量，是为了让测试能钉住长度：
    /// 超长会被 SQL Server 以 2628 拒绝整条 INSERT，而这条 INSERT 正是「这次跑完了没有」
    /// 的唯一凭据——最需要它的时候它自己先失败。
    /// </summary>
    public const string StatusRunning = "RUNNING";
    public const string StatusAborted = "ABORTED";
    /// <summary>和 dbo.SyncLog.Status 的列宽一致。</summary>
    public const int StatusColumnMax = 10;

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
    /// 开始执行：先落一行 <c>RUNNING</c>，返回行号，结束时由 <see cref="CompleteAsync"/> 回填。
    ///
    /// 2026-11-10 加。在这之前只有「结束时插一行」这一个动作，于是**跑到一半死掉的作业
    /// 在库里连痕迹都没有**：12:21:51 启动的 odds-backfill 在 12:40 随进程重启被掐断，
    /// SyncLog 里一行都没有，/api/jobs 仍然显示上一次成功（03:55 SUCCESS）和
    /// 「下次明天 03:20」—— 看板全绿，而当天唯一一次历史赔率回补已经没了。
    ///
    /// 写不进去不能挡住作业本身：日志是旁路，返回 null 让调用方退回「只写完成行」的老路。
    /// </summary>
    public async Task<long?> BeginAsync(string task, string? source, DateTime? startedAt,
        CancellationToken ct = default)
    {
        try
        {
            // 单独限时。DB 不响应时不能把「作业启动」卡在这里——
            // 这个项目已经在「持着通道等数据库」上停摆过不止一次。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await using var conn = await _factory.OpenAsync(cts.Token);
            return await conn.ExecuteScalarAsync<long>(
                $@"INSERT INTO dbo.SyncLog (TaskName, Source, StartedAt, FinishedAt, Status, RowsAffected, Message)
                  VALUES (@task, @source, ISNULL(@startedAt, SYSDATETIME()), NULL, '{StatusRunning}', NULL, NULL);
                  SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { task = ClipTask(task), source = ClipTask(source), startedAt },
                commandTimeout: 10);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 回填开始行的结论。拿不到开始行（写失败 / 老进程）就退回插一整行——
    /// 结论比形式重要，绝不能因为「没有开始行」把这次运行也一起丢掉。
    /// </summary>
    public async Task CompleteAsync(long? logId, string task, string? source, string status,
        int? rows, string? message, CancellationToken ct = default, DateTime? startedAt = null)
    {
        // Status 是 varchar(10)：RUNNING / ABORTED 都在界内，但写成常量让人一眼能核。
        if (logId is null)
        {
            await WriteAsync(task, source, status, rows, message, ct, startedAt);
            return;
        }

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            @"UPDATE dbo.SyncLog
              SET Status = @status, RowsAffected = @rows, Message = @message, FinishedAt = SYSDATETIME()
              WHERE LogId = @id",
            new { id = logId, status, rows, message = ClipMessage(message) });
    }

    /// <summary>
    /// 进程启动时，把上一个进程遗留的 <c>RUNNING</c> 行标成 <c>ABORTED</c>。
    ///
    /// 「上一进程遗留」这个前提靠**单实例**成立：同一时刻只有一个进程在跑调度循环
    /// （Api 的 SchedulerHostedService）。命令行 `scheduler --once X` 只调 RunOnceAsync
    /// 不调 RunAsync，不会触发清理，也就不会误伤正跑着的作业。
    ///
    /// FinishedAt 故意留 NULL：真实完成时刻并不存在，填上清扫时刻会让
    /// 「耗时 = FinishedAt - StartedAt」凭空多出一个假的执行时长。
    /// </summary>
    public async Task<int> SweepAbandonedAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            $@"UPDATE dbo.SyncLog
              SET Status = '{StatusAborted}',
                  Message = CONCAT('进程在本次执行期间退出（开始于 ',
                                   CONVERT(varchar(19), StartedAt, 120),
                                   '），这一轮没有跑完 —— 它的档期已在启动时被消费，不会自动补跑')
              WHERE Status = '{StatusRunning}' AND FinishedAt IS NULL");
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
            // 排除 RUNNING：那是「正在进行」而不是「上一次」，把它当上次运行喂给面板，
            // 会让 lastRun 变成 null、lastStatus 变成 RUNNING，反而看不出真正的上一次结论。
            // ABORTED 行的 FinishedAt 为 null，展示上用 StartedAt 顶替——那确实是
            // 「最后一次尝试的时刻」，只是没能跑完。
            @"WITH x AS (
                  SELECT TaskName, ISNULL(FinishedAt, StartedAt) AS FinishedAt, Status, Message,
                         ROW_NUMBER() OVER (PARTITION BY TaskName ORDER BY LogId DESC) AS rn
                  FROM dbo.SyncLog
                  WHERE TaskName LIKE 'job:%' AND Status <> 'RUNNING'
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

    /// <summary>
    /// 最近一条成功的 cards 采集（定时 <c>job:cards-daily</c> 或手动 <c>cards:manual</c>）。
    /// 闸 L1/L2 必须落在同一条记录上，所以这里只取一条，不做「配对一条 + 达标另一条」。
    /// </summary>
    public async Task<CardsSyncRow?> GetLatestCardsSuccessAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CardsSyncRow>(new CommandDefinition(
            @"SELECT TOP 1 TaskName, FinishedAt, RowsAffected, Message
              FROM dbo.SyncLog
              WHERE Status = 'SUCCESS'
                AND FinishedAt IS NOT NULL
                AND TaskName IN (N'job:cards-daily', N'cards:manual')
              ORDER BY FinishedAt DESC, LogId DESC",
            cancellationToken: ct));
    }

    /// <summary>
    /// <paramref name="before"/> 之前最近一次成功的 cards 采集完成时刻（no-sale 旁证：cap 应对齐它）。
    /// </summary>
    public async Task<DateTime?> GetPreviousCardsFinishedAsync(DateTime before, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            @"SELECT TOP 1 FinishedAt
              FROM dbo.SyncLog
              WHERE Status = 'SUCCESS'
                AND FinishedAt IS NOT NULL
                AND FinishedAt < @before
                AND TaskName IN (N'job:cards-daily', N'cards:manual')
              ORDER BY FinishedAt DESC, LogId DESC",
            new { before }, cancellationToken: ct));
    }

    public sealed class CardsSyncRow
    {
        public string TaskName { get; set; } = "";
        public DateTime FinishedAt { get; set; }
        public int? RowsAffected { get; set; }
        public string? Message { get; set; }
    }
}
