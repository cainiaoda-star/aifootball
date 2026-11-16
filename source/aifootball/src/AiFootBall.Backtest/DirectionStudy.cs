using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 方向检验台（2026-11-09 加）。
///
/// 起因：用户指出「我们连方向都猜错，赔率就更别提了——方向对了才谈得上中奖率」，
/// 举的例子是那不勒斯 vs 拉齐奥 猜负负、实际负平。
///
/// 这件事不能靠感觉回答，必须把「方向」拆成四个可独立计分的量：
///   1. 胜平负（3 档）—— 最粗的方向；
///   2. 让球胜平负（3 档）—— 用户明确点名的「让胜」；
///   3. 总进球（0..7+）—— 用户点名的「2 球」；
///   4. 半全场（9 档）**拆成半场格 + 全场格两问**。
///      这一条最关键：负负 与 负平 在「半场方向」上是同一个判断，
///      若半场格经常对、全场格经常错，说明问题在第二个半场的建模，
///      而不是「方向全错」。两者对应的修法完全不同。
///
/// 每个量都比三样：模型 Top-1、市场 Top-1（竞彩去水后的最高概率档）、
/// 以及一个「无脑基线」（永远猜主场/猜最常见档），后者用来判断前两者是不是真比瞎猜强。
///
/// 口径：
///   · 训练严格早于测试期；
///   · 赔率取开赛前最后一次采样（CapturedAt < MatchTime 的 MAX），即用户实际能买到的收盘价；
///   · 模型半全场＝两段独立泊松（0.45 / 0.55），与线上 HalfFullJoint 同公式。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- direction-probe 2026-10-01 [联赛]
/// </summary>
public static class DirectionStudy
{
    private const double HalfShare = 0.45;
    private const int MaxGoals = 8;

    /// <summary>9 档顺序：先半场（3胜/1平/0负），再全场。与 MilkCombos 一致。</summary>
    private static readonly string[] Combos = { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" };

    private sealed class MRow
    {
        public long MatchId { get; set; }
        public DateTime MatchTime { get; set; }
        public string? LeagueName { get; set; }
        public string Home { get; set; } = "";
        public string Away { get; set; } = "";
        public int HScore { get; set; }
        public int AScore { get; set; }
        public int? Hhs { get; set; }
        public int? Has { get; set; }
        public decimal? Handicap { get; set; }
        public decimal? TotalLine { get; set; }
    }

    private sealed class QRow
    {
        public long MatchId { get; set; }
        public string PlayType { get; set; } = "";
        public string OptionCode { get; set; } = "";
        public decimal Odds { get; set; }
    }

    /// <summary>一场比赛的评估载体：模型/市场各口径的概率 + 实际结果。</summary>
    private sealed class Case
    {
        public long MatchId { get; init; }
        public string League { get; init; } = "";
        public string Home { get; init; } = "";
        public string Away { get; init; } = "";
        public DateTime MatchTime { get; init; }
        public bool EnoughGames { get; init; }
        public int Hs { get; init; }
        public int As { get; init; }
        public int Hhs { get; init; }
        public int Has { get; init; }

        // 胜平负（3 档）：[0]=主胜 [1]=平 [2]=客胜
        public double[]? Model1X2 { get; init; }
        public double[]? Market1X2 { get; init; }

        // 让球胜平负（3 档）：[0]=让胜 [1]=让平 [2]=让负
        public double[]? ModelHcp { get; init; }
        public double[]? MarketHcp { get; init; }
        public decimal? Handicap { get; init; }

        // 总进球：0..7（7=7+）
        public double[]? ModelGoals { get; init; }
        public double[]? MarketGoals { get; init; }

        // 半全场 9 档
        public double[]? ModelBq { get; init; }
        public double[]? MarketBq { get; init; }

        /// <summary>竞彩胜平负报价（半全场基线要按主胜/客胜赔率条件化，取不到就回退联赛分层）。</summary>
        public decimal? SpfHome { get; init; }
        public decimal? SpfAway { get; init; }
    }

    public static async Task<int> RunAsync(string connStr, DateTime testStart, string? league)
    {
        var factory = new SqlConnectionFactory(connStr);

        // ---------- 训练 ----------
        List<(string? Lg, string H, string A, int Hs, int As)> train;
        await using (var c = await factory.OpenAsync())
        {
            train = (await c.QueryAsync<(string?, string, string, int, int)>(
                @"SELECT LeagueName, HomeTeamName, AwayTeamName, HomeScore, AwayScore
                  FROM dbo.FbMatch
                  WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                    AND MatchTime < @testStart",
                new { testStart }, commandTimeout: 600)).ToList();
        }
        if (train.Count < 5000)
        {
            Console.WriteLine($"训练样本只有 {train.Count} 场，不足以拟合。");
            return 1;
        }

        var model = ScorelineModel.Fit(train.Select(t => new ScoreSample
        {
            LeagueName = t.Lg,
            HomeTeam = t.H,
            AwayTeam = t.A,
            HomeScore = t.Hs,
            AwayScore = t.As
        }).ToList());
        if (!model.Ready)
        {
            Console.WriteLine("模型未就绪。");
            return 1;
        }

        Console.WriteLine($"# 方向检验  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"训练样本 {train.Count} 场（严格早于 {testStart:yyyy-MM-dd}）；" +
                          $"测试期从 {testStart:yyyy-MM-dd} 起{(league is null ? "，不限联赛" : $"，联赛={league}")}。");
        Console.WriteLine();

        // ---------- 测试期场次 ----------
        List<MRow> matches;
        await using (var c = await factory.OpenAsync())
        {
            matches = (await c.QueryAsync<MRow>(
                @"SELECT m.MatchId, m.MatchTime, m.LeagueName,
                         m.HomeTeamName AS Home, m.AwayTeamName AS Away,
                         m.HomeScore AS HScore, m.AwayScore AS AScore,
                         m.HalfHomeScore AS Hhs, m.HalfAwayScore AS Has,
                         m.Handicap, ou.TotalLine
                  FROM dbo.FbMatch m
                  OUTER APPLY (SELECT TOP 1 TotalLine FROM dbo.FbOdds
                               WHERE MatchId = m.MatchId AND OddsType = 'OU' AND CompanyId = 0
                                 AND IsOpening = 1
                               ORDER BY OddsId DESC) ou
                  WHERE m.MatchTime >= @testStart
                    AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                    AND (@league IS NULL OR m.LeagueName = @league)
                    AND EXISTS (SELECT 1 FROM dbo.FbJcOddsHistory h WHERE h.MatchId = m.MatchId)
                  ORDER BY m.MatchTime",
                new { testStart, league }, commandTimeout: 600)).ToList();
        }

        if (matches.Count == 0)
        {
            Console.WriteLine("测试期没有同时具备「赛果 + 竞彩赔率」的场次，无法检验。");
            return 0;
        }

        var ids = matches.Select(m => m.MatchId).ToList();
        var quotes = new Dictionary<long, Dictionary<string, Dictionary<string, decimal>>>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await QueryQuotes(factory, chunk);
            foreach (var r in rows)
            {
                if (!quotes.TryGetValue(r.MatchId, out var byPlay))
                    quotes[r.MatchId] = byPlay = new(StringComparer.Ordinal);
                if (!byPlay.TryGetValue(r.PlayType, out var byOpt))
                    byPlay[r.PlayType] = byOpt = new(StringComparer.Ordinal);
                byOpt[r.OptionCode] = r.Odds;
            }
        }

        var cases = new List<Case>(matches.Count);
        foreach (var m in matches)
        {
            if (!quotes.TryGetValue(m.MatchId, out var byPlay)) continue;

            var eg = model.ExpectedGoals(m.LeagueName, m.Home, m.Away, m.TotalLine);
            if (eg is null) continue;

            var cells = model.Distribution(m.LeagueName, m.Home, m.Away, m.TotalLine);
            if (cells.Count == 0) continue;

            var enough = model.TeamGames(m.LeagueName, m.Home) >= 3 && model.TeamGames(m.LeagueName, m.Away) >= 3;

            byPlay.TryGetValue("SPF", out var spf);
            byPlay.TryGetValue("NSPF", out var nspf);
            byPlay.TryGetValue("JQ", out var jq);
            byPlay.TryGetValue("BQ", out var bq);

            cases.Add(new Case
            {
                MatchId = m.MatchId,
                League = m.LeagueName ?? "",
                Home = m.Home,
                Away = m.Away,
                MatchTime = m.MatchTime,
                EnoughGames = enough,
                Hs = m.HScore,
                As = m.AScore,
                Hhs = m.Hhs ?? -1,
                Has = m.Has ?? -1,
                Model1X2 = cells.Count == 0 ? null : Marginal1X2(cells),
                Market1X2 = Devig(spf, ThreeWay),
                ModelHcp = m.Handicap is null ? null : HandicapDist(cells, m.Handicap.Value),
                MarketHcp = Devig(nspf, ThreeWay),
                Handicap = m.Handicap,
                ModelGoals = GoalsDist(cells),
                MarketGoals = DevigFixed(jq, 8, c => int.TryParse(c, out var g) && g is >= 0 and <= 7 ? g : -1),
                ModelBq = Joint(eg.Value.Home, eg.Value.Away),
                MarketBq = Devig(bq, Combos),
                SpfHome = spf is not null && spf.TryGetValue("3", out var so3) ? so3 : null,
                SpfAway = spf is not null && spf.TryGetValue("0", out var so0) ? so0 : null
            });
        }

        Console.WriteLine($"测试期完赛且模型可算的场次 **{cases.Count}** 场" +
                          $"（其中两队各 ≥3 场、线上会真正出手的 **{cases.Count(c => c.EnoughGames)}** 场）。");
        Console.WriteLine();
        Console.WriteLine("每行的「基线」= 无脑永远猜同一个档，用来判断模型和市场是不是真的比瞎猜强。");
        Console.WriteLine();

        // 主口径：全部场次；括注出注口径子集
        var all = cases;
        var gated = cases.Where(c => c.EnoughGames).ToList();

        // ---------- 一、胜平负 ----------
        Section("一、胜平负方向（最粗的「方向」）");
        DirectionBlock("胜平负", all, gated,
            c => c.Model1X2, c => c.Market1X2,
            c => c.Hs > c.As ? 0 : c.Hs == c.As ? 1 : 2,
            p => p[0] > p[1] && p[0] > p[2] ? 0 : p[1] > p[2] ? 1 : 2,
            new[] { "主胜", "平", "客胜" },
            baselineIdx: 0);

        // ---------- 二、让球胜平负 ----------
        Section("二、让球胜平负方向（用户点名的「让胜」）");
        var hcpAll = all.Where(c => c.ModelHcp is not null && c.MarketHcp is not null).ToList();
        var hcpGated = hcpAll.Where(c => c.EnoughGames).ToList();
        if (hcpAll.Count == 0)
        {
            Console.WriteLine("没有同时具备「让球盘口 + 让球赔率」的场次。");
        }
        else
        {
            DirectionBlock("让球", hcpAll, hcpGated,
                c => c.ModelHcp, c => c.MarketHcp,
                c =>
                {
                    var adj = c.Hs - c.As + (int)Math.Round(c.Handicap!.Value);
                    return adj > 0 ? 0 : adj == 0 ? 1 : 2;
                },
                p => p[0] > p[1] && p[0] > p[2] ? 0 : p[1] > p[2] ? 1 : 2,
                new[] { "让胜", "让平", "让负" },
                baselineIdx: 2);
            Console.WriteLine("（让球基线取「让负」：竞彩让球盘里让负出现的频率最高。）");
            Console.WriteLine();
        }

        // ---------- 三、总进球 ----------
        Section("三、总进球（用户点名的「2 球」）");
        var goalAll = all.Where(c => c.ModelGoals is not null && c.MarketGoals is not null).ToList();
        var goalGated = goalAll.Where(c => c.EnoughGames).ToList();
        if (goalAll.Count == 0)
        {
            Console.WriteLine("没有同时具备「总进球赔率」的场次。");
        }
        else
        {
            DirectionBlock("总进球", goalAll, goalGated,
                c => c.ModelGoals, c => c.MarketGoals,
                c => Math.Min(c.Hs + c.As, 7),
                p => Array.IndexOf(p, p.Max()),
                Enumerable.Range(0, 8).Select(i => i == 7 ? "7+" : i.ToString()).ToArray(),
                baselineIdx: 2);
            Console.WriteLine("（总进球基线取「2 球」：全库最高频档。）");
            Console.WriteLine();
        }

        // ---------- 四、半全场：拆成半场格 / 全场格 ----------
        Section("四、半全场 9 档：拆成「半场那一格」和「全场那一格」");
        var bqAll = all.Where(c => c.ModelBq is not null && c.MarketBq is not null && c.Hhs >= 0).ToList();
        if (bqAll.Count == 0)
        {
            Console.WriteLine("没有同时具备「9 档半全场赔率 + 真半场」的场次。");
        }
        else
        {
            var bqGated = bqAll.Where(c => c.EnoughGames).ToList();
            HalfFullBlock(bqAll, bqGated);
        }

        // ---------- 五、赔率下限扫描 ----------
        OddsFloorScan(bqAll);

        // ---------- 六、复刻线上闸门 ----------
        OnlineGateScan(bqAll);

        // ---------- 七、基线 EV 上限该不该有 ----------
        BaselineEvBucketScan(bqAll);

        return 0;
    }

    /// <summary>
    /// 赔率下限扫描。
    ///
    /// 出票不再用赔率地板，只看「能中」。下面的扫描仍把地板当作研究变量，不代表现网还在卡它。
    /// 9 档里最可能发生的那一档赔率通常只有 3.2~3.8（胜胜 3.32、平平 4.70、负负 4.70 这类），
    /// 地板开着的时候等于把最可能的剧本排除在外。
    ///
    /// 这一节把地板当作变量重放，看命中率与回报各怎么变。回报 ≈ 命中率 × 平均赔率，
    /// 两者必须一起看：单看命中率抬高而回报崩塌，等于把亏损摊平，不是变好。
    /// </summary>
    private static void OddsFloorScan(List<Case> cases)
    {
        Section("五、赔率下限扫描：那条「赔率 ≥4 才出票」的地板到底排除了什么");

        var usable = cases.Where(c => c.ModelBq is not null && c.MarketBq is not null).ToList();
        if (usable.Count == 0)
        {
            Console.WriteLine("没有可评估的场次。");
            return;
        }

        // 先看：最可能那一档的赔率分布。低于 4 的比例 = 被地板直接废掉的比例。
        var modalOdds = new List<decimal>();
        foreach (var c in usable)
        {
            if (c.MarketBq is null) continue;
            // 市场 BQ 概率最高档 ≈ 赔率最低档，用来取那一档的真实赔率需要原始赔率，
            // 这里用去水概率反推等效赔率（1/去水概率 = 去水后的公平赔率），再乘回返奖率即原赔率。
            var idx = Array.IndexOf(c.MarketBq, c.MarketBq.Max());
            if (c.MarketBq[idx] <= 0) continue;
            modalOdds.Add((decimal)Math.Round(0.797 / c.MarketBq[idx], 2));
        }
        if (modalOdds.Count > 0)
        {
            var under4 = modalOdds.Count(o => o < 4.0m);
            Console.WriteLine($"最可能那一档的等效赔率：中位数 **{Median(modalOdds):F2}**，" +
                              $"低于 4.0 的占 **{100.0 * under4 / modalOdds.Count:F1}%**（{under4}/{modalOdds.Count}）。");
            Console.WriteLine("这意味着线上那道地板，绝大多数场次都在把最可能发生的剧本直接排除。");
            Console.WriteLine();
        }

        Console.WriteLine("地板 × 概率下限 扫描（每场一注，过闸里取模型概率最高的那档）：");
        Console.WriteLine();
        Console.WriteLine("| 赔率下限 | 赔率上限 | 概率下限 | 注数 | 命中 | 命中率 | 平均赔率 | 平注回报 |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var floor in new[] { 2.0m, 2.5m, 3.0m, 3.5m, 4.0m })
        foreach (var minPct in new[] { 12.0, 15.0, 20.0 })
        {
            int n = 0, hit = 0;
            double oddsSum = 0, retSum = 0;
            foreach (var c in usable)
            {
                var p = c.ModelBq!;
                var mkt = c.MarketBq!;
                var actCode = ActCode(c);
                var actIdx = Array.IndexOf(Combos, actCode);
                if (actIdx < 0) continue;

                var bestIdx = -1;
                double best = -1;
                for (var i = 0; i < 9; i++)
                {
                    if (mkt[i] <= 0) continue;
                    // 等效原赔率 = 返奖率 / 去水概率
                    var odds = 0.797 / mkt[i];
                    if (odds < (double)floor || odds > 15.0) continue;
                    if (p[i] * 100.0 < minPct) continue;
                    if (p[i] > best) { best = p[i]; bestIdx = i; }
                }
                if (bestIdx < 0) continue;
                var od = 0.797 / mkt[bestIdx];
                n++; oddsSum += od;
                if (bestIdx == actIdx) { hit++; retSum += od; }
            }
            Console.WriteLine(n == 0
                ? $"| {floor:F1} | 15 | {minPct:F0}% | 0 | – | – | – | – |"
                : $"| {floor:F1} | 15 | {minPct:F0}% | {n} | {hit} | {100.0 * hit / n:F1}% | {oddsSum / n:F2} | {retSum / n:F3} |");
        }
        Console.WriteLine();
        Console.WriteLine("读法：若「下限 2.5」那几行的命中率明显高于「下限 4.0」，说明命中率是被地板压掉的；" +
                          "若回报同时下降，那就是「命中率换回报」的老交易，不是新 alpha。");
        Console.WriteLine();

        // 「方向对了」的理论上限：完全照市场最看好的那一档买，一次模型都不用。
        // 这一组是用来给上面所有模型方案画天花板的——模型再好也不可能长期超过它太多。
        Console.WriteLine("参照组：完全跟着市场最看好的那一档（= 买最低赔，模型不参与）：");
        Console.WriteLine();
        Console.WriteLine("| 赔率上限 | 注数 | 命中 | 命中率 | 平均赔率 | 平注回报 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var cap in new[] { 3.0m, 5.0m, 8.0m, 15.0m })
        {
            int n = 0, hit = 0;
            double oddsSum = 0, retSum = 0;
            foreach (var c in usable)
            {
                var mkt = c.MarketBq!;
                var actIdx = Array.IndexOf(Combos, ActCode(c));
                if (actIdx < 0) continue;
                var idx = Array.IndexOf(mkt, mkt.Max());
                if (mkt[idx] <= 0) continue;
                var odds = 0.797 / mkt[idx];
                if (odds > (double)cap) continue;
                n++; oddsSum += odds;
                if (idx == actIdx) { hit++; retSum += odds; }
            }
            Console.WriteLine(n == 0
                ? $"| {cap:F1} | 0 | – | – | – | – |"
                : $"| {cap:F1} | {n} | {hit} | {100.0 * hit / n:F1}% | {oddsSum / n:F2} | {retSum / n:F3} |");
        }
        Console.WriteLine();
    }

    private static string ActCode(Case c) => $"{Res(c.Hhs, c.Has)}-{Res(c.Hs, c.As)}";

    /// <summary>
    /// 复刻线上出票闸，扫赔率地板。
    ///
    /// 为什么必须复刻：`PassesBaselineEv` 给半全场设了一条 **EV 带**
    /// —— 低于 返奖率×1.00 拦（觉得不值），高于 返奖率×1.45 也拦（觉得基线离谱）。
    /// 这条带把「能出的票」框在一个很窄的 EV 区间里，所以「把地板降到 2.0 会怎样」
    /// 不能靠直觉，只能按真实闸门跑一遍：地板放宽后那些低赔档，到底是过带还是被带拦掉。
    ///
    /// 另外一个必须复刻的点：选注是按 **Blend 后概率**排序（0.7 模型 + 0.3 市场），
    /// 不是按 EV。这一点决定了「地板放宽」实际会选到哪一档。
    /// </summary>
    private static void OnlineGateScan(List<Case> cases)
    {
        Section("六、复刻线上出票闸（含基线 EV 带）：地板放宽到底会不会真的出票");

        var payback = MilkPickEngine.Payout(JcPlayTypes.Bq);
        const decimal edgeCap = 1.45m;   // MilkPickEngine.MaxBaselineEdgeRatio
        const decimal bqFloor = 1.00m;   // MilkPickEngine.MinBaselineEvRatioBq

        var usable = cases.Where(c => c.ModelBq is not null && c.MarketBq is not null).ToList();
        if (usable.Count == 0)
        {
            Console.WriteLine("没有可评估的场次。");
            return;
        }

        Console.WriteLine($"返奖率 {payback:F3}；EV 允许区间 [{payback * bqFloor:F3}, {payback * edgeCap:F3}]；" +
                          $"选注按 Blend(0.7 模型 + 0.3 市场) 排序。样本 {usable.Count} 场。");
        Console.WriteLine();
        Console.WriteLine("| 赔率下限 | 赔率上限 | 过闸注数 | 命中 | 命中率 | 平均赔率 | 平注回报 | 有候选但全被EV带拦掉的场次 |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");

        foreach (var maxOdds in new[] { 8.0m, 15.0m })
        foreach (var floor in new[] { 2.0m, 3.0m, 4.0m })
        {
            int n = 0, hit = 0, blockedMatches = 0;
            double oddsSum = 0, retSum = 0;

            foreach (var c in usable)
            {
                var model = c.ModelBq!;
                var mkt = c.MarketBq!;
                var actIdx = Array.IndexOf(Combos, ActCode(c));
                if (actIdx < 0) continue;

                var candidates = new List<(int Idx, double Blended, decimal Odds)>();
                for (var i = 0; i < 9; i++)
                {
                    if (mkt[i] <= 0) continue;
                    var odds = (decimal)((double)payback / mkt[i]);   // 去水概率 → 原始赔率
                    if (odds < floor || odds > maxOdds) continue;
                    var blended = 0.7 * model[i] + 0.3 * mkt[i];
                    // 模型/市场分歧上限（MilkPickEngine.BqModelOverMarketMax = 2.5）
                    if (mkt[i] > 0 && blended > mkt[i] * 2.5) continue;
                    candidates.Add((i, blended, odds));
                }
                if (candidates.Count == 0) continue;

                var passed = new List<(int Idx, double Blended, decimal Odds)>();
                foreach (var cand in candidates)
                {
                    var bp = OptionBaseline.Pct(c.League, JcPlayTypes.Bq, Combos[cand.Idx], c.SpfHome, c.SpfAway);
                    if (bp is not > 0m) { passed.Add(cand); continue; }   // 无基线放行
                    var realEv = bp.Value / 100m * cand.Odds;
                    if (realEv > payback * edgeCap) continue;              // 基线说好得离谱 → 拦
                    if (realEv < payback * bqFloor) continue;              // 基线说不够值 → 拦
                    passed.Add(cand);
                }
                if (passed.Count == 0) { blockedMatches++; continue; }

                var best = passed.OrderByDescending(x => x.Blended).First();
                n++; oddsSum += (double)best.Odds;
                if (best.Idx == actIdx) { hit++; retSum += (double)best.Odds; }
            }

            Console.WriteLine(n == 0
                ? $"| {floor:F1} | {maxOdds:F0} | 0 | – | – | – | – | {blockedMatches} |"
                : $"| {floor:F1} | {maxOdds:F0} | {n} | {hit} | {100.0 * hit / n:F1}% | {oddsSum / n:F2} | {retSum / n:F3} | {blockedMatches} |");
        }

        Console.WriteLine();
        Console.WriteLine("「有候选但全被 EV 带拦掉」那一列是关键：它说明地板放宽后，问题的性质从「不能出票」变成「能出但被 EV 带否掉」。");
        Console.WriteLine();
    }

    /// <summary>
    /// 七、基线 EV 上限到底该不该有。
    ///
    /// `MaxBaselineEdgeRatio = 1.45`（即 EV &gt; 返奖率×1.45 就拦）的理由是「基线表太粗，
    /// 算出这么高的 EV 一定是基线错了」。但它同时也拦掉了一类真实情况：
    /// **低赔档（最可能发生的剧本）本来 EV 就高**，因为条件化基线在低赔区间给得很满。
    ///
    /// 判据只有一个：把这些「EV 高得离谱」的档单独拿出来，看它们的**实际命中率**是否真能达到
    /// 基线说的那个数。能达到 → 我们一直在拒掉真钱，上限该抬；达不到 → 上限是对的，
    /// 基线虚高，别碰。
    ///
    /// 每场 9 档全部入栈（不是只入被选中的那档），样本量 = 场次 × 9，比只看选中档稳得多。
    /// </summary>
    private static void BaselineEvBucketScan(List<Case> cases)
    {
        Section("七、基线 EV 上限（1.45×）该不该有：把「EV 高得离谱」的档单独验一遍");

        var payback = MilkPickEngine.Payout(JcPlayTypes.Bq);
        var buckets = new (string Label, double Lo, double Hi)[]
        {
            ("EV < 0.797（低于返奖率，必被下限拦）", 0, 0.797),
            ("EV 0.797~1.000（也必被拦）", 0.797, 1.000),
            ("EV 1.000~1.156（线上唯一放行区间）", 1.000, 1.156),
            ("EV 1.156~1.600（被上限拦掉）", 1.156, 1.600),
            ("EV > 1.600（被上限拦掉，基线说好到不真实）", 1.600, 999)
        };

        var cells = new List<(double Ev, double BaselinePct, decimal Odds, bool Win)>();
        foreach (var c in cases)
        {
            if (c.ModelBq is null || c.MarketBq is null) continue;
            var actCode = ActCode(c);
            for (var i = 0; i < 9; i++)
            {
                if (c.MarketBq[i] <= 0) continue;
                var odds = (decimal)Math.Round((double)payback / c.MarketBq[i], 2);
                if (odds < 2.0m || odds > 15m) continue;
                var bp = OptionBaseline.Pct(c.League, JcPlayTypes.Bq, Combos[i], c.SpfHome, c.SpfAway);
                if (bp is not > 0m) continue;
                cells.Add(((double)(bp.Value / 100m * odds), (double)bp.Value, odds, Combos[i] == actCode));
            }
        }

        if (cells.Count == 0)
        {
            Console.WriteLine("没有可用样本。");
            return;
        }

        Console.WriteLine($"样本 = {cells.Count} 个「场次×档位」观测（每场 9 档全入，不是只算选中档）。");
        Console.WriteLine();
        Console.WriteLine("| EV 区间 | 档位观测 | 基线预言命中率 | 实际命中率 | 偏差 | 平均赔率 | 该组平注回报 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var b in buckets)
        {
            var sub = cells.Where(x => x.Ev >= b.Lo && x.Ev < b.Hi).ToList();
            if (sub.Count == 0) { Console.WriteLine($"| {b.Label} | 0 | – | – | – | – | – |"); continue; }
            var predHit = sub.Average(x => x.BaselinePct);
            var actHit = 100.0 * sub.Count(x => x.Win) / sub.Count;
            var ret = sub.Sum(x => x.Win ? (double)x.Odds : 0.0) / sub.Count;
            Console.WriteLine($"| {b.Label} | {sub.Count} | {predHit:F1}% | {actHit:F1}% | {actHit - predHit:+0.0;-0.0}pp | " +
                              $"{sub.Average(x => (double)x.Odds):F2} | {ret:F3} |");
        }
        Console.WriteLine();
        Console.WriteLine("读法：若「EV > 1.156」那两组的实际命中率**接近甚至超过**基线预言，说明基线没虚高、" +
                          "上限一直在拒掉真钱；若实际显著低于预言，说明上限是对的。");
        Console.WriteLine();
    }

    private static decimal Median(List<decimal> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s[s.Count / 2];
    }

    // ======================================================================

    /// <summary>3 档通用：模型 vs 市场 vs 基线，Top-1 命中率 + Brier + 交叉表。</summary>
    private static void DirectionBlock(
        string label,
        List<Case> all, List<Case> gated,
        Func<Case, double[]?> modelSel,
        Func<Case, double[]?> marketSel,
        Func<Case, int> actualSel,
        Func<double[], int> topSel,
        string[] names,
        int baselineIdx)
    {
        var both = all.Where(c => modelSel(c) is not null && marketSel(c) is not null).ToList();
        if (both.Count == 0)
        {
            Console.WriteLine($"没有可评估的{label}场次。");
            Console.WriteLine();
            return;
        }

        int mHit = 0, kHit = 0, bHit = 0;
        int mRightKWrong = 0, mWrongKRight = 0, bothRight = 0, bothWrong = 0;
        double mBrier = 0, kBrier = 0;
        var samples = new List<(int Actual, int ModelTop, int MarketTop)>();

        foreach (var c in both)
        {
            var mp = modelSel(c)!;
            var kp = marketSel(c)!;
            var act = actualSel(c);
            var mt = topSel(mp);
            var kt = topSel(kp);

            if (mt == act) mHit++;
            if (kt == act) kHit++;
            if (baselineIdx == act) bHit++;
            if (mt == act && kt == act) bothRight++;
            else if (mt == act) mRightKWrong++;
            else if (kt == act) mWrongKRight++;
            else bothWrong++;

            mBrier += MultiBrier(mp, act);
            kBrier += MultiBrier(kp, act);
            samples.Add((act, mt, kt));
        }

        var n = both.Count;
        Console.WriteLine("| 口径 | 场次 | Top-1 命中率 | 多分类 Brier |");
        Console.WriteLine("|---|---|---|---|");
        Console.WriteLine($"| 模型 | {n} | {100.0 * mHit / n:F1}% | {mBrier / n:F4} |");
        Console.WriteLine($"| 市场（竞彩去水） | {n} | {100.0 * kHit / n:F1}% | {kBrier / n:F4} |");
        Console.WriteLine($"| 无脑基线（永远猜 {names[baselineIdx]}） | {n} | {100.0 * bHit / n:F1}% | – |");
        Console.WriteLine();

        Console.WriteLine($"交叉：{bothRight} 场两者都中，模型独中 {mRightKWrong}，市场独中 {mWrongKRight}，都没中 {bothWrong}。");
        Console.WriteLine($"（模型独中 − 市场独中 = **{(mRightKWrong - mWrongKRight):+0;-0}** 场；" +
                          $"正数才是模型真有额外信息，负数说明模型是在给市场添乱。）");
        Console.WriteLine();

        // 分档：模型和市场各自最看好的那档，命中率的差异
        Console.WriteLine("模型最看好的档位分布（看它是不是总押同一类结果）：");
        Console.WriteLine("| 档位 | 模型押注次数 | 其中命中 | 市场押注次数 | 市场命中 |");
        Console.WriteLine("|---|---|---|---|---|");
        for (var i = 0; i < names.Length; i++)
        {
            var mi = samples.Count(s => s.ModelTop == i);
            var mh = samples.Count(s => s.ModelTop == i && s.Actual == i);
            var ki = samples.Count(s => s.MarketTop == i);
            var kh = samples.Count(s => s.MarketTop == i && s.Actual == i);
            Console.WriteLine($"| {names[i]} | {mi} | {mh} | {ki} | {kh} |");
        }
        Console.WriteLine();

        if (gated.Count > 0 && gated.Count != all.Count)
        {
            var sub = all.Where(c => c.EnoughGames && modelSel(c) is not null && marketSel(c) is not null).ToList();
            int sm = 0, sk = 0;
            foreach (var c in sub)
            {
                var act = actualSel(c);
                if (topSel(modelSel(c)!) == act) sm++;
                if (topSel(marketSel(c)!) == act) sk++;
            }
            Console.WriteLine($"出注口径子集（两队各 ≥3 场，{sub.Count} 场）：模型 {100.0 * sm / sub.Count:F1}%、" +
                              $"市场 {100.0 * sk / sub.Count:F1}%。");
            Console.WriteLine();
        }
    }

    /// <summary>半全场：把 9 档拆成「半场格对不对」「全场格对不对」两问。</summary>
    private static void HalfFullBlock(List<Case> all, List<Case> gated)
    {
        var n = all.Count;
        int modelCell = 0, marketCell = 0;
        int modelHalf = 0, modelFull = 0, marketHalf = 0, marketFull = 0;
        int modelHalfMarginalTop = 0, modelFullMarginalTop = 0;

        // 「半场猜对之后，全场猜对多少」—— 专门衡量第二个半场的建模质量
        int halfRightN = 0, halfRightFullRight = 0;
        // 实际结果上，模型给的概率 vs 市场给的概率（越低说明越没认出来）
        double modelPOnActual = 0, marketPOnActual = 0;

        // 逐档偏差：模型和市场对 9 档的平均概率 vs 实际频率
        var modelAvg = new double[9];
        var marketAvg = new double[9];
        var freq = new int[9];

        foreach (var c in all)
        {
            var mp = c.ModelBq!;
            var kp = c.MarketBq!;
            var actHalf = Res(c.Hhs, c.Has);
            var actFull = Res(c.Hs, c.As);
            var actCode = $"{actHalf}-{actFull}";
            var actIdx = Array.IndexOf(Combos, actCode);
            if (actIdx < 0) continue;

            for (var i = 0; i < 9; i++)
            {
                modelAvg[i] += mp[i];
                marketAvg[i] += kp[i];
            }
            freq[actIdx]++;

            var mTop = Combos[Array.IndexOf(mp, mp.Max())];
            var kTop = Combos[Array.IndexOf(kp, kp.Max())];
            if (mTop == actCode) modelCell++;
            if (kTop == actCode) marketCell++;

            // 半场那一格：模型 Top-1 的半场判断是否等于实际半场
            if (mTop[0] == actHalf[0]) modelHalf++;
            if (kTop[0] == actHalf[0]) marketHalf++;
            // 全场那一格
            if (mTop[2] == actFull[0]) modelFull++;
            if (kTop[2] == actFull[0]) marketFull++;

            // 边际：把 9 档对半场求和，取最大
            var mHalfM = Marginal(mp, 0);
            var mFullM = Marginal(mp, 1);
            if (TopIdx3(mHalfM) == actHalf[0] - '0') modelHalfMarginalTop++;
            if (TopIdx3(mFullM) == actFull[0] - '0') modelFullMarginalTop++;

            if (mTop[0] == actHalf[0])
            {
                halfRightN++;
                if (mTop[2] == actFull[0]) halfRightFullRight++;
            }

            modelPOnActual += mp[actIdx];
            marketPOnActual += kp[actIdx];
        }

        Console.WriteLine("| 口径 | 场次 | 9 档整格命中 | 半场格命中 | 全场格命中 |");
        Console.WriteLine("|---|---|---|---|---|");
        Console.WriteLine($"| 模型 Top-1 | {n} | {100.0 * modelCell / n:F1}% | {100.0 * modelHalf / n:F1}% | {100.0 * modelFull / n:F1}% |");
        Console.WriteLine($"| 市场 Top-1 | {n} | {100.0 * marketCell / n:F1}% | {100.0 * marketHalf / n:F1}% | {100.0 * marketFull / n:F1}% |");
        Console.WriteLine($"| 模型边际 Top-1（不跟 9 档，只看半场/全场单独的 3 档） | {n} | – | {100.0 * modelHalfMarginalTop / n:F1}% | {100.0 * modelFullMarginalTop / n:F1}% |");
        Console.WriteLine();

        Console.WriteLine($"**半场猜对之后，全场也猜对：{halfRightFullRight} / {halfRightN} " +
                          $"({(halfRightN == 0 ? 0 : 100.0 * halfRightFullRight / halfRightN):F1}%)**");
        Console.WriteLine("（这一格就是「第二个半场」的建模质量。若明显低于「全场格命中率」，" +
                          "说明模型认得出谁先进球、认不出后面怎么走。）");
        Console.WriteLine();

        Console.WriteLine($"平均给「实际发生那一档」的概率：模型 {100.0 * modelPOnActual / n:F2}%，市场 {100.0 * marketPOnActual / n:F2}%。");
        Console.WriteLine();

        Console.WriteLine("逐档偏差（模型/市场对该档的平均概率 vs 实际频率）：");
        Console.WriteLine("| 档位 | 实际频率 | 模型平均 | 偏差 | 市场平均 | 偏差 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        for (var i = 0; i < 9; i++)
        {
            var f = 100.0 * freq[i] / n;
            var ma = 100.0 * modelAvg[i] / n;
            var ka = 100.0 * marketAvg[i] / n;
            Console.WriteLine($"| {Name(Combos[i])} | {f:F2}% | {ma:F2}% | {ma - f:+0.00;-0.00}pp | {ka:F2}% | {ka - f:+0.00;-0.00}pp |");
        }
        Console.WriteLine();

        if (gated.Count > 0 && gated.Count != all.Count)
        {
            int sm = 0, sh = 0, sf = 0;
            foreach (var c in gated)
            {
                var mp = c.ModelBq!;
                var actCode = $"{Res(c.Hhs, c.Has)}-{Res(c.Hs, c.As)}";
                var mTop = Combos[Array.IndexOf(mp, mp.Max())];
                if (mTop == actCode) sm++;
                if (mTop[0] == actCode[0]) sh++;
                if (mTop[2] == actCode[2]) sf++;
            }
            Console.WriteLine($"出注口径子集（{gated.Count} 场）：整格 {100.0 * sm / gated.Count:F1}%、" +
                              $"半场格 {100.0 * sh / gated.Count:F1}%、全场格 {100.0 * sf / gated.Count:F1}%。");
            Console.WriteLine();
        }

        // 「半场对、全场错」的典型错法：模型把全场判到哪去了
        Console.WriteLine("最常出现的错法（模型半场猜对、全场猜错时，它把全场判成了什么）：");
        Console.WriteLine("| 模型判的全场 | 次数 |");
        Console.WriteLine("|---|---|");
        var wrongFull = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in all)
        {
            var mp = c.ModelBq!;
            var actHalf = Res(c.Hhs, c.Has);
            var actFull = Res(c.Hs, c.As);
            var mTop = Combos[Array.IndexOf(mp, mp.Max())];
            if (mTop[0] != actHalf[0] || mTop[2] == actFull[0]) continue;
            var key = $"{Name(mTop)}（实际是 {Name($"{actHalf}-{actFull}")}）";
            wrongFull[key] = wrongFull.GetValueOrDefault(key) + 1;
        }
        foreach (var kv in wrongFull.OrderByDescending(k => k.Value).Take(10))
            Console.WriteLine($"| {kv.Key} | {kv.Value} |");
        Console.WriteLine();
        Console.WriteLine($"合计 {wrongFull.Values.Sum()} 场属于「半场对、全场错」，" +
                              $"占全部 {n} 场的 {(n == 0 ? 0 : 100.0 * wrongFull.Values.Sum() / n):F1}%。");
        Console.WriteLine();
    }

    // ======================================================================

    private static async Task<List<QRow>> QueryQuotes(SqlConnectionFactory factory, long[] ids)
    {
        await using var c = await factory.OpenAsync();
        return (await c.QueryAsync<QRow>(
            @"SELECT MatchId, PlayType, OptionCode, Odds FROM (
                  SELECT h.MatchId, h.PlayType, h.OptionCode, h.Odds,
                         ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.PlayType, h.OptionCode
                                            ORDER BY h.CapturedAt DESC) AS rn
                  FROM dbo.FbJcOddsHistory h
                  JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                  WHERE h.MatchId IN @ids
                    AND h.CapturedAt < m.MatchTime
                    AND h.PlayType IN ('SPF', 'NSPF', 'JQ', 'BQ')
              ) t WHERE rn = 1 AND Odds > 1",
            new { ids }, commandTimeout: 600)).ToList();
    }

    private static IEnumerable<long[]> Chunk(List<long> ids, int size = 300)
    {
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.Skip(i).Take(size).ToArray();
    }

    private static readonly string[] ThreeWay = { "3", "1", "0" };

    /// <summary>按固定档位表去水：返回与 codes 同序的概率数组；缺档返回 null。</summary>
    private static double[]? Devig(Dictionary<string, decimal>? odds, string[] codes)
    {
        if (odds is null || odds.Count == 0) return null;
        var inv = new double[codes.Length];
        double sum = 0;
        for (var i = 0; i < codes.Length; i++)
        {
            if (!odds.TryGetValue(codes[i], out var o) || o <= 1m) return null;
            inv[i] = 1.0 / (double)o;
            sum += inv[i];
        }
        if (sum <= 0) return null;
        for (var i = 0; i < inv.Length; i++) inv[i] /= sum;
        return inv;
    }

    /// <summary>去水到固定长度的数组（总进球用，档位不连续）。</summary>
    private static double[]? DevigFixed(Dictionary<string, decimal>? odds, int len, Func<string, int> map)
    {
        if (odds is null || odds.Count == 0) return null;
        var inv = new double[len];
        double sum = 0;
        foreach (var (code, o) in odds)
        {
            var idx = map(code);
            if (idx < 0 || idx >= len || o <= 1m) continue;
            inv[idx] = 1.0 / (double)o;
            sum += inv[idx];
        }
        if (sum <= 0) return null;
        for (var i = 0; i < len; i++) inv[i] /= sum;
        return inv;
    }

    private static double[] Marginal1X2(IEnumerable<(int H, int A, double P)> cells)
    {
        var acc = new double[3];
        foreach (var (h, a, p) in cells)
            acc[h > a ? 0 : h == a ? 1 : 2] += p;
        Normalize(acc);
        return acc;
    }

    /// <summary>让球三档：把盘口加进净胜球，再分胜/平/负。</summary>
    private static double[] HandicapDist(IEnumerable<(int H, int A, double P)> cells, decimal handicap)
    {
        var acc = new double[3];
        var hcp = (int)Math.Round(handicap);
        foreach (var (h, a, p) in cells)
        {
            var adj = h - a + hcp;
            acc[adj > 0 ? 0 : adj == 0 ? 1 : 2] += p;
        }
        Normalize(acc);
        return acc;
    }

    private static double[] GoalsDist(IEnumerable<(int H, int A, double P)> cells)
    {
        var acc = new double[8];
        foreach (var (h, a, p) in cells)
            acc[Math.Min(h + a, 7)] += p;
        Normalize(acc);
        return acc;
    }

    private static void Normalize(double[] a)
    {
        var s = a.Sum();
        if (s <= 0) return;
        for (var i = 0; i < a.Length; i++) a[i] /= s;
    }

    private static double MultiBrier(double[] probs, int actual)
    {
        double s = 0;
        for (var i = 0; i < probs.Length; i++)
        {
            var y = i == actual ? 1.0 : 0.0;
            s += (probs[i] - y) * (probs[i] - y);
        }
        return s;
    }

    /// <summary>9 档对某一半场求和（side=0 半场，side=1 全场），得到 3 档边际。</summary>
    private static double[] Marginal(double[] joint, int side)
    {
        var acc = new double[3];
        for (var i = 0; i < 9; i++)
        {
            var code = Combos[i];
            var ch = side == 0 ? code[0] : code[2];
            acc[ch == '3' ? 0 : ch == '1' ? 1 : 2] += joint[i];
        }
        Normalize(acc);
        return acc;
    }

    private static int TopIdx3(double[] p) => p[0] >= p[1] && p[0] >= p[2] ? 0 : p[1] >= p[2] ? 1 : 2;

    private static string Res(int h, int a) => h > a ? "3" : h == a ? "1" : "0";

    private static string Name(string combo) => combo switch
    {
        "3-3" => "胜胜",
        "3-1" => "胜平",
        "3-0" => "胜负",
        "1-3" => "平胜",
        "1-1" => "平平",
        "1-0" => "平负",
        "0-3" => "负胜",
        "0-1" => "负平",
        "0-0" => "负负",
        _ => combo
    };

    /// <summary>9 档联合，与线上 MilkPickEngine.HalfFullJoint 同公式（两段独立泊松）。</summary>
    private static double[]? Joint(double home, double away)
    {
        var first = Grid(home * HalfShare, away * HalfShare);
        var second = Grid(home * (1 - HalfShare), away * (1 - HalfShare));
        var m1 = first.Sum(x => x.P);
        var m2 = second.Sum(x => x.P);
        if (m1 <= 0 || m2 <= 0) return null;

        var acc = new double[9];
        foreach (var a in first)
        foreach (var b in second)
        {
            var ht = a.H > a.A ? 0 : a.H == a.A ? 1 : 2;
            var fh = a.H + b.H;
            var fa = a.A + b.A;
            var ft = fh > fa ? 0 : fh == fa ? 1 : 2;
            acc[ht * 3 + ft] += a.P * b.P;
        }
        for (var i = 0; i < 9; i++) acc[i] /= m1 * m2;
        return acc;
    }

    private static List<(int H, int A, double P)> Grid(double lh, double la)
    {
        var list = new List<(int, int, double)>((MaxGoals + 1) * (MaxGoals + 1));
        for (var h = 0; h <= MaxGoals; h++)
        for (var a = 0; a <= MaxGoals; a++)
            list.Add((h, a, Poisson(h, lh) * Poisson(a, la)));
        return list;
    }

    private static double Poisson(int k, double lambda)
    {
        if (k < 0) return 0;
        if (lambda < 1e-6) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }

    private static void Section(string title)
    {
        Console.WriteLine($"## {title}");
        Console.WriteLine();
    }
}
