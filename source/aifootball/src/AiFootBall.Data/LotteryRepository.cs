using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>彩票数据仓储：彩种字典、开奖数据读写。</summary>
public sealed class LotteryRepository
{
    private readonly SqlConnectionFactory _factory;

    public LotteryRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// 彩种字典。默认只返回 IsActive=1 的，即「对外可见的彩种」。
    /// 开奖采集必须显式传 includeInactive:true —— 七乐彩/七星彩不提供选号，
    /// 但历史开奖仍要继续采，停采会让已入库的 3000 期变成孤岛。
    /// </summary>
    public async Task<List<LotteryTypeInfo>> GetLotteryTypesAsync(bool includeInactive = false,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<LotteryTypeInfo>(
            @"SELECT LotteryId, Code, Name, Category, RedCount, RedMax, BlueCount, BlueMax,
                     DrawWeekdays, SourceUrl, IsActive
              FROM dbo.LotteryType
              WHERE @IncludeInactive = 1 OR IsActive = 1
              ORDER BY LotteryId",
            new { IncludeInactive = includeInactive });
        return rows.ToList();
    }

    /// <summary>按 code 精确查。不受 IsActive 影响：停用的彩种也要能查到，否则采集按 code 会扑空。</summary>
    public async Task<LotteryTypeInfo?> GetLotteryTypeAsync(string code, CancellationToken ct = default)
    {
        var all = await GetLotteryTypesAsync(includeInactive: true, ct);
        return all.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>批量写入开奖数据（幂等：按期号 MERGE，号码全量重建）。返回实际影响期数。</summary>
    public async Task<int> UpsertDrawsAsync(LotteryTypeInfo lt, IEnumerable<LotteryDrawRecord> draws,
        CancellationToken ct = default)
    {
        const string mergeSql = @"
MERGE dbo.LotteryDraw AS t
USING (SELECT @LotteryId AS LotteryId, @Issue AS Issue) AS s
    ON t.LotteryId = s.LotteryId AND t.Issue = s.Issue
WHEN MATCHED THEN UPDATE SET
    DrawDate = @DrawDate, RedNumbers = @RedNumbers, BlueNumbers = @BlueNumbers,
    RedSum = @RedSum, RedSpan = @RedSpan, OddCount = @OddCount, BigCount = @BigCount,
    SalesAmount = ISNULL(@SalesAmount, t.SalesAmount),
    PoolAmount  = ISNULL(@PoolAmount,  t.PoolAmount),
    Source = @Source, UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (LotteryId, Issue, DrawDate, RedNumbers, BlueNumbers, RedSum, RedSpan, OddCount, BigCount,
     SalesAmount, PoolAmount, Source)
    VALUES (@LotteryId, @Issue, @DrawDate, @RedNumbers, @BlueNumbers, @RedSum, @RedSpan,
            @OddCount, @BigCount, @SalesAmount, @PoolAmount, @Source);

DELETE FROM dbo.LotteryDrawNumber WHERE LotteryId = @LotteryId AND Issue = @Issue;
";
        const string numberSql = @"INSERT INTO dbo.LotteryDrawNumber (LotteryId, Issue, BallType, Seq, Number)
VALUES (@LotteryId, @Issue, @BallType, @Seq, @Number);";
        const string prizeSql = @"
MERGE dbo.LotteryPrize AS t
USING (SELECT @LotteryId AS LotteryId, @Issue AS Issue, @Level AS PrizeLevel) AS s
    ON t.LotteryId = s.LotteryId AND t.Issue = s.Issue AND t.PrizeLevel = s.PrizeLevel
WHEN MATCHED THEN UPDATE SET BetCount = @BetCount, PrizeAmount = @Amount
WHEN NOT MATCHED THEN INSERT (LotteryId, Issue, PrizeLevel, BetCount, PrizeAmount)
    VALUES (@LotteryId, @Issue, @Level, @BetCount, @Amount);";

        var list = draws
            .Where(d => d.Reds.Count > 0 && d.Issue.Length > 0)
            .GroupBy(d => d.Issue)
            .Select(g => g.First())
            .ToList();

        var affected = 0;
        await using var conn = await _factory.OpenAsync(ct);

        foreach (var d in list)
        {
            ct.ThrowIfCancellationRequested();

            var stats = LotteryMath.Compute(lt, d);
            await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
            {
                try
                {
                    await conn.ExecuteAsync(mergeSql, new
                    {
                        LotteryId = lt.LotteryId,
                        d.Issue,
                        DrawDate = d.DrawDate.ToDateTime(TimeOnly.MinValue),
                        RedNumbers = d.RedCsv,
                        BlueNumbers = d.BlueCsv,
                        stats.RedSum,
                        stats.RedSpan,
                        stats.OddCount,
                        stats.BigCount,
                        d.SalesAmount,
                        d.PoolAmount,
                        d.Source
                    }, tx);

                    var nums = d.Reds.Select((v, i) => new { BallType = "R", Seq = (byte)(i + 1), Number = (byte)v })
                        .Concat(d.Blues.Select((v, i) => new { BallType = "B", Seq = (byte)(i + 1), Number = (byte)v }))
                        .Select(x => new { LotteryId = lt.LotteryId, d.Issue, x.BallType, x.Seq, x.Number });
                    await conn.ExecuteAsync(numberSql, nums, tx);

                    if (d.Prizes.Count > 0)
                    {
                        await conn.ExecuteAsync(prizeSql, d.Prizes.Select(p => new
                        {
                            LotteryId = lt.LotteryId, d.Issue, p.Level, p.BetCount, p.Amount
                        }), tx);
                    }

                    await tx.CommitAsync(ct);
                    affected++;
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            }
        }

        return affected;
    }

    /// <summary>读取历史开奖（按期号倒序），用于统计分析。</summary>
    public async Task<List<LotteryDrawRecord>> GetDrawsAsync(string code, int take = 0,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var sql = @"SELECT TOP (@Take) d.Issue, d.DrawDate, d.RedNumbers, d.BlueNumbers,
                           d.SalesAmount, d.PoolAmount, d.Source, t.Code
                    FROM dbo.LotteryDraw d
                    JOIN dbo.LotteryType t ON t.LotteryId = d.LotteryId
                    WHERE t.Code = @Code
                      AND (@From IS NULL OR d.DrawDate >= @From)
                      AND (@To   IS NULL OR d.DrawDate <= @To)
                    ORDER BY d.DrawDate DESC, d.Issue DESC";
        var takeValue = take <= 0 ? 100000 : take;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<dynamic>(sql, new
        {
            Code = code,
            Take = takeValue,
            From = from?.ToDateTime(TimeOnly.MinValue),
            To = to?.ToDateTime(TimeOnly.MinValue)
        });

        var list = new List<LotteryDrawRecord>();
        foreach (var r in rows)
        {
            string redCsv = (string)r.RedNumbers;
            string? blueCsv = (string?)r.BlueNumbers;
            list.Add(new LotteryDrawRecord
            {
                LotteryCode = (string)r.Code,
                Issue = (string)r.Issue,
                DrawDate = DateOnly.FromDateTime((DateTime)r.DrawDate),
                Reds = ParseCsv(redCsv),
                Blues = ParseCsv(blueCsv),
                SalesAmount = (long?)(r.SalesAmount is DBNull ? null : r.SalesAmount),
                PoolAmount = (long?)(r.PoolAmount is DBNull ? null : r.PoolAmount),
                Source = (string)r.Source
            });
        }
        return list;
    }

    private static List<int> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new List<int>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => int.Parse(s.Trim()))
                 .ToList();

    /// <summary>各彩种数据概览</summary>
    public async Task<List<dynamic>> GetOverviewAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync(@"
SELECT t.Code, t.Name, t.Category, t.IsActive,
       COUNT(d.DrawId)                       AS DrawCount,
       CONVERT(varchar(10), MIN(d.DrawDate), 23) AS FirstDate,
       CONVERT(varchar(10), MAX(d.DrawDate), 23) AS LastDate,
       MAX(d.Issue)                          AS LastIssue,
       MAX(d.UpdatedAt)                      AS LastSync
FROM dbo.LotteryType t
LEFT JOIN dbo.LotteryDraw d ON d.LotteryId = t.LotteryId
GROUP BY t.Code, t.Name, t.Category, t.LotteryId, t.IsActive
ORDER BY t.LotteryId");
        return rows.ToList();
    }

    public async Task WriteSyncLogAsync(string task, string? source, string status, int? rows, string? message,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            @"INSERT INTO dbo.SyncLog (TaskName, Source, FinishedAt, Status, RowsAffected, Message)
              VALUES (@task, @source, SYSDATETIME(), @status, @rows, @message)",
            new
            {
                task = SyncLogRepository.ClipTask(task),
                source = SyncLogRepository.ClipTask(source),
                status,
                rows,
                message = SyncLogRepository.ClipMessage(message)
            });
    }
}
