using AiFootBall.Data;

namespace AiFootBall.Backtest;

/// <summary>
/// 历史战绩口径核对（2026-11-13 第四轮，**只读**）。
///
/// 起因（用户当天要求）：榜面改成「只读该受注日最新成功轮」之后，
/// 必须证明**升级前已经开赛、结算、撤注的注不会因为切轮而消失**。
/// 说「不会」不算证据，所以把新旧两种口径并排算出来，让差异自己说话：
///
///   · 旧口径 = 升级前的算法（逐行、不看轮次、不去重）
///   · 新口径 = 上过榜的一注（按业务键去重、只认成功发布轮 + 升级前 NULL 行）
///
/// 两条判定：
///   `legacySettled == legacySettledInNew` —— 升级前的已结算行全部存活（历史不丢）
///   `newSettled <= distinctBets`          —— 去重生效（同一注不被复制放大）
///
/// 不写库，可在生产库直接跑。
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- milk-ledger-report [天数]
/// </summary>
public static class MilkLedgerAuditStudy
{
    public static async Task<int> RunAsync(string connStr, int days = 30)
    {
        var factory = new SqlConnectionFactory(connStr);
        var repo = new MilkPickRepository(factory);
        var audit = await repo.GetLedgerAuditAsync(days);

        Console.WriteLine($"# 历史战绩口径核对（近 {audit.Days} 天，自 {audit.From:MM-dd}）  "
                          + $"{DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();

        Console.WriteLine("| 项目 | 数值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 原始行数 | {audit.AllRows} |");
        Console.WriteLine($"| 去重后注数 | {audit.DistinctBets} |");
        Console.WriteLine($"| 多副本重复行 | {audit.DuplicateRows} |");
        Console.WriteLine($"| RunId 为空（升级前） | {audit.LegacyRows} |");
        Console.WriteLine($"| 带 RunId（升级后） | {audit.RunRows} |");
        Console.WriteLine($"| 已结算行（逐行） | {audit.SettledRows} |");
        Console.WriteLine($"| 撤注行（逐行） | {audit.VoidedRows} |");
        Console.WriteLine($"| 升级前已结算 | {audit.LegacySettled} |");
        Console.WriteLine($"| ↳ 在新口径中存活 | {audit.LegacySettledInNew} |");
        Console.WriteLine($"| 旧口径 已结算 / 命中 | {audit.OldSettled} / {audit.OldHit}（{audit.OldHitPct:0.0}%） |");
        Console.WriteLine($"| 新口径 已结算 / 命中 | {audit.NewSettled} / {audit.NewHit}（{audit.NewHitPct:0.0}%） |");
        Console.WriteLine();

        if (audit.Plays.Count > 0)
        {
            Console.WriteLine("| 玩法 | 旧 总数/命中 | 新 总数/命中 | 移出新口径 | 其中升级前来源 |");
            Console.WriteLine("|---|---|---|---|---|");
            foreach (var p in audit.Plays)
                Console.WriteLine($"| {p.PlayType} | {p.OldTotal}/{p.OldHit} | {p.NewTotal}/{p.NewHit} "
                                  + $"| {p.OldTotal - p.NewTotal} | {p.NewLegacySettled} |");
            Console.WriteLine();
        }

        Console.WriteLine("**判定：**");
        Console.WriteLine($"- 历史保留（升级前已结算 {audit.LegacySettled} → 新口径 {audit.LegacySettledInNew}）："
                          + (audit.LegacyPreserved ? "通过" : "**不通过——历史被吞**"));
        Console.WriteLine($"- 去重生效（新口径 {audit.NewSettled} ≤ 去重注数 {audit.DistinctBets}）："
                          + (audit.NoInflation ? "通过" : "**不通过——同一注被重复统计**"));
        Console.WriteLine();

        // 读路径连通性（只读）：四条查榜/查战绩的 SQL 都在本轮改过，
        // 任何一条语法错都会让页面 500 或战绩页空白——这类错只有真跑一次才会暴露。
        Console.WriteLine("**读路径连通性（只读）：**");
        Console.WriteLine();
        Console.WriteLine("| 方法 | 行数 |");
        Console.WriteLine("|---|---|");
        var failures = 0;
        async Task ProbeAsync(string name, Func<Task<int>> run)
        {
            try
            {
                var n = await run();
                Console.WriteLine($"| {name} | {n} |");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"| {name} | **失败：{ex.GetType().Name}: {ex.Message}** |");
            }
        }

        await ProbeAsync("GetDayAsync(今天)", async () => (await repo.GetDayAsync(DateTime.Today)).Count);
        await ProbeAsync("GetReportAsync(30)", async () => (await repo.GetReportAsync(30)).Overall.Total);
        await ProbeAsync("GetOpenDayAsync(风控)", async () => (await repo.GetOpenDayAsync(DateTime.Today)).Count);
        await ProbeAsync("GetVoidedAsync(撤榜区)", async () => (await repo.GetVoidedAsync(DateTime.Today)).Count);
        await ProbeAsync("GetGateAsync(方向闸)", async () => (await repo.GetGateAsync(DateTime.Today)).Count);
        Console.WriteLine();

        var ok = audit.Ok && failures == 0;
        Console.WriteLine(ok
            ? "两条判定 + 五条读路径全部通过：切轮不会让历史战绩消失，也不会把同一注算成多注。"
            : "存在不通过项，**不可**把该口径用于战绩展示。");

        return ok ? 0 : 1;
    }
}
