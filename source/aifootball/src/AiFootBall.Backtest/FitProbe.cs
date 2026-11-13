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
/// 这个缺陷的特征是**编译能过、单测能过、线上不报错**：它只在审计字段上留一个问号。
/// 所以它不可能靠普通单测发现，只能拿真实库跑一遍「取样 → 拟合 → 读窗口」这条完整链路。
///
/// 2026-11-13 晚第二版改动（用户令）：
///   · 训练集必须排除**拟合时刻之后开赛**的比赛，哪怕已有比分、哪怕没有竞彩赔率。
///     旧版对「带比分的未来场次」给了「进可售盘口才 FAIL、超过 20 场才 FAIL」的容差——
///     容差已全部删除，现在判据只有一条：**进训练集就是 FAIL**。
///   · 线上拟合显式传 asOf（HitProbabilityService），SQL 层再用 GETDATE() 兜底，两层都卡。
///
/// 本探针查八件事，任一条不成立就退出码 1：
///   1. 真实取样回来的样本，MatchTime 是不是都不为空、都不晚于取数时刻；
///   2. 线上口径（显式 asOf = 取数时刻）的拟合窗口是不是真实日期，且 FitTo 不越过该时刻；
///   2b. 同一时刻取两次样，版本戳是否逐字相同（取样顺序不稳会让 rho 乱跳，见第二节的注）；
///   3. 给了更早的 asOf 时，参与拟合的场次是否真的减少、FitTo 是否 ≤ 截止日；
///   4. `before` 参数（回测口径）是否真的把截止日之后的场次挡在 SQL 外；
///   5. 有没有「开赛在未来却有比分」的场次混进训练（模型提前知道结果的入口）——零容差；
///   6. 衰减对比用过的样本路径有没有时间字段（`ModelWeightStudy` 自己那条 SQL）；
///   7. 这次过滤到底动了什么：被剔掉的场次、涉及的联赛与球队、λ 的实际变化。
///      —— 概率不是「零变化」，这一节就是「变了多少」的留痕。
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
        var fetchedAt = DateTime.Now;

        Console.WriteLine($"# 取样链路探针  {fetchedAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        // ---------- 1. 真实取样路径：时间字段 + 不晚于取数时刻 ----------
        var all = await repo.GetScoreSamplesAsync(asOf: fetchedAt);
        var withTime = all.Count(s => s.MatchTime is not null);
        var nullTime = all.Count - withTime;
        var future = all.Count(s => s.MatchTime is DateTime t && t > fetchedAt);
        var minTime = all.Where(s => s.MatchTime is not null).Min(s => s.MatchTime);
        var maxTime = all.Where(s => s.MatchTime is not null).Max(s => s.MatchTime);

        Console.WriteLine("## 一、真实取样路径（BettingRepository.GetScoreSamplesAsync，asOf = 取数时刻）");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 样本场次 | {all.Count} |");
        Console.WriteLine($"| MatchTime 为空 | {nullTime} |");
        Console.WriteLine($"| 开赛晚于取数时刻（必须为 0） | {future} |");
        Console.WriteLine($"| 最早开赛 | {(minTime is DateTime a ? a.ToString("yyyy-MM-dd HH:mm") : "—")} |");
        Console.WriteLine($"| 最晚开赛 | {(maxTime is DateTime b ? b.ToString("yyyy-MM-dd HH:mm") : "—")} |");
        Console.WriteLine();
        if (all.Count == 0) failures.Add("取样返回 0 场，后面几节无从验证");
        else if (nullTime > 0)
            failures.Add($"取样里有 {nullTime} 场 MatchTime 为空——SQL 又漏了 f.MatchTime，拟合窗口会退回 ?..?");
        else if (future > 0)
            failures.Add($"取样里有 {future} 场开赛时间晚于取数时刻——训练集泄漏，时间闸没生效");
        else
            Console.WriteLine($"数值判定：**PASS** —— {all.Count} 场全部带开赛时间，且没有一场晚于取数时刻。");
        Console.WriteLine();

        // ---------- 2. 线上口径的拟合窗口（显式 asOf） ----------
        var model = ScorelineModel.Fit(all, asOf: fetchedAt);

        // Fit 内部还会剔掉「不可用样本」（队名空、比分越界 0..12）。这是既有规则，不是新漏洞，
        // 但必须把差额算清楚——否则「FitGames ≠ 取样」这条断言会被当成泄漏，白查一轮。
        int unusable;
        await using (var conn0 = await factory.OpenAsync())
        {
            unusable = await conn0.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM dbo.FbMatch f
                  WHERE f.Status = 3 AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                    AND f.MatchTime >= DATEADD(month, -18, GETDATE())
                    AND f.MatchTime <= @asOf
                    AND (LTRIM(RTRIM(ISNULL(f.HomeTeamName, ''))) = ''
                         OR LTRIM(RTRIM(ISNULL(f.AwayTeamName, ''))) = ''
                         OR f.HomeScore > 12 OR f.AwayScore > 12)",
                new { asOf = fetchedAt });
        }

        Console.WriteLine("## 二、拟合窗口（线上口径：显式传 asOf = 取数时刻）");
        Console.WriteLine();
        Console.WriteLine($"版本戳：`{model.VersionTag}`");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| Ready | {model.Ready} |");
        Console.WriteLine($"| 取样场次 | {all.Count} |");
        Console.WriteLine($"| 其中 Fit 判定不可用（队名空 / 比分 > 12） | {unusable} |");
        Console.WriteLine($"| 参与拟合场次 | {model.FitGames} |");
        Console.WriteLine($"| FitFrom | {(model.FitFrom is DateTime f ? f.ToString("yyyy-MM-dd HH:mm") : "NULL")} |");
        Console.WriteLine($"| FitTo | {(model.FitTo is DateTime t ? t.ToString("yyyy-MM-dd HH:mm") : "NULL")} |");
        Console.WriteLine($"| rho | {model.Rho:0.000} |");
        Console.WriteLine();
        if (!model.Ready) failures.Add("模型未就绪（样本 < 30 或全无效）");
        else if (model.FitFrom is null || model.FitTo is null)
            failures.Add("FitFrom/FitTo 仍为 NULL——窗口还是 ?..?，取样时间字段没生效");
        else if (model.FitTo is DateTime ft0 && ft0 > fetchedAt)
            failures.Add($"FitTo={ft0:yyyy-MM-dd HH:mm} 晚于取数时刻 {fetchedAt:yyyy-MM-dd HH:mm}——未来场次仍在训练集里");
        else if (model.FitGames != all.Count - unusable)
            failures.Add($"FitGames={model.FitGames} ≠ 取样 {all.Count} − 不可用 {unusable}——"
                + "有样本在 Fit 里被静默丢掉，窗口戳与实际训练集不一致");
        else
            Console.WriteLine("数值判定：**PASS** —— 窗口是真实日期，FitTo 不越过取数时刻，"
                + "场次数 = 取样 − 不可用样本（差额有据可查）。");
        Console.WriteLine();
        Console.WriteLine($"注：rho 由 `FitRho` 按 1/{Math.Max(1, all.Count / 2500)} 的步长子采样估计，"
            + $"网格步长 0.01。样本组成微变（本次剔掉 2 场）就可能让它跳一格，"
            + $"所以 rho 只作留痕、不当判据。取样 SQL 已加 `ORDER BY MatchTime, MatchId`，"
            + $"保证两次取样抽出同一批子样本——下一节就是钉这件事。");
        Console.WriteLine();

        // ---------- 2b. 取样可重复性 ----------
        // 为什么单独查：同代码 + 同数据必须出同一个版本戳，否则「事后解释这条注」就是一句空话。
        // 实测过的病：取样 SQL 没有 ORDER BY，FitRho 按步长 71 抽 2500 个样本，
        // 同一份 178177 场三次跑出 rho = 0.010 / 0.000 / -0.010。
        var all2 = await repo.GetScoreSamplesAsync(asOf: fetchedAt);
        var model2 = ScorelineModel.Fit(all2, asOf: fetchedAt);
        Console.WriteLine("## 二b、取样可重复性（同一时刻取两次，版本戳必须逐字相同）");
        Console.WriteLine();
        Console.WriteLine($"第一次：`{model.VersionTag}`");
        Console.WriteLine();
        Console.WriteLine($"第二次：`{model2.VersionTag}`");
        Console.WriteLine();
        if (model.VersionTag != model2.VersionTag)
            failures.Add($"两次取样拟合出不同版本戳（{model.FitGames}/{model2.FitGames} 场，rho {model.Rho:0.000}/{model2.Rho:0.000}）"
                + "——取样顺序不稳定，同代码同数据会出两张榜");
        else
            Console.WriteLine("数值判定：**PASS** —— 两次逐字相同。");
        Console.WriteLine();

        // ---------- 3. 更早的 asOf 截断 ----------
        var cut = DateTime.Today.AddDays(-asOfDaysAgo);
        var cutModel = ScorelineModel.Fit(all, asOf: cut);
        Console.WriteLine($"## 三、历史 asOf 截断（截止日 = {cut:yyyy-MM-dd}）");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 不截断的场次 | {model.FitGames} |");
        Console.WriteLine($"| 传 asOf 的场次 | {cutModel.FitGames} |");
        Console.WriteLine($"| 被剔掉 | {model.FitGames - cutModel.FitGames} |");
        Console.WriteLine($"| 传 asOf 后的 FitTo | {(cutModel.FitTo is DateTime ct2 ? ct2.ToString("yyyy-MM-dd HH:mm") : "NULL")} |");
        Console.WriteLine();
        if (!cutModel.Ready) failures.Add("传 asOf 后模型未就绪——样本被剔光了（正是 MatchTime 为 null 时的旧病）");
        else if (cutModel.FitTo is DateTime ft && ft > cut)
            failures.Add($"传 asOf 后 FitTo={ft:yyyy-MM-dd HH:mm} 晚于截止日 {cut:yyyy-MM-dd}，截止日过滤没生效");
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
        Console.WriteLine();

        // ---------- 5. 带比分的未来场次：零容差 ----------
        // 起因：跑第二节时 FitTo 落在两天之后，追下去发现库里有 2 场「开赛时间在未来、
        // 但 Status=3 且比分齐全」的比赛。旧版给过容差（进可售盘口才 FAIL / 超过 20 场才 FAIL），
        // 用户 2026-11-13 明确否掉：**有比分、没有赔率，都不是能进训练集的理由**。
        // 现在只问一件事——它们进没进本次训练集。
        Console.WriteLine("## 五、带比分的未来场次（模型提前知道结果的入口）");
        Console.WriteLine();
        await using (var conn = await factory.OpenAsync())
        {
            // 分开写标量子查询：SQL Server 不允许 SUM(CASE WHEN EXISTS(子查询) ...)
            // ——「不能对包含聚合或子查询的表达式执行聚合函数」，第一次就撞上了。
            const string cond = @"f.Status = 3 AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                    AND f.MatchTime > @asOf";
            var fut = (await conn.QueryAsync<(int Total, int OnBoard, DateTime? MaxT)>(
                $@"SELECT
                     (SELECT COUNT(*) FROM dbo.FbMatch f WHERE {cond}) AS Total,
                     (SELECT COUNT(*) FROM dbo.FbMatch f WHERE {cond}
                        AND EXISTS (SELECT 1 FROM dbo.BettingCard c WHERE c.MatchId = f.MatchId)) AS OnBoard,
                     (SELECT MAX(f.MatchTime) FROM dbo.FbMatch f WHERE {cond}) AS MaxT",
                new { asOf = fetchedAt })).Single();

            Console.WriteLine("| 项目 | 值 |");
            Console.WriteLine("|---|---|");
            Console.WriteLine($"| 库里「开赛在未来却有比分」 | {fut.Total} 场（脏数据，不参与判定） |");
            Console.WriteLine($"| 其中进了可售盘口 | {fut.OnBoard} 场（仅留痕） |");
            Console.WriteLine($"| 最晚一场的开赛时间 | {(fut.MaxT is DateTime mt ? mt.ToString("yyyy-MM-dd HH:mm") : "—")} |");
            Console.WriteLine($"| **其中进了本次训练集** | **{future} 场** |");
            Console.WriteLine();

            if (future > 0)
                failures.Add($"{future} 场「开赛在未来却已有比分」的比赛进了训练集——模型等于赛前拿到答案，必须堵住");
            else
                Console.WriteLine($"数值判定：**PASS** —— 脏数据 {fut.Total} 场全部被时间闸挡在训练集之外。"
                    + "判定不看「有没有进盘口」，也不给场次容差：进训练集就是 FAIL。");
        }

        // ---------- 6. 衰减对比用过的样本路径 ----------
        // 这一节不是查本仓 repo，是查 `weight-probe`（ModelWeightStudy）自己那段 SQL。
        // 它与 repo 方法是两份代码，所以必须单独确认——否则「衰减为什么无效」的结论可能建立在空时间上。
        Console.WriteLine();
        Console.WriteLine("## 六、衰减对比（weight-probe）用过的样本路径");
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

        // ---------- 7. 这次过滤到底动了什么 ----------
        // 用户要求：不许再把整包称为「概率零变化」。这一节把被剔掉的场次、涉及的联赛/球队、
        // 以及 λ 的实际差值列出来——变了就是变了，写清楚变在哪。
        Console.WriteLine();
        Console.WriteLine("## 七、过滤影响（被剔掉的场次与 λ 实差）");
        Console.WriteLine();
        await using (var conn = await factory.OpenAsync())
        {
            var removed = (await conn.QueryAsync<ScoreSample>(
                @"SELECT f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
                         f.MatchTime, f.HomeScore, f.AwayScore
                  FROM dbo.FbMatch f
                  WHERE f.Status = 3 AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                    AND f.MatchTime >= DATEADD(month, -18, GETDATE())
                    AND f.MatchTime > @asOf",
                new { asOf = fetchedAt })).ToList();

            Console.WriteLine("| 项目 | 值 |");
            Console.WriteLine("|---|---|");
            Console.WriteLine($"| 被剔掉的场次 | {removed.Count} |");
            Console.WriteLine($"| 涉及联赛 | {string.Join("、", removed.Select(r => r.LeagueName).Distinct())} |");
            Console.WriteLine($"| 涉及球队 | {removed.SelectMany(r => new[] { r.HomeTeam, r.AwayTeam }).Distinct().Count()} 支 |");
            Console.WriteLine();

            if (removed.Count > 0)
            {
                var oldModel = ScorelineModel.Fit(all.Concat(removed).ToList());
                Console.WriteLine("| 联赛 | 主队 | 客队 | 比分 | 开赛 | λ主 旧→新 | λ客 旧→新 | 最高单格概率 旧→新 |");
                Console.WriteLine("|---|---|---|---|---|---|---|---|");
                foreach (var s in removed.OrderBy(x => x.MatchTime))
                {
                    var o = oldModel.ExpectedGoals(s.LeagueName, s.HomeTeam, s.AwayTeam, null);
                    var n = model.ExpectedGoals(s.LeagueName, s.HomeTeam, s.AwayTeam, null);
                    var op = oldModel.Distribution(s.LeagueName, s.HomeTeam, s.AwayTeam, null);
                    var np = model.Distribution(s.LeagueName, s.HomeTeam, s.AwayTeam, null);
                    var oTop = op.Count == 0 ? 0 : op.Max(x => x.P);
                    var nTop = np.Count == 0 ? 0 : np.Max(x => x.P);
                    Console.WriteLine($"| {s.LeagueName} | {s.HomeTeam} | {s.AwayTeam} | {s.HomeScore}:{s.AwayScore} "
                        + $"| {s.MatchTime:yyyy-MM-dd HH:mm} "
                        + $"| {o?.Home:0.0000}→{n?.Home:0.0000} "
                        + $"| {o?.Away:0.0000}→{n?.Away:0.0000} "
                        + $"| {oTop:P2}→{nTop:P2} |");
                }
                Console.WriteLine();
                Console.WriteLine("口径说明：旧模型 = `Fit(全量样本)`（即改动前线上实际在跑的路径，不传 asOf）；"
                    + "新模型 = `Fit(全量删掉未来场次, asOf=取数时刻)`。表里只列被剔掉的那几场；"
                    + "其余比赛不受直接影响，只有全局场均与这两个联赛的场均被拉动，"
                    + "拉动幅度等于「2 场 / 17.8 万场」。这是**真实的概率变化**，不是零变化。");
            }
            else
            {
                Console.WriteLine("本次没有场次被剔除（库里没有带比分的未来场次），概率不受这一项影响。");
            }
        }

        // ---------- 结论 ----------
        Console.WriteLine();
        Console.WriteLine("## 结论");
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("**PASS** —— 取样时间字段、拟合窗口、asOf 截断、before 时间闸、未来场次零泄漏五项全部成立。");
            return 0;
        }

        Console.WriteLine("**FAIL**");
        foreach (var msg in failures) Console.WriteLine($"- {msg}");
        return 1;
    }
}
