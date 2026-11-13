using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 回测/校准诊断台。只读库，不改任何业务表。
///
/// 存在的理由（2026-11-05）：大奶榜连着出烂注，根因不是某条规则写错，而是没人量过
/// 「模型给的概率和真实发生频率差多少」。没有这张表，所有改动都是猜的。
///
/// 关键约束：**必须按时间切分训练/测试**。第一版直接拿全量样本 Fit 再评测同一批比赛，
/// 模型 Brier 0.52「优于」市场 0.578——那是泄漏：每队只有 3.9 场样本，一场就是 25% 权重，
/// 模型等于在背答案。这个数字不能信，也不能拿去指导任何决策。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release [-- 0.67]
///       参数是训练集占比（按开赛时间前向切分），默认 0.67。
/// </summary>
public static class Program
{
    private const string DefaultConn =
        "Server=localhost,1433;Database=AiFootBall;Integrated Security=True;TrustServerCertificate=True;Encrypt=False";

    public sealed class Row
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int HalfHomeScore { get; set; }
        public int HalfAwayScore { get; set; }
        public decimal HomeWin { get; set; }
        public decimal Draw { get; set; }
        public decimal AwayWin { get; set; }
        public decimal? TotalLine { get; set; }
        public decimal? OverWater { get; set; }
        public decimal? UnderWater { get; set; }
    }

    public sealed class Sample
    {
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    /// <summary>北单历史平手盘一行：原始胜负欧赔 + 实际比分，无让球，可直接当市场标尺。</summary>
    public sealed class BdRow
    {
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public decimal HomeWin { get; set; }
        public decimal Draw { get; set; }
        public decimal AwayWin { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    public static async Task<int> Main(string[] args)
    {
        var connStr = args.FirstOrDefault(a => a.StartsWith("Server=", StringComparison.OrdinalIgnoreCase)) ?? DefaultConn;
        var trainRatio = args.Select(a => double.TryParse(a, out var v) ? v : 0)
            .FirstOrDefault(v => v > 0.3 && v < 0.95, 0.67);

        var factory = new SqlConnectionFactory(connStr);

        // 半全场「下半场 λ 按排名条件化」的专项验证（2026-11-08）。
        // 独立入口，不改变本回测台原有的任何一节，也不碰线上引擎。
        if (args.Any(a => a.Equals("hf-lambda", StringComparison.OrdinalIgnoreCase)))
            return await HalfFullLambdaStudy.RunAsync(connStr);

        // 时间衰减 × 混合权重的联合 A/B（2026-11-11 加，用户批准「先做1」）。
        // 只读。同时回答「18 个月等权该不该改成按时间衰减」和「MarketWeight=0.3 方向对不对」，
        // 因为两者的效应会互相掩盖，必须放在同一张表里比。
        if (args.Any(a => a.Equals("weight-probe", StringComparison.OrdinalIgnoreCase)))
            return await ModelWeightStudy.RunAsync(connStr, trainRatio);

        // 半全场口径的「模型 vs 盘口」对照（2026-11-11 加）。
        // 只读。存在的理由：MarketWeight=0.3 的原始证据（约 500 场、模型全面压过市场）
        // 在代码库里找不到可重跑的实现，而回测台全目录没有任何一处过滤占位赔率（Odds=1.00）——
        // 这条过滤是本项目已经被真实事故血证过的必需步骤。这个 study 就是把它重跑一遍。
        if (args.Any(a => a.Equals("exotic-bq", StringComparison.OrdinalIgnoreCase)))
            return await ExoticWeightStudy.RunAsync(connStr);

        // 市场权重的**下注级**回放（2026-11-11 加）：按玩法分别 sweep w，报投入/返还/ROI/命中率，
        // 并按时间切分调参段与验证段。Brier 只能说「准不准」，说不了「按这个 w 买下去赚不赚」。
        // 只读。可选参数：调参段占比（默认 0.6）。
        if (args.Any(a => a.Equals("weight-replay", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("weight-replay", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
            var share = double.TryParse(rest.ElementAtOrDefault(0), out var sv) ? sv : 0.6;
            return await WeightReplayStudy.RunAsync(connStr, share);
        }

        // 单场 λ 诊断（2026-11-08，埃尔切 vs 贝蒂斯「胜胜 19.7%」事件后加）：
        // 只打印「认不认得这支队 / 多少样本 / λ 是否退回了联赛均值」，不参与任何计算。
        if (args.Any(a => a.Equals("team-check", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("team-check", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
            var h = rest.ElementAtOrDefault(0) ?? "埃尔切";
            var a2 = rest.ElementAtOrDefault(1) ?? "贝蒂斯";
            var lg2 = rest.ElementAtOrDefault(2) ?? "西甲";
            return await TeamLambdaStudy.RunAsync(connStr, h, a2, lg2);
        }

        // 「模型纯值 / 市场隐含」单列闸的阈值校准（2026-11-08，热那亚负胜@36 事件后补）。
        // 只读，不改线上任何阈值；产出是「比值分箱 → 命中率/回报」，用来判断 0.70 该不该动。
        if (args.Any(a => a.Equals("counter-gate", StringComparison.OrdinalIgnoreCase)))
        {
            var startArg = args.SkipWhile(a => !a.Equals("counter-gate", StringComparison.OrdinalIgnoreCase))
                               .Skip(1)
                               .FirstOrDefault(a => DateTime.TryParse(a, out _));
            var testStart = DateTime.TryParse(startArg, out var ts) ? ts : new DateTime(2026, 7, 1);
            return await CounterGateStudy.RunAsync(connStr, testStart);
        }

        // 「赔率档 × 档位」条件化基线值不值得建（2026-11-08，江原负负 EV 报错事件后补）。
        // 只读；产出三个数：条件化前后的 Brier 差、各赔率箱的真实 ROI、边际基线的高估方向。
        if (args.Any(a => a.Equals("odds-cond", StringComparison.OrdinalIgnoreCase)))
        {
            var startArg = args.SkipWhile(a => !a.Equals("odds-cond", StringComparison.OrdinalIgnoreCase))
                               .Skip(1)
                               .FirstOrDefault(a => DateTime.TryParse(a, out _));
            var testStart = DateTime.TryParse(startArg, out var ts) ? ts : new DateTime(2026, 5, 1);
            return await OddsConditionStudy.RunAsync(connStr, testStart);
        }

        // 真实取样链路探针（2026-11-13 加）。只读。
        // 起因：11-13 注单的版本戳是 `dc1|?..?|n=178179|...`，根因是取样 SQL 没 SELECT f.MatchTime，
        // 拟合窗口恒为 ?、衰减 anchor 恒为 null、Fit(asOf) 会把样本全剔光。
        // 这类缺陷编译能过、单测能过、线上不报错，只能拿真库跑一遍完整链路才看得见。
        if (args.Any(a => a.Equals("fit-probe", StringComparison.OrdinalIgnoreCase)))
        {
            var fi = Array.FindIndex(args, a => a.Equals("fit-probe", StringComparison.OrdinalIgnoreCase));
            var days = fi >= 0 && fi + 1 < args.Length && int.TryParse(args[fi + 1], out var dv) ? dv : 30;
            return await FitProbe.RunAsync(connStr, days);
        }

        // ρ 前向对比台（2026-11-13 加）。只读。
        // 起因：取样加 ORDER BY 后 ρ 从「+0.010/0.000/−0.010 随机跳」稳定到 −0.060，
        // 而 ρ 直接改低比分格概率（λh·λa≈1.54 时 0:0 相对 +11%）。
        // 「可复现」与「落在常见区间」都不是上线依据，必须在前向测试集上比。
        if (args.Any(a => a.Equals("rho-study", StringComparison.OrdinalIgnoreCase)))
            return await RhoStudy.RunAsync(connStr);

        // 审计表端到端自检（2026-11-12 加）。会写库，但只写 2090-01-01 这个远离真实数据的日期，
        // 跑完自动清理。存在的理由：两套手写 INSERT 一旦列数不齐，线上会被 try/catch 静默吞掉。
        if (args.Any(a => a.Equals("audit-check", StringComparison.OrdinalIgnoreCase)))
        {
            // 可选第二参数 = 批量行数（默认 0：只验列对不对，不压测）。
            // 给个 1500 就能看到「一次出榜的存档要写多久」，用来判断它会不会把作业拖过 10 分钟上限。
            var ai = Array.FindIndex(args, a => a.Equals("audit-check", StringComparison.OrdinalIgnoreCase));
            var bulk = ai >= 0 && ai + 1 < args.Length && int.TryParse(args[ai + 1], out var bn) ? bn : 0;
            // 2026-11-13 第四轮：默认只允许在隔离库（库名含 audit/test/sandbox/tmp/probe）执行。
            // 它会建表、加列、写探针行，属于结构变更——用户已明确要求「后续自检移到隔离测试库」。
            var allowProd = args.Any(a => a.Equals("--allow-prod-unsafe", StringComparison.OrdinalIgnoreCase));
            return await AuditCheck.RunAsync(connStr, bulk, allowProd);
        }

        // 历史战绩口径核对（2026-11-13 第四轮）。**只读**，可在生产库跑。
        // 回答「榜面改成按最新轮读取之后，升级前已开赛/已结算/已撤注的记录会不会消失」。
        if (args.Any(a => a.Equals("milk-ledger-report", StringComparison.OrdinalIgnoreCase)))
        {
            var li = Array.FindIndex(args, a => a.Equals("milk-ledger-report", StringComparison.OrdinalIgnoreCase));
            var days = li >= 0 && li + 1 < args.Length && int.TryParse(args[li + 1], out var dv) ? dv : 30;
            return await MilkLedgerAuditStudy.RunAsync(connStr, days);
        }

        // 隔离库集成验证（2026-11-13 第五轮）。**只在隔离库跑**，会建库建表、造多轮副本，
        // 用来验「历史战绩的新统计口径」会不会重复计票、会不会改写已冻结的收益。
        // 判据不是「看起来合理」，而是报表必须与空跑台账（冻结/结算规则）逐项相等。
        if (args.Any(a => a.Equals("milk-isolated-check", StringComparison.OrdinalIgnoreCase)))
            return await MilkIsolatedCheck.RunAsync(connStr);

        // 比分（BF）只读评估（2026-11-12 加）：模型 Hit@1 / Hit@3 vs 历史频率 vs 市场。
        // 只读，且**不恢复出票**——`EnableScorePicks` 仍然关着，这里只回答「模型挑比分准不准」。
        // 起因：校验方案要求「比分 Hit@1 在 8%~15% 算可用」，而这条数在项目里从来不存在。
        if (args.Any(a => a.Equals("score-probe", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("score-probe", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
            var share = double.TryParse(rest.ElementAtOrDefault(0), out var sv) ? sv : 0.67;
            return await ScoreProbeStudy.RunAsync(connStr, share);
        }

        // 半全场边际 vs 全场模型的一致性诊断（2026-11-12 加）。
        // 只读。第三方校验方案里唯一一条「我们从来不敢说通过」的自洽项：
        // 联合分布对半场求和之后，主/平/客三向是否与比分网格一致。中位 L1 > 5 点即说明
        // 0.45/0.55 这个固定切分站不住，半全场 9 档概率整体被带偏。
        if (args.Any(a => a.Equals("hf-margin", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("hf-margin", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
            var share = double.TryParse(rest.ElementAtOrDefault(0), out var sv) ? sv : 0.67;
            return await HalfFullMarginStudy.RunAsync(connStr, share);
        }

        // 单联赛「能不能套现有模型」专项诊断（2026-11-09 加，国际比赛日窗口前）。
        // 只读。回答：这个联赛的队模型认不认识、够不够 MinTeamGames、按出票口径会出几注、出了准不准。
        if (args.Any(a => a.Equals("league-probe", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("league-probe", StringComparison.OrdinalIgnoreCase))
                           .Skip(1)
                           .ToArray();
            var league = rest.FirstOrDefault(a =>
                !a.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) &&
                !DateTime.TryParse(a, out _)) ?? "欧国联";
            var startArg = rest.FirstOrDefault(a => DateTime.TryParse(a, out _));
            var testStart = DateTime.TryParse(startArg, out var ts2) ? ts2 : new DateTime(2026, 10, 1);
            return await LeagueProbeStudy.RunAsync(connStr, league, testStart);
        }

        // 方向检验（2026-11-09 加，用户「连方向都猜错，方向对了才谈得上中奖率」）。
        // 只读。把「方向」拆成 胜平负 / 让球 / 总进球 / 半全场的半场格与全场格 四个可独立计分的量，
        // 每个量都比「模型 vs 市场 vs 无脑基线」，用来判断我们到底是方向错了还是在赌低概率档。
        if (args.Any(a => a.Equals("direction-probe", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("direction-probe", StringComparison.OrdinalIgnoreCase))
                           .Skip(1)
                           .ToArray();
            var startArg = rest.FirstOrDefault(a => DateTime.TryParse(a, out _));
            var testStart = DateTime.TryParse(startArg, out var dts) ? dts : new DateTime(2026, 10, 1);
            var lg = rest.FirstOrDefault(a =>
                !a.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) &&
                !DateTime.TryParse(a, out _));
            var league = lg is null || lg.Equals("ALL", StringComparison.OrdinalIgnoreCase) || lg == "*" ? null : lg;
            return await DirectionStudy.RunAsync(connStr, testStart, league);
        }

        // 稳胆口径回测（2026-11-09 加，用户拍板开「稳胆」栏目后的前置验证）。
        // 只读。回答「只买市场最看好那一档」按赔率分箱的命中率/回报，用来定赔率上限，而不是拍脑袋定 3 倍。
        if (args.Any(a => a.Equals("steady-probe", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("steady-probe", StringComparison.OrdinalIgnoreCase))
                           .Skip(1)
                           .ToArray();
            var startArg = rest.FirstOrDefault(a => DateTime.TryParse(a, out _));
            var testStart = DateTime.TryParse(startArg, out var sts) ? sts : new DateTime(2026, 10, 1);
            var capArg = rest.FirstOrDefault(a => decimal.TryParse(a, out _));
            var cap = decimal.TryParse(capArg, out var capv) ? capv : 3.0m;
            return await SteadyStudy.RunAsync(connStr, testStart, cap);
        }

        // 竞彩赔率漂移研究（2026-11-10 加，死窗口里唯一不需要等新数据的方向）。
        // 只读。用 FbJcOddsHistory 已有轨迹回答：临场价格「往哪边动」本身是不是信息。
        if (args.Any(a => a.Equals("drift-probe", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("drift-probe", StringComparison.OrdinalIgnoreCase))
                           .Skip(1)
                           .ToArray();
            var startArg = rest.FirstOrDefault(a => DateTime.TryParse(a, out _));
            var testStart = DateTime.TryParse(startArg, out var dts2) ? dts2 : new DateTime(2026, 10, 1);
            // 可选：drift-probe <起日> <最小跨度小时> <最小快照数>
            var nums = rest.Where(a => double.TryParse(a, out _)).Select(double.Parse).ToList();
            var minSpan = nums.Count > 0 ? nums[0] : 6.0;
            var minSnap = nums.Count > 1 ? (int)nums[1] : 10;
            return await DriftStudy.RunAsync(connStr, testStart, minSpan, minSnap);
        }

        // 稳胆线上引擎复核：把同一批历史数据喂给 SteadyPickEngine（线上那份代码），
        // 检验它和 steady-probe 的统计口径是否一致。只读。
        if (args.Any(a => a.Equals("steady-check", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(a => !a.Equals("steady-check", StringComparison.OrdinalIgnoreCase))
                           .Skip(1)
                           .ToArray();
            var startArg = rest.FirstOrDefault(a => DateTime.TryParse(a, out _));
            var testStart = DateTime.TryParse(startArg, out var cts) ? cts : new DateTime(2026, 9, 1);
            return await SteadyStudy.CheckAsync(connStr, testStart);
        }

        await using var conn = await factory.OpenAsync();

        var allSamples = (await conn.QueryAsync<Sample>(
            @"SELECT LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam,
                     MatchTime, HomeScore, AwayScore
              FROM dbo.FbMatch
              WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL")).ToList();

        var rows = (await conn.QueryAsync<Row>(
            @"SELECT m.MatchId, m.LeagueName,
                     m.HomeTeamName AS HomeTeam, m.AwayTeamName AS AwayTeam, m.MatchTime,
                     m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore,
                     e.HomeWin, e.Draw, e.AwayWin,
                     o.TotalLine, o.OverWater, o.UnderWater
              FROM dbo.FbMatch m
              OUTER APPLY (SELECT TOP 1 HomeWin, Draw, AwayWin FROM dbo.FbOdds
                           WHERE MatchId = m.MatchId AND OddsType = 'EU' AND CompanyId = 0
                             AND IsOpening = 1 AND HomeWin > 1 AND Draw > 1 AND AwayWin > 1
                           ORDER BY OddsId DESC) e
              OUTER APPLY (SELECT TOP 1 TotalLine, OverWater, UnderWater FROM dbo.FbOdds
                           WHERE MatchId = m.MatchId AND OddsType = 'OU' AND CompanyId = 0
                             AND IsOpening = 1
                           ORDER BY OddsId DESC) o
              WHERE m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND e.HomeWin IS NOT NULL
              ORDER BY m.MatchTime")).ToList();

        if (allSamples.Count == 0 || rows.Count == 0)
        {
            Console.WriteLine("没有可用样本。");
            return 1;
        }

        // 切分点：按「有欧赔场次」的开赛时间取分位，训练只用严格早于该时刻的样本
        var sortedTimes = rows.Select(r => r.MatchTime).OrderBy(t => t).ToList();
        var cutIndex = Math.Max(1, Math.Min(sortedTimes.Count - 1, (int)(sortedTimes.Count * trainRatio)));
        var cutoff = sortedTimes[cutIndex];

        var trainSamples = allSamples.Where(s => s.MatchTime < cutoff).ToList();
        var testRows = rows.Where(r => r.MatchTime >= cutoff).ToList();

        Console.WriteLine($"# 回测报告  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"| 项目 | 值 |");
        Console.WriteLine($"|---|---|");
        Console.WriteLine($"| 全库完赛场次 | {allSamples.Count} |");
        Console.WriteLine($"| 有欧赔场次 | {rows.Count} |");
        Console.WriteLine($"| 切分时刻 | {cutoff:yyyy-MM-dd HH:mm} |");
        Console.WriteLine($"| 训练样本（< 切分时刻） | {trainSamples.Count} 场 |");
        Console.WriteLine($"| 测试场次（≥ 切分时刻） | {testRows.Count} 场 |");
        Console.WriteLine();

        var fitSamples = trainSamples.Select(s => new ScoreSample
        {
            LeagueName = s.LeagueName,
            HomeTeam = s.HomeTeam,
            AwayTeam = s.AwayTeam,
            HomeScore = s.HomeScore,
            AwayScore = s.AwayScore
        }).ToList();

        var model = ScorelineModel.Fit(fitSamples);
        // A/B 对照：跨联赛兜底（回测判定不采用，见 ScorelineModel.Fit 注释）
        var modelCross = ScorelineModel.Fit(fitSamples, crossLeague: true);
        // 方案 1：联赛强度校准（跨联赛兜底 + 用跨联赛交手估的联赛强度系数）。
        // 2026-11-05 加。目标是让「荷甲队」和「意甲队」跨联赛可比——只按联赛场均归一
        // 会把「联赛进攻性强」当成「球队强」，强度系数就是补上这一维。
        var strength = ScorelineModel.EstimateLeagueStrength(fitSamples);
        var modelStrength = ScorelineModel.Fit(fitSamples, crossLeague: true, leagueStrength: strength);

        if (!model.Ready)
        {
            Console.WriteLine("**模型未就绪**：训练样本不足 30 场或全部无效。");
            return 1;
        }

        var teamGames = trainSamples
            .SelectMany(s => new[] { s.HomeTeam, s.AwayTeam })
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine($"训练集球队数 {teamGames.Count}，每队平均 {trainSamples.Count * 2.0 / teamGames.Count:F1} 场");
        Console.WriteLine();

        // ---------- 零、训练集覆盖度 ----------
        // 2026-11-05 加：回填历史赛季之前，每队平均只有 3.9 场，模型等于没有记忆。
        // 这一节就是量「回填到底补进去了多少」，别只看总场次涨了，要看到每支队身上的密度。
        Section("零、训练集覆盖度（每队样本密度，决定模型有没有资格谈预测）");
        var gamesSorted = teamGames.Values.OrderBy(x => x).ToList();
        double Pct(double q) => gamesSorted.Count == 0 ? 0 : gamesSorted[(int)Math.Min(gamesSorted.Count - 1, gamesSorted.Count * q)];
        Console.WriteLine("| 指标 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 训练场次 | {trainSamples.Count} |");
        Console.WriteLine($"| 球队数 | {teamGames.Count} |");
        Console.WriteLine($"| 每队场均 | {trainSamples.Count * 2.0 / teamGames.Count:F1} 场 |");
        Console.WriteLine($"| 每队场次中位数 | {Pct(0.50)} 场 |");
        Console.WriteLine($"| 75 分位 | {Pct(0.75)} 场 |");
        Console.WriteLine($"| 90 分位 | {Pct(0.90)} 场 |");
        Console.WriteLine($"| 99 分位 | {Pct(0.99)} 场 |");
        Console.WriteLine();
        Console.WriteLine("| 每队样本门槛 | 球队数 | 占比 |");
        Console.WriteLine("|---|---|---|");
        foreach (var gate in new[] { 5, 10, 15, 20, 30 })
        {
            var n = teamGames.Values.Count(v => v >= gate);
            Console.WriteLine($"| ≥ {gate} 场 | {n} | {100.0 * n / teamGames.Count:F1}% |");
        }
        Console.WriteLine();
        // 测试集里的覆盖度：用训练集统计的每队场次，去看测试场次的两队样本是否够
        int MinGamesOf(Row r) => Math.Min(teamGames.GetValueOrDefault(r.HomeTeam, 0), teamGames.GetValueOrDefault(r.AwayTeam, 0));
        var testKnown = testRows.Count(r => MinGamesOf(r) >= 5);
        Console.WriteLine($"测试集 {testRows.Count} 场里，两队训练样本都 ≥ 5 场的有 {testKnown} 场（{100.0 * testKnown / Math.Max(1, testRows.Count):F1}%），" +
                          $"≥ 10 场的有 {testRows.Count(r => MinGamesOf(r) >= 10)} 场。");
        Console.WriteLine();

        // ---------- 逐场比对 ----------
        List<Rec> Eval(ScorelineModel m)
        {
            var list = new List<Rec>(testRows.Count);
            foreach (var r in testRows)
            {
                var cells = m.Distribution(r.LeagueName, r.HomeTeam, r.AwayTeam, null);
                if (cells.Count == 0) continue;

                double pH = 0, pD = 0, pA = 0;
                foreach (var (h, a, p) in cells)
                {
                    if (h > a) pH += p;
                    else if (h == a) pD += p;
                    else pA += p;
                }
                var sum = pH + pD + pA;
                if (sum <= 0) continue;
                pH /= sum; pD /= sum; pA /= sum;

                var invH = 1.0 / (double)r.HomeWin;
                var invD = 1.0 / (double)r.Draw;
                var invA = 1.0 / (double)r.AwayWin;
                var invSum = invH + invD + invA;
                var mH = invH / invSum; var mD = invD / invSum; var mA = invA / invSum;

                var actualH = r.HomeScore > r.AwayScore ? 1.0 : 0.0;
                var actualD = r.HomeScore == r.AwayScore ? 1.0 : 0.0;
                var actualA = r.HomeScore < r.AwayScore ? 1.0 : 0.0;

                var eg = m.ExpectedGoals(r.LeagueName, r.HomeTeam, r.AwayTeam, null);
                var modelTotal = eg is null ? double.NaN : eg.Value.Home + eg.Value.Away;
                var actualTotal = r.HomeScore + r.AwayScore;

                double? marketTotal = null;
                if (r.TotalLine is > 0 && r.OverWater is > 1 && r.UnderWater is > 1)
                    marketTotal = ImpliedTotal(
                        (1.0 / (double)r.OverWater.Value) / (1.0 / (double)r.OverWater.Value + 1.0 / (double)r.UnderWater.Value),
                        (double)r.TotalLine.Value);

                // 门槛口径：本联赛样本（线上 TeamGames 就是它），与出注闸一致
                var gateGames = Math.Min(
                    model.TeamGames(r.LeagueName, r.HomeTeam),
                    model.TeamGames(r.LeagueName, r.AwayTeam));

                list.Add(new Rec(r.LeagueName ?? "", r.HelpText(), pH, pD, pA, mH, mD, mA,
                    actualH, actualD, actualA, modelTotal, marketTotal, actualTotal, gateGames,
                    m.KnowsTeam(r.LeagueName, r.HomeTeam) && m.KnowsTeam(r.LeagueName, r.AwayTeam)));
            }
            return list;
        }

        var recsCross = Eval(modelCross);
        var recs = Eval(model);
        var recsStrength = Eval(modelStrength);

        // ---------- A/B ----------
        // 2026-11-05：跨联赛兜底（本联赛样本 <5 场时改用该队在别的赛事的攻防）。
        // 同一份训练样本、同一批测试场次，两个模型各跑一遍，差别只在这一点。
        Section("A/B：跨联赛兜底要不要开（线上=关）");
        Console.WriteLine("| 口径 | Brier | LogLoss | 主胜概率标准差 | 平均总进球偏差 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var (label, rs) in new (string, List<Rec>)[]
                 { ("线上：只在本联赛取，不够用先验", recs), ("对照：跨联赛兜底（不采用）", recsCross), ("方案1：兜底+联赛强度校准", recsStrength) })
        {
            if (rs.Count == 0) { Console.WriteLine($"| {label} | 无可评估场次 | — | — | — |"); continue; }
            var st = Stat(rs);
            Console.WriteLine($"| {label} | {st.Brier:F4} | {st.Log:F4} | {st.Std * 100:F1}pp | {st.TotalDev:+0.00;-0.00} |");
        }

        // 方案 1 的联赛强度系数本身：把估出来的值打出来，让人看「强联赛」排位是否合理。
        Console.WriteLine();
        Console.WriteLine("联赛强度系数（方案1 估计，1.0=全局平均，>1=更强）");
        Console.WriteLine("| 联赛 | 强度 |");
        Console.WriteLine("|---|---|");
        foreach (var kv in strength.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"| {kv.Key} | {kv.Value:F3} |");

        // 关键分层：只在「本联赛样本不够」的场次上比。跨联赛兜底只在这些场次起作用，
        // 全网一起算会被 86% 本来就正常的场次稀释掉，看不出好坏。
        Console.WriteLine();
        Console.WriteLine("| 分层（本联赛最少样本） | 场次 | 线上 Brier | 兜底 Brier | 强度校准 Brier | 强度-兜底差 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        for (var idx = 0; idx < 3; idx++)
        {
            var (label, lo, hi) = idx switch
            {
                0 => ("0-2 场（欧战球队的典型值）", 0, 2),
                1 => ("3-5 场", 3, 5),
                _ => ("≥6 场（两版应完全一致）", 6, int.MaxValue)
            };
            var a = recs.Where(x => x.MinTeamGames >= lo && x.MinTeamGames <= hi).ToList();
            var b = recsCross.Where(x => x.MinTeamGames >= lo && x.MinTeamGames <= hi).ToList();
            var c = recsStrength.Where(x => x.MinTeamGames >= lo && x.MinTeamGames <= hi).ToList();
            if (a.Count == 0 || b.Count == 0) { Console.WriteLine($"| {label} | 0 | — | — | — | — |"); continue; }
            Console.WriteLine($"| {label} | {a.Count} | {Stat(a).Brier:F4} | {Stat(b).Brier:F4} | {Stat(c).Brier:F4} | {Stat(c).Brier - Stat(b).Brier:+0.0000;-0.0000} |");
        }
        Console.WriteLine();

        // 模型对不认识的队也照给概率（退先验），所以要单独量「它其实不认识」的那部分
        var unknownBoth = testRows.Count(r =>
            !model.KnowsTeam(r.LeagueName, r.HomeTeam) && !model.KnowsTeam(r.LeagueName, r.AwayTeam));
        var unknownOne = testRows.Count(r =>
            model.KnowsTeam(r.LeagueName, r.HomeTeam) ^ model.KnowsTeam(r.LeagueName, r.AwayTeam));

        Console.WriteLine($"可评估场次 {recs.Count}");
        Console.WriteLine($"两队都不认识 {unknownBoth} 场（{100.0 * unknownBoth / testRows.Count:F1}%），只认识一队 {unknownOne} 场");
        Console.WriteLine();

        if (recs.Count == 0)
        {
            Console.WriteLine("**测试集里没有任何一场模型认识两队**——这本身就是结论：覆盖率不够。");
            return 0;
        }

        // ---------- 一、总体 ----------
        Section("一、总体：模型 vs 市场（Brier / LogLoss 越低越好）");

        var modelBrier = recs.Average(x => Sqr(x.PH, x.AH) + Sqr(x.PD, x.AD) + Sqr(x.PA, x.AA));
        var marketBrier = recs.Average(x => Sqr(x.MH, x.AH) + Sqr(x.MD, x.AD) + Sqr(x.MA, x.AA));
        var modelLog = recs.Average(x => -Math.Log(Math.Max(1e-9, x.AH > 0.5 ? x.PH : x.AD > 0.5 ? x.PD : x.PA)));
        var marketLog = recs.Average(x => -Math.Log(Math.Max(1e-9, x.AH > 0.5 ? x.MH : x.AD > 0.5 ? x.MD : x.MA)));

        Console.WriteLine("| 指标 | 模型 | 市场 | 差距（正=模型更差） |");
        Console.WriteLine("|---|---|---|---|");
        Console.WriteLine($"| Brier | {modelBrier:F4} | {marketBrier:F4} | {modelBrier - marketBrier:+0.0000;-0.0000} |");
        Console.WriteLine($"| LogLoss | {modelLog:F4} | {marketLog:F4} | {modelLog - marketLog:+0.0000;-0.0000} |");
        Console.WriteLine($"| 平均总进球 | {recs.Where(x => !double.IsNaN(x.ModelTotal)).Average(x => x.ModelTotal):F2} | 实际 {recs.Average(x => x.ActualTotal):F2} | 偏差 {recs.Where(x => !double.IsNaN(x.ModelTotal)).Average(x => x.ModelTotal) - recs.Average(x => x.ActualTotal):+0.00;-0.00} |");

        // 无脑基线的对照：永远猜 45/27/28
        var baseBrier = recs.Average(x => Sqr(0.45, x.AH) + Sqr(0.27, x.AD) + Sqr(0.28, x.AA));
        Console.WriteLine($"| 参考：固定 45/27/28 瞎猜 | {baseBrier:F4} | — | — |");
        Console.WriteLine();

        // ---------- 二、校准曲线 ----------
        Section("二、校准曲线（主胜口径）：模型说 X%，实际发生多少");
        Console.WriteLine("| 模型概率 | 场次 | 模型均值 | 实际频率 | 偏差 |");
        Console.WriteLine("|---|---|---|---|---|");
        for (var lo = 0; lo < 100; lo += 10)
        {
            var bucket = recs.Where(x => x.PH * 100 >= lo && x.PH * 100 < lo + 10).ToList();
            if (bucket.Count < 20) continue;
            var pred = bucket.Average(x => x.PH);
            var act = bucket.Average(x => x.AH);
            Console.WriteLine($"| {lo}-{lo + 10}% | {bucket.Count} | {pred * 100:F1}% | {act * 100:F1}% | {(act - pred) * 100:+0.0;-0.0}pp |");
        }
        Console.WriteLine();

        // 同一口径下市场自己的校准（作为标尺）
        Section("三、市场校准曲线（主胜口径），用来判断「偏差多大算大」");
        Console.WriteLine("| 市场概率 | 场次 | 市场均值 | 实际频率 | 偏差 |");
        Console.WriteLine("|---|---|---|---|---|");
        for (var lo = 0; lo < 100; lo += 10)
        {
            var bucket = recs.Where(x => x.MH * 100 >= lo && x.MH * 100 < lo + 10).ToList();
            if (bucket.Count < 20) continue;
            var pred = bucket.Average(x => x.MH);
            var act = bucket.Average(x => x.AH);
            Console.WriteLine($"| {lo}-{lo + 10}% | {bucket.Count} | {pred * 100:F1}% | {act * 100:F1}% | {(act - pred) * 100:+0.0;-0.0}pp |");
        }
        Console.WriteLine();

        // ---------- 四、分联赛 ----------
        Section("四、分联赛偏差（样本 ≥ 15 场，按总进球偏差排序）");
        Console.WriteLine("| 联赛 | 场次 | 模型总进球 | 实际总进球 | 偏差 | 模型主胜% | 实际主胜% |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var g in recs.GroupBy(x => x.League).Where(g => g.Count() >= 15)
                     .OrderByDescending(g => Math.Abs(g.Average(x => x.ModelTotal) - g.Average(x => x.ActualTotal)))
                     .Take(20))
        {
            var mt = g.Average(x => x.ModelTotal);
            var at = g.Average(x => x.ActualTotal);
            Console.WriteLine($"| {(string.IsNullOrWhiteSpace(g.Key) ? "(无联赛名)" : g.Key)} | {g.Count()} | {mt:F2} | {at:F2} | {mt - at:+0.00;-0.00} | {g.Average(x => x.PH) * 100:F1}% | {g.Average(x => x.AH) * 100:F1}% |");
        }
        Console.WriteLine();

        // ---------- 五、样本量分层 ----------
        Section("五、按「两队中样本较少一方」分层 —— 定熔断阈值");
        Console.WriteLine("| 最少样本 | 场次 | 模型 Brier | 市场 Brier | 模型更差 | 模型总进球 | 实际总进球 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (label, lo, hi) in new (string, int, int)[]
                 { ("0-2 场", 0, 2), ("3-5 场", 3, 5), ("6-10 场", 6, 10), ("11+ 场", 11, int.MaxValue) })
        {
            var sub = recs.Where(x => x.MinTeamGames >= lo && x.MinTeamGames <= hi).ToList();
            if (sub.Count == 0) continue;
            var mb = sub.Average(x => Sqr(x.PH, x.AH) + Sqr(x.PD, x.AD) + Sqr(x.PA, x.AA));
            var kb = sub.Average(x => Sqr(x.MH, x.AH) + Sqr(x.MD, x.AD) + Sqr(x.MA, x.AA));
            Console.WriteLine($"| {label} | {sub.Count} | {mb:F4} | {kb:F4} | {mb - kb:+0.0000;-0.0000} | {sub.Average(x => x.ModelTotal):F2} | {sub.Average(x => x.ActualTotal):F2} |");
        }
        Console.WriteLine();

        // ---------- 六、模型到底有没有区分度 ----------
        Section("六、模型有区分度吗（如果每场都给出相似概率，Brier 再接近瞎猜也不意外）");
        var phStd = Math.Sqrt(recs.Average(x => Math.Pow(x.PH - recs.Average(y => y.PH), 2)));
        var mhStd = Math.Sqrt(recs.Average(x => Math.Pow(x.MH - recs.Average(y => y.MH), 2)));
        Console.WriteLine("| 口径 | 主胜概率均值 | 标准差 |");
        Console.WriteLine("|---|---|---|");
        Console.WriteLine($"| 模型 | {recs.Average(x => x.PH) * 100:F1}% | {phStd * 100:F1}pp |");
        Console.WriteLine($"| 市场 | {recs.Average(x => x.MH) * 100:F1}% | {mhStd * 100:F1}pp |");
        Console.WriteLine();
        Console.WriteLine($"模型标准差只有市场的 {phStd / Math.Max(1e-9, mhStd):P0}——越低说明它越不区分比赛。");
        Console.WriteLine();

        Section("七、认识 vs 不认识 两队的场次，表现差多少");
        Console.WriteLine("| 分组 | 场次 | 模型 Brier | 市场 Brier | 模型更差 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var (label, sub) in new (string, List<Rec>)[]
                 {
                     ("两队都认识", recs.Where(x => x.KnownBoth).ToList()),
                     ("至少一队不认识", recs.Where(x => !x.KnownBoth).ToList())
                 })
        {
            if (sub.Count == 0) continue;
            var mb = sub.Average(x => Sqr(x.PH, x.AH) + Sqr(x.PD, x.AD) + Sqr(x.PA, x.AA));
            var kb = sub.Average(x => Sqr(x.MH, x.AH) + Sqr(x.MD, x.AD) + Sqr(x.MA, x.AA));
            Console.WriteLine($"| {label} | {sub.Count} | {mb:F4} | {kb:F4} | {mb - kb:+0.0000;-0.0000} |");
        }
        Console.WriteLine();
        Console.WriteLine("线上对这些「不认识」的场次也会照给概率，这正是「模型说阿尔克马能赢尤文」的来源。");

        // ============================================================================
        // 大奶盘命中率回测（2026-11-05 加）。
        // 之前回测台只量了「胜平负方向」，从来没量过对外发布的大奶（比分/半全场/总进球）。
        // 方向模型打不过市场，于是方向停发、只留大奶——但大奶到底准不准，一直没人量过。
        // 这一节就是补上这块盲区：用 FbJcOddsHistory 里竞彩官方赔率（收盘快照）当市场标尺，
        // 拿 Dixon-Coles 比分网格当模型，逐场对「模型概率 vs 市场概率 vs 实际赛果」。
        // 关键约束同正文：只评测试集（≥ 切分时刻），不碰训练集，避免泄漏。
        // ============================================================================
        var testMatchIds = testRows.Select(r => r.MatchId).Distinct().ToList();
        var jcClose = await LoadJcClosingOdds(conn, testMatchIds);
        var jcTest = testRows.Where(r => jcClose.ContainsKey(r.MatchId)).ToList();

        Console.WriteLine();
        Section("八、大奶盘：比分(BF)命中率与校准（模型 vs 市场）");
        BacktestScore(model, jcTest, jcClose);

        Console.WriteLine();
        Section("九、大奶盘：总进球(JQ)命中率与校准（模型 vs 市场）");
        BacktestGoals(model, jcTest, jcClose);

        Console.WriteLine();
        Section("十、大奶盘：半全场(BQ)命中率（模型 vs 市场）");
        BacktestHalfFull(model, jcTest, jcClose);

        // ============================================================================
        // 十一、北单历史欧赔（平手盘）作为第二市场标尺（2026-11-05 加）。
        // 正文的市场标尺用的是 FbOdds（实时采集，只有 7183 场，2026-10-28 之后）。
        // 库里 FbBdHistory（北单历史开奖）里躺着 66015 场「平手盘」（Handicap=0）：
        // 平手盘的 AvgHome/AvgDraw/AvgAway 就是原始胜负欧赔，无需让球换算，可直接当市场标尺。
        // 这一节把「模型 vs 市场」的校准样本从 7 千场放大到 6.6 万场，结论立刻从「谨慎参考」变「可信」。
        // 同样按时间前向切分，训练集严格早于切分时刻，测试集是北单平手盘里 ≥ 切分时刻的部分。
        // ============================================================================
        var bdRows = (await conn.QueryAsync<BdRow>(
            @"SELECT LeagueName, HomeTeam, AwayTeam, EndTime AS MatchTime,
                     AvgHome AS HomeWin, AvgDraw AS Draw, AvgAway AS AwayWin,
                     HomeScore, AwayScore
              FROM dbo.FbBdHistory
              WHERE Handicap = 0 AND AvgHome > 1 AND AvgDraw > 1 AND AvgAway > 1
                AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL")).ToList();

        Console.WriteLine();
        Section("十一、北单历史欧赔（平手盘）作为第二市场标尺：模型 vs 市场");
        // 用北单自己的时间轴做严格前向切分：训练集 = FbMatch 里 < 2025-01-01 的场次（约 20 万场），
        // 测试集 = 北单平手盘里 ≥ 2025-01-01 的场次（约 2 万场）。两段时间轴对齐，训练不碰测试，无泄漏。
        // 这里重新 Fit 一个「截至 2024 年底」的模型，与正文模型（截至 2026-10）不同——因为正文模型的
        // 训练集已包含 2025-2026 的 FbMatch 赛果，直接拿来评北单 2025+ 会泄漏。
        var bdCutoff = new DateTime(2025, 1, 1);
        var bdFitSamples = allSamples.Where(s => s.MatchTime < bdCutoff)
            .Select(s => new ScoreSample
            {
                LeagueName = s.LeagueName,
                HomeTeam = s.HomeTeam,
                AwayTeam = s.AwayTeam,
                HomeScore = s.HomeScore,
                AwayScore = s.AwayScore
            }).ToList();
        var bdModel = ScorelineModel.Fit(bdFitSamples);
        if (bdModel.Ready)
            BacktestBdMarket(bdModel, bdRows, bdCutoff, bdFitSamples.Count);
        else
            Console.WriteLine("北单标尺模型未就绪（截至 2024 底的训练样本不足）。");

        return 0;
    }

    // ============================================================================
    // 大奶盘回测辅助（2026-11-05 加）
    // ============================================================================

    /// <summary>
    /// 读测试集场次的竞彩收盘赔率：每个 MatchId 每个玩法每个档，取开赛前最后一次采样（CapturedAt 最晚）。
    /// 返回结构：matchId -> playType -> optionCode -> (Odds)。
    /// 口径上「收盘价」才是在售时用户能拿到的价格，也是市场对结果定价的最终表达。
    /// </summary>
    private static async Task<Dictionary<long, Dictionary<string, Dictionary<string, decimal>>>> LoadJcClosingOdds(
        System.Data.Common.DbConnection conn, List<long> matchIds)
    {
        var result = new Dictionary<long, Dictionary<string, Dictionary<string, decimal>>>();
        if (matchIds.Count == 0) return result;

        // 每档取 CapturedAt 最晚的那条。Chunk 防 IN 参数超限。
        foreach (var chunk in ChunkIds(matchIds))
        {
            var rows = await conn.QueryAsync<(long MatchId, string PlayType, string OptionCode, decimal Odds)>(
                @"SELECT MatchId, PlayType, OptionCode, Odds FROM (
                    SELECT MatchId, PlayType, OptionCode, Odds,
                           ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt DESC) AS rn
                    FROM dbo.FbJcOddsHistory WHERE MatchId IN @ids
                 ) t WHERE rn = 1 AND Odds > 1",
                new { ids = chunk });
            foreach (var r in rows)
            {
                if (!result.TryGetValue(r.MatchId, out var byPlay))
                    result[r.MatchId] = byPlay = new Dictionary<string, Dictionary<string, decimal>>();
                if (!byPlay.TryGetValue(r.PlayType, out var byOpt))
                    byPlay[r.PlayType] = byOpt = new Dictionary<string, decimal>();
                byOpt[r.OptionCode] = r.Odds;
            }
        }
        return result;
    }

    private static IEnumerable<long[]> ChunkIds(List<long> ids, int size = 500)
    {
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.Skip(i).Take(size).ToArray();
    }

    /// <summary>比分盘回测。模型 49 格分布 vs 市场（官方 BF 赔率去水）vs 实际比分。</summary>
    private static void BacktestScore(
        ScorelineModel model, List<Row> test,
        Dictionary<long, Dictionary<string, Dictionary<string, decimal>>> close)
    {
        // 汇总：模型 Top-1 命中 / 市场最低赔命中 / Brier（模型 vs 市场）
        int n = 0, modelTopHit = 0, marketTopHit = 0;
        double modelBrier = 0, marketBrier = 0;
        var calib = new List<(double Pred, int Hit)>();   // 模型概率 → 是否命中（该校准用）
        var calibM = new List<(double Pred, int Hit)>();  // 市场概率 → 是否命中

        foreach (var r in test)
        {
            if (!close.TryGetValue(r.MatchId, out var byPlay) || !byPlay.TryGetValue("BF", out var bf)) continue;
            if (bf.Count < 3) continue;   // 至少要几个具体比分档

            var cells = model.Distribution(r.LeagueName, r.HomeTeam, r.AwayTeam, r.TotalLine);
            if (cells.Count == 0) continue;

            // 模型概率：49 格归一化（Distribution 已归一，但保险再归一）
            var sum = cells.Sum(c => c.P);
            var modelProbs = cells.ToDictionary(c => $"{c.Home}:{c.Away}", c => c.P / Math.Max(1e-9, sum));

            // 市场概率：官方赔率去水。只算具体比分档（"胜其它/平其它/负其它"无法对应单格，跳过）
            var marketProbs = new Dictionary<string, double>();
            double invSum = 0;
            var marketByCode = new Dictionary<string, double>();
            foreach (var (code, odds) in bf)
            {
                if (!code.Contains(':')) continue;   // 跳过"其它"档
                var p = 1.0 / (double)odds;
                marketByCode[code] = p;
                invSum += p;
            }
            if (invSum <= 0) continue;
            foreach (var (code, p) in marketByCode) marketProbs[code] = p / invSum;

            var actual = $"{r.HomeScore}:{r.AwayScore}";
            n++;

            // 模型 Top-1（概率最高的单格）
            var modelTop = cells.OrderByDescending(c => c.P).First();
            if ($"{modelTop.Home}:{modelTop.Away}" == actual) modelTopHit++;

            // 市场 Top-1（赔率最低的具体比分）
            var marketTop = marketByCode.OrderBy(x => x.Value).Last();
            if (marketTop.Key == actual) marketTopHit++;

            // Brier：多分类，Σ(1[k=actual] - p_k)^2 = 1 - 2p_actual + Σp_k^2
            var pModelActual = modelProbs.TryGetValue(actual, out var pm) ? pm : 0.0;
            var pMarketActual = marketProbs.TryGetValue(actual, out var pk) ? pk : 0.0;
            double brierModel = 1 - 2 * pModelActual + modelProbs.Values.Sum(x => x * x);
            double brierMarket = 1 - 2 * pMarketActual + marketProbs.Values.Sum(x => x * x);
            modelBrier += brierModel;
            marketBrier += brierMarket;

            // 校准：遍历每个预测项（49 格），记录「模型给该格的概率」和「该格是否命中」。
            // 这样分桶后，p=8% 的桶里实际命中率才应该接近 8%，能暴露过度自信。
            foreach (var (h, a, p) in cells)
                calib.Add((p / Math.Max(1e-9, sum), $"{h}:{a}" == actual ? 1 : 0));
            // 市场侧同理：遍历有赔率的具体比分档
            foreach (var (code, p) in marketProbs)
                calibM.Add((p, code == actual ? 1 : 0));
        }

        if (n == 0) { Console.WriteLine("无可评估的比分场次（测试集里没有 BF 收盘赔率）。"); return; }

        Console.WriteLine("| 口径 | 场次 | Top-1 命中率 | 平均 Brier |");
        Console.WriteLine("|---|---|---|---|");
        Console.WriteLine($"| 模型 | {n} | {100.0 * modelTopHit / n:F1}% | {modelBrier / n:F4} |");
        Console.WriteLine($"| 市场 | {n} | {100.0 * marketTopHit / n:F1}% | {marketBrier / n:F4} |");
        Console.WriteLine();
        Console.WriteLine("校准（模型说这个比分 X%，实际命中频率）：");
        PrintCalibration(calib, "模型");
        Console.WriteLine();
        Console.WriteLine("校准（市场说这个比分 X%，实际命中频率）：");
        PrintCalibration(calibM, "市场");
    }

    /// <summary>总进球盘回测。模型对角线合计 vs 市场（官方 JQ 赔率去水）vs 实际总进球。</summary>
    private static void BacktestGoals(
        ScorelineModel model, List<Row> test,
        Dictionary<long, Dictionary<string, Dictionary<string, decimal>>> close)
    {
        int n = 0, modelTopHit = 0, marketTopHit = 0;
        double modelBrier = 0, marketBrier = 0;
        var calib = new List<(double, int)>();
        var calibM = new List<(double, int)>();

        foreach (var r in test)
        {
            if (!close.TryGetValue(r.MatchId, out var byPlay) || !byPlay.TryGetValue("JQ", out var jq)) continue;
            var cells = model.Distribution(r.LeagueName, r.HomeTeam, r.AwayTeam, r.TotalLine);
            if (cells.Count == 0) continue;

            // 模型总进球分布：对角线求和，0..6 + 7+
            var modelGoals = new double[8];
            foreach (var (h, a, p) in cells)
            {
                var g = h + a;
                modelGoals[Math.Min(g, 7)] += p;
            }
            var gSum = modelGoals.Sum();
            for (var i = 0; i < 8; i++) modelGoals[i] /= Math.Max(1e-9, gSum);

            // 市场总进球分布：JQ 档 0..7（7=7+），去水
            var marketGoals = new double[8];
            double invSum = 0;
            foreach (var (code, odds) in jq)
            {
                if (int.TryParse(code, out var g) && g >= 0 && g <= 7)
                {
                    marketGoals[g] = 1.0 / (double)odds;
                    invSum += marketGoals[g];
                }
            }
            if (invSum <= 0) continue;
            for (var i = 0; i < 8; i++) marketGoals[i] /= invSum;

            var actual = Math.Min(r.HomeScore + r.AwayScore, 7);
            n++;

            if (Array.IndexOf(modelGoals, modelGoals.Max()) == actual) modelTopHit++;
            if (Array.IndexOf(marketGoals, marketGoals.Max()) == actual) marketTopHit++;

            double brierModel = 1 - 2 * modelGoals[actual] + modelGoals.Sum(x => x * x);
            double brierMarket = 1 - 2 * marketGoals[actual] + marketGoals.Sum(x => x * x);
            modelBrier += brierModel;
            marketBrier += brierMarket;
            // 校准：遍历 8 个总进球档，记录每档概率与是否命中
            for (var g = 0; g < 8; g++)
            {
                calib.Add((modelGoals[g], g == actual ? 1 : 0));
                calibM.Add((marketGoals[g], g == actual ? 1 : 0));
            }
        }

        if (n == 0) { Console.WriteLine("无可评估的总进球场次。"); return; }
        Console.WriteLine("| 口径 | 场次 | Top-1 命中率 | 平均 Brier |");
        Console.WriteLine("|---|---|---|---|");
        Console.WriteLine($"| 模型 | {n} | {100.0 * modelTopHit / n:F1}% | {modelBrier / n:F4} |");
        Console.WriteLine($"| 市场 | {n} | {100.0 * marketTopHit / n:F1}% | {marketBrier / n:F4} |");
        Console.WriteLine();
        Console.WriteLine("校准（模型给该档的概率 → 该档是否实际命中）：");
        PrintCalibration(calib, "模型");
        Console.WriteLine();
        Console.WriteLine("校准（市场给该档的概率 → 该档是否实际命中）：");
        PrintCalibration(calibM, "市场");
    }

    /// <summary>半全场盘回测。上半场 λ×0.45 + 下半场 λ×0.55 独立泊松联合，对比市场 BQ 赔率。</summary>
    private static void BacktestHalfFull(
        ScorelineModel model, List<Row> test,
        Dictionary<long, Dictionary<string, Dictionary<string, decimal>>> close)
    {
        // 半全场只发顺风三档：3-3 胜胜 / 1-1 平平 / 0-0 负负。回测也聚焦这三档（+ 市场全 9 档做标尺）。
        int n = 0, modelTopHit = 0, marketTopHit = 0;
        double modelBrier = 0, marketBrier = 0;
        var calib = new List<(double, int)>();
        var calibM = new List<(double, int)>();

        foreach (var r in test)
        {
            if (!close.TryGetValue(r.MatchId, out var byPlay) || !byPlay.TryGetValue("BQ", out var bq)) continue;
            var eg = model.ExpectedGoals(r.LeagueName, r.HomeTeam, r.AwayTeam, r.TotalLine);
            if (eg is null) continue;

            // 模型半全场联合分布（与 MilkPickEngine.HalfFullJoint 同口径：两段独立泊松）
            var joint = HalfFullJoint(eg.Value.Home, eg.Value.Away);
            var actual = HalfFullCode(r.HalfHomeScore, r.HalfAwayScore, r.HomeScore, r.AwayScore);

            // 市场半全场分布（9 档去水）
            var market = new Dictionary<string, double>();
            double invSum = 0;
            foreach (var (code, odds) in bq)
            {
                var p = 1.0 / (double)odds;
                market[code] = p;
                invSum += p;
            }
            if (invSum <= 0) continue;
            foreach (var k in market.Keys.ToList()) market[k] /= invSum;

            n++;
            // 模型 Top-1 半全场（9 档里概率最高的）
            var modelTop = joint.OrderByDescending(x => x.Value).First();
            if (modelTop.Key == actual) modelTopHit++;
            var marketTop = market.OrderByDescending(x => x.Value).First();
            if (marketTop.Key == actual) marketTopHit++;

            // Brier：9 档多分类 Brier（∑ (p_k - 1[k=actual])^2）
            double bm = 0, bk = 0;
            foreach (var (code, p) in joint)
            {
                var y = code == actual ? 1.0 : 0.0;
                bm += (p - y) * (p - y);
            }
            foreach (var (code, p) in market)
            {
                var y = code == actual ? 1.0 : 0.0;
                bk += (p - y) * (p - y);
            }
            modelBrier += bm;
            marketBrier += bk;

            // 校准：遍历 9 档，记录每档概率与是否命中
            foreach (var (code, p) in joint)
                calib.Add((p, code == actual ? 1 : 0));
            foreach (var (code, p) in market)
                calibM.Add((p, code == actual ? 1 : 0));
        }

        if (n == 0) { Console.WriteLine("无可评估的半全场场次。"); return; }
        Console.WriteLine("| 口径 | 场次 | Top-1 命中率 | 平均 Brier |");
        Console.WriteLine("|---|---|---|---|");
        Console.WriteLine($"| 模型 | {n} | {100.0 * modelTopHit / n:F1}% | {modelBrier / n:F4} |");
        Console.WriteLine($"| 市场 | {n} | {100.0 * marketTopHit / n:F1}% | {marketBrier / n:F4} |");
        Console.WriteLine();
        Console.WriteLine("半全场只有 9 档，Top-1 命中率本身很低属正常，关键看模型有没有超过市场。");
        Console.WriteLine("校准（模型给该档的概率 → 该档是否命中）：");
        PrintCalibration(calib, "模型");
        Console.WriteLine();
        Console.WriteLine("校准（市场给该档的概率 → 该档是否命中）：");
        PrintCalibration(calibM, "市场");
    }

    /// <summary>
    /// 北单历史欧赔（平手盘）作为第二市场标尺：模型 vs 北单市场。
    /// 只评测试集（MatchTime ≥ cutoff），与正文同口径的无泄漏前向切分。
    /// 模型用 Distribution 算胜平负三边概率；市场用 AvgHome/AvgDraw/AvgAway 去水。
    ///
    /// 方案 A（2026-11-05）：主结论口径从「全部场次」切换到「只两队都认识」——
    /// 模型不认识的队退全局先验，比的是先验不是模型，混进结论会污染标尺。
    /// 同时新增「样本充足」（两队 TeamGames 都 ≥5，与出注闸一致）的更严口径，
    /// 以及按联赛拆「不认识」场次的诊断，把 43% 的噪声具体归因到冷门联赛。
    /// </summary>
    private static void BacktestBdMarket(ScorelineModel model, List<BdRow> bdRows, DateTime cutoff, int trainCount)
    {
        var test = bdRows.Where(r => r.MatchTime >= cutoff).ToList();
        if (test.Count == 0)
        {
            Console.WriteLine("北单平手盘里没有 ≥ 切分时刻的场次（北单历史可能停在切分之前）。");
            return;
        }

        Console.WriteLine($"| 项目 | 值 |");
        Console.WriteLine($"|---|---|");
        Console.WriteLine($"| 北单平手盘总场次 | {bdRows.Count} |");
        Console.WriteLine($"| 切分时刻 | {cutoff:yyyy-MM-dd} |");
        Console.WriteLine($"| 训练集（FbMatch < 切分时刻） | {trainCount} 场 |");
        Console.WriteLine($"| 测试集（北单 ≥ 切分时刻） | {test.Count} |");
        Console.WriteLine();

        // 逐场评估，收集 (模型三边概率, 市场三边概率, 实际结果, 两队是否都认识, 两队样本是否充足)
        var items = new List<(double PH, double PD, double PA, double MH, double MD, double MA,
            double AH, double AD, double AA, bool KnownBoth, bool EnoughGames)>();
        int unknownBoth = 0;

        foreach (var r in test)
        {
            var cells = model.Distribution(r.LeagueName, r.HomeTeam, r.AwayTeam, null);
            if (cells.Count == 0) continue;

            double pH = 0, pD = 0, pA = 0;
            foreach (var (h, a, p) in cells)
            {
                if (h > a) pH += p; else if (h == a) pD += p; else pA += p;
            }
            var sum = pH + pD + pA;
            if (sum <= 0) continue;
            pH /= sum; pD /= sum; pA /= sum;

            var invH = 1.0 / (double)r.HomeWin;
            var invD = 1.0 / (double)r.Draw;
            var invA = 1.0 / (double)r.AwayWin;
            var invSum = invH + invD + invA;
            var mH = invH / invSum; var mD = invD / invSum; var mA = invA / invSum;

            var knownBoth = model.KnowsTeam(r.LeagueName, r.HomeTeam) &&
                            model.KnowsTeam(r.LeagueName, r.AwayTeam);
            if (!knownBoth) unknownBoth++;

            // 样本充足：两队在本联赛（线上口径）都至少有 5 场，与出注闸 MinTeamGames 一致
            var enoughGames = knownBoth &&
                              model.TeamGames(r.LeagueName, r.HomeTeam) >= 5 &&
                              model.TeamGames(r.LeagueName, r.AwayTeam) >= 5;

            var aH = r.HomeScore > r.AwayScore ? 1.0 : 0.0;
            var aD = r.HomeScore == r.AwayScore ? 1.0 : 0.0;
            var aA = r.HomeScore < r.AwayScore ? 1.0 : 0.0;

            items.Add((pH, pD, pA, mH, mD, mA, aH, aD, aA, knownBoth, enoughGames));
        }

        if (items.Count == 0)
        {
            Console.WriteLine("测试集里模型一场都评不了（Distribution 为空）。");
            return;
        }

        double BrierModel(IEnumerable<(double PH, double PD, double PA, double MH, double MD, double MA, double AH, double AD, double AA, bool KnownBoth, bool EnoughGames)> xs) =>
            xs.Average(x => Sqr(x.PH, x.AH) + Sqr(x.PD, x.AD) + Sqr(x.PA, x.AA));
        double BrierMarket(IEnumerable<(double PH, double PD, double PA, double MH, double MD, double MA, double AH, double AD, double AA, bool KnownBoth, bool EnoughGames)> xs) =>
            xs.Average(x => Sqr(x.MH, x.AH) + Sqr(x.MD, x.AD) + Sqr(x.MA, x.AA));

        var knownItems = items.Where(x => x.KnownBoth).ToList();
        var enoughItems = items.Where(x => x.EnoughGames).ToList();

        Console.WriteLine("| 口径 | 场次 | 模型 Brier | 北单市场 Brier | 模型更差 |");
        Console.WriteLine("|---|---|---|---|---|");
        var allMb = BrierModel(items);
        var allKb = BrierMarket(items);
        Console.WriteLine($"| 全部（含模型不认识的，参考） | {items.Count} | {allMb:F4} | {allKb:F4} | {allMb - allKb:+0.0000;-0.0000} |");
        if (knownItems.Count > 0)
        {
            var kMb = BrierModel(knownItems);
            var kKb = BrierMarket(knownItems);
            Console.WriteLine($"| **只两队都认识（主口径）** | {knownItems.Count} | {kMb:F4} | {kKb:F4} | {kMb - kKb:+0.0000;-0.0000} |");
        }
        if (enoughItems.Count > 0)
        {
            var eMb = BrierModel(enoughItems);
            var eKb = BrierMarket(enoughItems);
            Console.WriteLine($"| 两队都认识且样本≥5（出注口径） | {enoughItems.Count} | {eMb:F4} | {eKb:F4} | {eMb - eKb:+0.0000;-0.0000} |");
        }
        Console.WriteLine();
        Console.WriteLine($"测试集里模型「两队都认识」的有 {knownItems.Count} 场，不认识至少一队的 {unknownBoth} 场" +
                          $"（{100.0 * unknownBoth / items.Count:F1}%）——不认识的场次模型退先验，不算真本事。");
        Console.WriteLine();

        // 按联赛拆「不认识」的场次：把噪声归因到具体冷门联赛，说明方案 A 丢的是哪些
        Console.WriteLine("「不认识至少一队」的场次按联赛分布（前 15，说明噪声从哪来）：");
        Console.WriteLine("| 联赛 | 不认识场次 | 占该联赛测试比例 |");
        Console.WriteLine("|---|---|---|");
        var unknownByLeague = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalByLeague = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in test)
        {
            var lg = r.LeagueName ?? "(空)";
            totalByLeague[lg] = totalByLeague.GetValueOrDefault(lg) + 1;
            if (!model.KnowsTeam(lg, r.HomeTeam) || !model.KnowsTeam(lg, r.AwayTeam))
                unknownByLeague[lg] = unknownByLeague.GetValueOrDefault(lg) + 1;
        }
        foreach (var kv in unknownByLeague.OrderByDescending(kv => kv.Value).Take(15))
        {
            var total = totalByLeague.GetValueOrDefault(kv.Key, 1);
            Console.WriteLine($"| {kv.Key} | {kv.Value} | {100.0 * kv.Value / total:F0}% |");
        }
        Console.WriteLine();

        // 校准曲线：用「两队都认识」的场次，主胜口径（和正文第二节同口径，可直接对比）
        if (knownItems.Count < 50)
        {
            Console.WriteLine("两队都认识的场次 < 50，校准曲线跳过（样本太薄）。");
            return;
        }

        Console.WriteLine("校准（模型，主胜口径，只两队都认识）：");
        Console.WriteLine("| 模型概率 | 场次 | 模型均值 | 实际频率 | 偏差 |");
        Console.WriteLine("|---|---|---|---|---|");
        for (var lo = 0; lo < 100; lo += 10)
        {
            var bucket = knownItems.Where(x => x.PH * 100 >= lo && x.PH * 100 < lo + 10).ToList();
            if (bucket.Count < 20) continue;
            var pred = bucket.Average(x => x.PH);
            var act = bucket.Average(x => x.AH);
            Console.WriteLine($"| {lo}-{lo + 10}% | {bucket.Count} | {pred * 100:F1}% | {act * 100:F1}% | {(act - pred) * 100:+0.0;-0.0}pp |");
        }
        Console.WriteLine();

        Console.WriteLine("校准（北单市场，主胜口径，同一批场次）：");
        Console.WriteLine("| 市场概率 | 场次 | 市场均值 | 实际频率 | 偏差 |");
        Console.WriteLine("|---|---|---|---|---|");
        for (var lo = 0; lo < 100; lo += 10)
        {
            var bucket = knownItems.Where(x => x.MH * 100 >= lo && x.MH * 100 < lo + 10).ToList();
            if (bucket.Count < 20) continue;
            var pred = bucket.Average(x => x.MH);
            var act = bucket.Average(x => x.AH);
            Console.WriteLine($"| {lo}-{lo + 10}% | {bucket.Count} | {pred * 100:F1}% | {act * 100:F1}% | {(act - pred) * 100:+0.0;-0.0}pp |");
        }
    }

    /// <summary>半全场联合分布：上半场 λ×0.45、下半场 λ×0.55 各跑一次独立泊松，再合并。</summary>
    private static Dictionary<string, double> HalfFullJoint(double home, double away)
    {
        const double share = 0.45;
        const int maxGoals = 6;
        var result = new Dictionary<string, double>();
        double total = 0;

        for (var h1 = 0; h1 <= maxGoals; h1++)
        for (var a1 = 0; a1 <= maxGoals; a1++)
        for (var h2 = 0; h2 <= maxGoals; h2++)
        for (var a2 = 0; a2 <= maxGoals; a2++)
        {
            var p1 = Poisson(h1, home * share) * Poisson(a1, away * share);
            var p2 = Poisson(h2, home * (1 - share)) * Poisson(a2, away * (1 - share));
            var p = p1 * p2;
            if (p <= 0) continue;
            var code = HalfFullCode(h1, a1, h1 + h2, a1 + a2);
            result[code] = result.GetValueOrDefault(code) + p;
            total += p;
        }
        if (total <= 0) return result;
        foreach (var k in result.Keys.ToList()) result[k] /= total;
        return result;
    }

    /// <summary>半全场结果编码：3-3 胜胜 / 1-1 平平 / 0-0 负负，第一个是半场、第二个是全场。</summary>
    private static string HalfFullCode(int halfHome, int halfAway, int fullHome, int fullAway)
    {
        char Res(int h, int a) => h > a ? '3' : h == a ? '1' : '0';
        return $"{Res(halfHome, halfAway)}-{Res(fullHome, fullAway)}";
    }

    private static double Poisson(int k, double lambda)
    {
        if (lambda <= 0) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }

    /// <summary>校准分桶打印：按预测概率 0-10/10-20/... 分桶，看实际命中频率。</summary>
    private static void PrintCalibration(List<(double Pred, int Hit)> items, string label)
    {
        Console.WriteLine($"| {label}概率 | 场次 | 平均预测 | 实际命中率 | 偏差 |");
        Console.WriteLine("|---|---|---|---|---|");
        for (var lo = 0; lo < 100; lo += 10)
        {
            var bucket = items.Where(x => x.Pred * 100 >= lo && x.Pred * 100 < lo + 10).ToList();
            if (bucket.Count < 10) continue;
            var pred = bucket.Average(x => x.Pred);
            var act = bucket.Average(x => x.Hit);
            Console.WriteLine($"| {lo}-{lo + 10}% | {bucket.Count} | {pred * 100:F1}% | {act * 100:F1}% | {(act - pred) * 100:+0.0;-0.0}pp |");
        }
    }

    private static double Sqr(double p, double y) => (p - y) * (p - y);

    /// <summary>A/B 用的四项汇总指标，口径与正文一致，避免两处算法漂移。</summary>
    private static (double Brier, double Log, double Std, double TotalDev) Stat(List<Rec> rs)
    {
        var brier = rs.Average(x => Sqr(x.PH, x.AH) + Sqr(x.PD, x.AD) + Sqr(x.PA, x.AA));
        var log = rs.Average(x => -Math.Log(Math.Max(1e-9, x.AH > 0.5 ? x.PH : x.AD > 0.5 ? x.PD : x.PA)));
        var mean = rs.Average(x => x.PH);
        var std = Math.Sqrt(rs.Average(x => Math.Pow(x.PH - mean, 2)));
        var withTotal = rs.Where(x => !double.IsNaN(x.ModelTotal)).ToList();
        var dev = withTotal.Count == 0 ? 0 : withTotal.Average(x => x.ModelTotal) - rs.Average(x => x.ActualTotal);
        return (brier, log, std, dev);
    }

    /// <summary>从「大于盘口」的市场概率反解泊松期望总进球。二分法，单调可靠。</summary>
    private static double ImpliedTotal(double pOver, double line)
    {
        var k = (int)Math.Floor(line) + 1;   // 2.5 -> 至少 3 球才算大
        double lo = 0.2, hi = 9.0;
        for (var i = 0; i < 80; i++)
        {
            var mid = (lo + hi) / 2;
            var p = 1 - PoissonCdf(mid, k - 1);
            if (p > pOver) hi = mid; else lo = mid;
            if (hi - lo < 1e-12) break;
        }
        return (lo + hi) / 2;
    }

    /// <summary>P(X ≤ k)，X ~ Poisson(lambda)</summary>
    private static double PoissonCdf(double lambda, int k)
    {
        if (k < 0) return 0;
        var term = Math.Exp(-lambda);
        var sum = term;
        for (var i = 1; i <= k; i++)
        {
            term *= lambda / i;
            sum += term;
        }
        return Math.Min(1.0, sum);
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("## " + title);
        Console.WriteLine();
    }

    private sealed record Rec(
        string League, string Teams,
        double PH, double PD, double PA,
        double MH, double MD, double MA,
        double AH, double AD, double AA,
        double ModelTotal, double? MarketTotal, double ActualTotal,
        int MinTeamGames, bool KnownBoth);
}

internal static class RowExt
{
    public static string HelpText(this Program.Row r) => $"{r.HomeTeam} vs {r.AwayTeam}";
}
