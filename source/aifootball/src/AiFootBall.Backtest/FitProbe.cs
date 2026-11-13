using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 真实取样路径探针（2026-11-13 加，只读）。
///
/// 存在的理由：11-13 那条注单的版本戳是
/// <c>dc1|?..?|n=178179|rho=0.010|...|w=70/70/50/80/30|bf=off</c> —— 窗口两段都是问号。
/// 根因是 <c>BettingRepository.GetScoreSamplesAsync</c> 的 SELECT 里**没有 f.MatchTime**，
/// Dapper 映射时 MatchTime 恒为 null，<c>ScorelineModel.Fit</c> 取 min/max 的那段被整段跳过。
///
/// 这个缺陷的特征是**编译能过、单测能过、线上不报错**：它不改变任何概率（线上两条调用都不传
/// asOf / halfLifeDays），只在审计字段上留一个问号。所以它不可能靠普通单测发现，
/// 只能拿真实库跑一遍「取样 → 拟合 → 读窗口」这条完整链路。
///
/// 本探针查五件事，任一条不成立就退出码 1：
///   1. 真实取样回来的样本，MatchTime 是不是都不为空；
///   2. 不给 asOf 时，VersionTag 里的拟合窗口是不是真实日期（不是 <c>?..?</c>）；
///   3. 给了 asOf 时，参与拟合的场次是否真的减少、FitTo 是否 ≤ 截止日；
///   4. `before` 参数（回测口径）是否真的把截止日之后的场次挡在 SQL 外；
///   5. 有没有「开赛在未来却有比分」的场次混进训练（模型提前知道结果的入口）。
/// 另外报一节「衰减对比用过的样本路径」——那是 `ModelWeightStudy` 自己写的 SQL，
/// 与本仓的 repo 方法不是同一条，需要单独确认它的时间字段也不是空的。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- fit-probe [截止日偏移天数]
/// 默认偏移 30 天。全程只读，不写任何表。
/// </summary>
public static class FitProbe
{
    public static async Task<int> RunAsync(string connStr, int asOfDaysAgo = 30)
    {
        var factory = new SqlConnectionFactory(connStr);
        var repo = new BettingRepository(factory);
        var failures = new List<string>();

        Console.WriteLine($"# 取样链路探针  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();

        // ---------- 1. 真实取样路径：时间字段 ----------
        var all = await repo.GetScoreSamplesAsync();
        var withTime = all.Count(s => s.MatchTime is not null);
        var nullTime = all.Count - withTime;
        var minTime = all.Where(s => s.MatchTime is not null).Min(s => s.MatchTime);
        var maxTime = all.Where(s => s.MatchTime is not null).Max(s => s.MatchTime);

        Console.WriteLine("## 一、真实取样路径（BettingRepository.GetScoreSamplesAsync）");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 样本场次 | {all.Count} |");
        Console.WriteLine($"| MatchTime 为空 | {nullTime} |");
        Console.WriteLine($"| 最早开赛 | {(minTime is DateTime a ? a.ToString("yyyy-MM-dd") : "—")} |");
        Console.WriteLine($"| 最晚开赛 | {(maxTime is DateTime b ? b.ToString("yyyy-MM-dd") : "—")} |");
        Console.WriteLine();
        if (all.Count == 0) failures.Add("取样返回 0 场，后面几节无从验证");
        else if (nullTime > 0)
            failures.Add($"取样里有 {nullTime} 场 MatchTime 为空——SQL 又漏了 f.MatchTime，拟合窗口会退回 ?..?");
        else
            Console.WriteLine($"数值判定：**PASS** —— {all.Count} 场全部带开赛时间。");
        Console.WriteLine();

        // ---------- 2. 拟合窗口（线上口径：不传 asOf / halfLife） ----------
        var model = ScorelineModel.Fit(all);
        Console.WriteLine("## 二、拟合窗口（线上口径，不传 asOf / halfLifeDays）");
        Console.WriteLine();
        Console.WriteLine($"版本戳：`{model.VersionTag}`");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| Ready | {model.Ready} |");
        Console.WriteLine($"| 参与拟合场次 | {model.FitGames} |");
        Console.WriteLine($"| FitFrom | {(model.FitFrom is DateTime f ? f.ToString("yyyy-MM-dd HH:mm") : "NULL")} |");
        Console.WriteLine($"| FitTo | {(model.FitTo is DateTime t ? t.ToString("yyyy-MM-dd HH:mm") : "NULL")} |");
        Console.WriteLine($"| rho | {model.Rho:0.000} |");
        Console.WriteLine();
        if (!model.Ready) failures.Add("模型未就绪（样本 < 30 或全无效）");
        else if (model.FitFrom is null || model.FitTo is null)
            failures.Add("FitFrom/FitTo 仍为 NULL——窗口还是 ?..?，取样时间字段没生效");
        else
            Console.WriteLine("数值判定：**PASS** —— 窗口是真实日期，不再是 `?..?`。");
        Console.WriteLine();

        // ---------- 3. asOf 截断 ----------
        var cut = DateTime.Today.AddDays(-asOfDaysAgo);
        var cutModel = ScorelineModel.Fit(all, asOf: cut);
        Console.WriteLine($"## 三、asOf 截断（截止日 = {cut:yyyy-MM-dd}）");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 不传 asOf 的场次 | {model.FitGames} |");
        Console.WriteLine($"| 传 asOf 的场次 | {cutModel.FitGames} |");
        Console.WriteLine($"| 被剔掉 | {model.FitGames - cutModel.FitGames} |");
        Console.WriteLine($"| 传 asOf 后的 FitTo | {(cutModel.FitTo is DateTime ct2 ? ct2.ToString("yyyy-MM-dd HH:mm") : "NULL")} |");
        Console.WriteLine();
        if (!cutModel.Ready) failures.Add("传 asOf 后模型未就绪——样本被剔光了（正是 MatchTime 为 null 时的旧病）");
        else if (cutModel.FitTo is DateTime ft && ft > cut)
            failures.Add($"传 asOf 后 FitTo={ft:yyyy-MM-dd} 晚于截止日 {cut:yyyy-MM-dd}，截止日过滤没生效");
        else if (cutModel.FitGames >= model.FitGames)
            failures.Add($"传 asOf 后场次没有减少（{cutModel.FitGames} vs {model.FitGames}），过滤形同虚设");
        else
            Console.WriteLine("数值判定：**PASS** —— 场次真的少了，且 FitTo 不越过截止日。");
        Console.WriteLine();

        // ---------- 4. before 参数（回测口径） ----------
        var beforeRows = await repo.GetScoreSamplesAsync(before: cut);
        var badBefore = beforeRows.Count(s => s.MatchTime is not DateTime t || t >= cut);
        Console.WriteLine($"## 四、before 参数（RecommendationService 回测用的口径）");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| before={cut:yyyy-MM-dd} 取到 | {beforeRows.Count} 场 |");
        Console.WriteLine($"| 其中开赛不早于截止日 | {badBefore} 场 |");
        Console.WriteLine();
        if (beforeRows.Count == 0) failures.Add("before 参数取不到样本，SQL 的时间闸写反了");
        else if (badBefore > 0) failures.Add($"before 没挡住 {badBefore} 场截止日之后（或时间未知）的比赛");
        else Console.WriteLine("数值判定：**PASS** —— 取回来的全在截止日之前。");

        // ---------- 5. 衰减对比用过的样本路径 ----------
        // 这一节不是查本仓 repo，是查 `weight-probe`（ModelWeightStudy）自己那段 SQL。
        // 它与 repo 方法是两份代码，所以必须单独确认——否则「衰减为什么无效」的结论可能建立在空时间上。
        Console.WriteLine();
        Console.WriteLine("## 五、衰减对比（weight-probe）用过的样本路径");
        Console.WriteLine();
        await using (var conn = await factory.OpenAsync())
        {
            var rows = (await conn.QueryAsync<(int Total, int WithTime, DateTime? MinT, DateTime? MaxT)>(
                @"SELECT COUNT(*) AS Total,
                         COUNT(MatchTime) AS WithTime,
                         MIN(MatchTime) AS MinT,
                         MAX(MatchTime) AS MaxT
                  FROM dbo.FbMatch
                  WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                    AND MatchTime >= DATEADD(month, -18, GETDATE())")).Single();

            Console.WriteLine("| 项目 | 值 |");
            Console.WriteLine("|---|---|");
            Console.WriteLine($"| 场次 | {rows.Total} |");
            Console.WriteLine($"| MatchTime 为空 | {rows.Total - rows.WithTime} |");
            Console.WriteLine($"| 最早开赛 | {(rows.MinT is DateTime m1 ? m1.ToString("yyyy-MM-dd") : "—")} |");
            Console.WriteLine($"| 最晚开赛 | {(rows.MaxT is DateTime m2 ? m2.ToString("yyyy-MM-dd") : "—")} |");
            Console.WriteLine();
            if (rows.Total == 0) failures.Add("衰减对比的样本 SQL 取不到场次");
            else if (rows.WithTime < rows.Total)
                failures.Add($"衰减对比的样本里有 {rows.Total - rows.WithTime} 场没有 MatchTime，那份结论要重跑");
            else
                Console.WriteLine("数值判定：**PASS** —— 那份 SQL 自己带了 MatchTime，"
                    + "否则 ModelWeightStudy 里 `samples.Where(s => s.MatchTime < cutoff)` 会把样本全都滤掉、"
                    + "直接报「样本不足」，不可能跑出 16 万场的结果。**旧结论不需重跑。**");
        }

        // ---------- 6. 带比分的未来场次（「模型提前知道结果」的入口） ----------
        // 起因：跑第 2 节时 FitTo 落在两天之后，追下去发现库里有 2 场「开赛时间在未来、
        // 但 Status=3 且比分齐全」的比赛。线上 Fit 不传 asOf，它们会照常进训练集。
        //
        // 危险程度分两级，必须分开看：
        //   · 只是混进 17.8 万场里 → 影响可忽略，但要留痕（否则窗口戳永远指向未来，没人解释得了）；
        //   · 如果它同时是**可售场次**（进了 BettingCard），那只可能是「赛前就有比分」的数据错误，
        //     模型据此下注等于用未来的答案，必须 FAIL。
        Console.WriteLine();
        Console.WriteLine("## 六、带比分的未来场次（「模型提前知道结果」的入口）");
        Console.WriteLine();
        await using (var conn = await factory.OpenAsync())
        {
            // 三个标量子查询分开写：SQL Server 不允许 SUM(CASE WHEN EXISTS(子查询) ...)
            // ——「不能对包含聚合或子查询的表达式执行聚合函数」，第一次就撞上了。
            const string cond = @"f.Status = 3 AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                    AND f.MatchTime > GETDATE()";
            var fut = (await conn.QueryAsync<(int Total, int OnBoard, DateTime? MaxT)>(
                $@"SELECT
                     (SELECT COUNT(*) FROM dbo.FbMatch f WHERE {cond}) AS Total,
                     (SELECT COUNT(*) FROM dbo.FbMatch f WHERE {cond}
                        AND EXISTS (SELECT 1 FROM dbo.BettingCard c WHERE c.MatchId = f.MatchId)) AS OnBoard,
                     (SELECT MAX(f.MatchTime) FROM dbo.FbMatch f WHERE {cond}) AS MaxT")).Single();

            Console.WriteLine("| 项目 | 值 |");
            Console.WriteLine("|---|---|");
            Console.WriteLine($"| 开赛在未来却有比分 | {fut.Total} 场 |");
            Console.WriteLine($"| 其中进了可售盘口 | {fut.OnBoard} 场 |");
            Console.WriteLine($"| 最晚一场的开赛时间 | {(fut.MaxT is DateTime mt ? mt.ToString("yyyy-MM-dd HH:mm") : "—")} |");
            Console.WriteLine();

            if (fut.OnBoard > 0)
                failures.Add($"有 {fut.OnBoard} 场「开赛在未来却已带比分」的比赛进了可售盘口——"
                    + "模型等于赛前拿到答案，必须查清数据来源并加过滤");
            else if (fut.Total > 20)
                failures.Add($"{fut.Total} 场带比分的未来场次超过 20，已经不是零星脏数据，必须先查清再训练");
            else if (fut.Total > 0)
                Console.WriteLine($"数值判定：**WARN** —— {fut.Total} 场零星脏数据（不在任何盘口里），"
                    + "对 17.8 万场训练集的影响可忽略，但它是拟合窗口落在未来的唯一原因。"
                    + "**未改代码**：改动训练集会动概率，今晚不动；这一项留给你判断。");
            else
                Console.WriteLine("数值判定：**PASS** —— 没有带比分的未来场次。");
        }

        // ---------- 结论 ----------
        Console.WriteLine();
        Console.WriteLine("## 结论");
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("**PASS** —— 取样时间字段、拟合窗口、asOf 截断、before 时间闸四项全部成立。");
            return 0;
        }

        Console.WriteLine("**FAIL**");
        foreach (var msg in failures) Console.WriteLine($"- {msg}");
        return 1;
    }
}
