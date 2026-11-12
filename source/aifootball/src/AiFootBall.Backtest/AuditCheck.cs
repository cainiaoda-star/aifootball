using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 审计表端到端自检（2026-11-12 加）。
///
/// 为什么需要它：`SaveDayAsync` / `SaveProbSnapshotsAsync` 里都是**手写的多列 INSERT**，
/// 列数和值数一旦不齐、或者新加的那一列没进 SELECT，SQL Server 会在运行时抛错。
/// 而调用方（`RecommendationService.SaveMilkAsync`）用 try/catch 包着存档写入——
/// 于是「存档一直没写进去」的表现是**完全静默**的：榜照出、日志无异常、表里就是空的。
/// 这个项目已经因为「判据写了、字段没读」栽过一次（`GetOpenDayAsync` 漏 SELECT BaselineEv）。
///
/// 做法：用**远离真实数据的日期**（2090-01-01）插一条假注单和三条假分布，读回来核对，再删干净。
/// 不碰任何真实场次、不触发结算、不改榜面。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- audit-check
/// 会写库（仅 2090-01-01 这个日期），跑完自动清理。
/// </summary>
public static class AuditCheck
{
    public static async Task<int> RunAsync(string connStr)
    {
        var probeDate = new DateTime(2090, 1, 1);
        const long probeMatchId = 999_999_991L;
        var factory = new SqlConnectionFactory(connStr);
        var repo = new MilkPickRepository(factory);
        var failures = new List<string>();

        await using var conn = await factory.OpenAsync();

        // ---------- 1) FbMilkPick 多列 INSERT（含新加的 ModelVersion） ----------
        var pick = new MilkPick
        {
            MatchNo = "自检000",
            MatchId = probeMatchId,
            LeagueName = "自检联赛",
            HomeTeam = "自检主队",
            AwayTeam = "自检客队",
            MatchTime = probeDate.AddHours(12),
            PlayType = JcPlayTypes.Bq,
            OptionCode = "3-3",
            OptionName = "胜胜",
            Odds = 2.5m,
            ModelProbability = 31.11m,
            ModelPctPure = 30.00m,
            Ev = 0.778m,
            ModelVersion = "SELFCHECK|dc1|2090-01-01..2090-01-01|n=1|rho=0.000|test|w=70/70/50/80/30|bf=off"
        };

        var written = await repo.SaveDayAsync(probeDate, new[] { pick });
        var readBack = await repo.GetDayAsync(probeDate);
        var stored = readBack.FirstOrDefault(x => x.MatchId == probeMatchId);
        if (written == 0) failures.Add("FbMilkPick 写入 0 行（多列 INSERT 可能列数不匹配）");
        if (stored is null) failures.Add("FbMilkPick 读回不到自检行（SELECT 列不全？）");
        else if (stored.ModelVersion != pick.ModelVersion) failures.Add($"ModelVersion 未落库：读到 {stored.ModelVersion ?? "NULL"}");

        // ---------- 2) 分布存档表 ----------
        var snapshotRows = new List<MilkProbSnapshot>
        {
            new()
            {
                MatchId = probeMatchId, MatchNo = "自检000", PlayType = JcPlayTypes.Bq, OptionCode = "3-3",
                Odds = 2.5m, MarketPct = 31.89m, ModelPctPure = 30.00m, BlendedPct = 31.60m,
                MarketPriced = true, ModelVersion = pick.ModelVersion
            },
            new()
            {
                MatchId = probeMatchId, MatchNo = "自检000", PlayType = JcPlayTypes.Bq, OptionCode = "1-1",
                Odds = 5.0m, MarketPct = 15.95m, ModelPctPure = 12.00m, BlendedPct = 14.58m,
                MarketPriced = true, ModelVersion = pick.ModelVersion
            },
            new()
            {
                MatchId = probeMatchId, MatchNo = "自检000", PlayType = JcPlayTypes.Jq, OptionCode = "3",
                Odds = 4.0m, MarketPct = 19.93m, ModelPctPure = 22.00m, BlendedPct = 20.34m,
                MarketPriced = true, ModelVersion = pick.ModelVersion
            }
        };

        var snapWritten = await repo.SaveProbSnapshotsAsync(probeDate, snapshotRows);
        var snapStored = (await conn.QueryAsync<(string PlayType, string OptionCode, decimal? Pure, decimal? Blend, string? Ver)>(
            @"SELECT PlayType, OptionCode, ModelPctPure AS Pure, BlendedPct AS Blend, ModelVersion AS Ver
              FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d AND MatchId=@m",
            new { d = probeDate, m = probeMatchId })).ToList();

        if (snapWritten != snapshotRows.Count) failures.Add($"分布存档写入 {snapWritten} 行，期望 {snapshotRows.Count}");
        if (snapStored.Count != snapshotRows.Count) failures.Add($"分布存档读回 {snapStored.Count} 行，期望 {snapshotRows.Count}");
        var bq33 = snapStored.FirstOrDefault(x => x.PlayType == JcPlayTypes.Bq && x.OptionCode == "3-3");
        if (bq33.PlayType is null) failures.Add("分布存档缺 3-3 行");
        else if (bq33.Pure != 30.00m || bq33.Blend != 31.60m) failures.Add($"分布存档数值不对：pure={bq33.Pure} blend={bq33.Blend}");

        // 幂等：同一期再写一次不应累加（先 DELETE 再 INSERT）
        await repo.SaveProbSnapshotsAsync(probeDate, snapshotRows);
        var afterRewrite = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
        if (afterRewrite != snapshotRows.Count) failures.Add($"重写不幂等：两次写入后共 {afterRewrite} 行");

        // ---------- 清理 ----------
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkPick WHERE PickDate=@d", new { d = probeDate });
        var leftPick = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkPick WHERE PickDate=@d", new { d = probeDate });
        var leftSnap = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
        if (leftPick != 0 || leftSnap != 0) failures.Add($"清理不干净：pick={leftPick} snapshot={leftSnap}");

        Console.WriteLine($"# 审计表自检  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"| 检查项 | 结果 |");
        Console.WriteLine($"|---|---|");
        Console.WriteLine($"| FbMilkPick 写入行数 | {written} |");
        Console.WriteLine($"| FbMilkPick 读回 | {(stored is null ? "失败" : "成功")} |");
        Console.WriteLine($"| ModelVersion 落库 | {(stored?.ModelVersion is null ? "**NULL**" : "正常")} |");
        Console.WriteLine($"| 分布存档写入 / 读回 | {snapWritten} / {snapStored.Count}（期望 {snapshotRows.Count}） |");
        Console.WriteLine($"| 重写后行数（幂等） | {afterRewrite} |");
        Console.WriteLine($"| 清理后残留 | pick={leftPick} snapshot={leftSnap} |");
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("**全部通过。** 两套 INSERT 的列与值数量一致，新列能落库也能读回。");
            return 0;
        }

        Console.WriteLine("**失败项：**");
        foreach (var f in failures) Console.WriteLine($"- {f}");
        return 1;
    }
}
