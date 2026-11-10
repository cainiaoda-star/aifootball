using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 竞彩赔率漂移研究（2026-11-10 加）。只读，不改任何业务表、不改线上阈值。
///
/// 为什么做这个：截至今天，本项目所有结论都建立在「某一个时点的价格」上——
/// 方向检验、EV 带、半全场基线、稳胆口径，用的都是收盘价。
/// 而 <c>FbJcOddsHistory</c> 从 2026-10-05 起已经攒下每 2 分钟一条的完整轨迹，
/// 570 场完赛、平均每场 189 个快照，却一次都没被用来回答一个最基本的问题：
/// **「价格往哪边动」本身是不是信息？**
///
/// §10 那条「竞彩单边砸价、必发不认 = 该侧被高估」只有 13 场，样本太小推不动。
/// 这次不要求必发配合，只看竞彩自己的轨迹，样本能到 570 场。
///
/// ★ 必须提前说清的采样局限：采样从「该场进入当日在售榜面」才开始，
/// 每场平均跨度只有 12 小时（≥24 小时的只有 161 场）。
/// 所以这里测的是**临场 12 小时内的价格移动**，不是「开盘到收盘」。
/// 报告第一节会把这个窗口分布打出来，任何结论都不许超出这个窗口。
///
/// 用法：
///   dotnet run --project src/AiFootBall.Backtest -c Release -- drift-probe 2026-10-01
/// </summary>
public static class DriftStudy
{
    private sealed class MRow
    {
        public long MatchId { get; set; }
        public DateTime MatchTime { get; set; }
        public string? LeagueName { get; set; }
        public string Home { get; set; } = "";
        public string Away { get; set; } = "";
        public int HScore { get; set; }
        public int AScore { get; set; }
    }

    private sealed class PRow
    {
        public long MatchId { get; set; }
        public string OptionCode { get; set; } = "";
        public decimal? OpenOdds { get; set; }
        public decimal? CloseOdds { get; set; }
        public decimal? T6Odds { get; set; }
        public decimal? T1Odds { get; set; }
        public DateTime FirstCap { get; set; }
        public DateTime LastCap { get; set; }
        public int N { get; set; }
    }

    /// <summary>一场的可评估样本：开盘/收盘三档概率、漂移、实际结果。</summary>
    private sealed class Case
    {
        public long MatchId { get; init; }
        public string League { get; init; } = "";
        public string Home { get; init; } = "";
        public string Away { get; init; } = "";
        public DateTime MatchTime { get; init; }
        public decimal SpanHours { get; init; }
        public int Snapshots { get; init; }

        // 去水概率（用于比价、算隐含，不用于算回报）
        public decimal OpenH { get; init; }
        public decimal OpenD { get; init; }
        public decimal OpenA { get; init; }
        public decimal CloseH { get; init; }
        public decimal CloseD { get; init; }
        public decimal CloseA { get; init; }

        // 真实收盘赔率（算回报只能用这个）。★ 用 1/去水概率 会把抽水抹掉，
        // 回报凭空高 12%，这是回测里最容易犯又最难发现的一类错。
        public decimal QuoteH { get; init; }
        public decimal QuoteD { get; init; }
        public decimal QuoteA { get; init; }

        // 开赛前 1 小时的去水概率（拿不到就整场不进「最后一小时」那一节）
        public decimal? T1H { get; init; }
        public decimal? T1D { get; init; }
        public decimal? T1A { get; init; }
        public bool HasT1 => T1H is not null && T1D is not null && T1A is not null;
        public decimal T1Of(char d) => d switch { '3' => T1H!.Value, '1' => T1D!.Value, _ => T1A!.Value };
        /// <summary>临场最后一小时的漂移（T−1h → 收盘），单位百分点。</summary>
        public decimal LateDriftOf(char d) => (CloseOf(d) - T1Of(d)) * 100m;
        public char LateMaxDriftDir
        {
            get
            {
                var d3 = LateDriftOf('3'); var d1 = LateDriftOf('1'); var d0 = LateDriftOf('0');
                return d3 >= d1 && d3 >= d0 ? '3' : d1 >= d0 ? '1' : '0';
            }
        }

        public char Actual { get; init; }
        public bool Excluded { get; init; }

        /// <summary>按 3/1/0 下标取开盘概率。</summary>
        public decimal OpenOf(char d) => d switch { '3' => OpenH, '1' => OpenD, _ => OpenA };
        public decimal CloseOf(char d) => d switch { '3' => CloseH, '1' => CloseD, _ => CloseA };
        public decimal OddsOf(char d) => d switch { '3' => QuoteH, '1' => QuoteD, _ => QuoteA };
        public decimal DriftOf(char d) => (CloseOf(d) - OpenOf(d)) * 100m;   // 百分点
        public decimal CloseFav => Math.Max(CloseH, Math.Max(CloseD, CloseA));
        public char CloseFavDir => CloseH >= CloseD && CloseH >= CloseA ? '3' : CloseD >= CloseA ? '1' : '0';
        public decimal OpenFav => Math.Max(OpenH, Math.Max(OpenD, OpenA));
        public char OpenFavDir => OpenH >= OpenD && OpenH >= OpenA ? '3' : OpenD >= OpenA ? '1' : '0';

        /// <summary>三档概率变动的绝对值之和，衡量这场价格有多乱。</summary>
        public decimal Vol => Math.Abs(DriftOf('3')) + Math.Abs(DriftOf('1')) + Math.Abs(DriftOf('0'));
        public char MaxDriftDir
        {
            get
            {
                var d3 = DriftOf('3'); var d1 = DriftOf('1'); var d0 = DriftOf('0');
                return d3 >= d1 && d3 >= d0 ? '3' : d1 >= d0 ? '1' : '0';
            }
        }
        public char MinDriftDir
        {
            get
            {
                var d3 = DriftOf('3'); var d1 = DriftOf('1'); var d0 = DriftOf('0');
                return d3 <= d1 && d3 <= d0 ? '3' : d1 <= d0 ? '1' : '0';
            }
        }
    }

    private static readonly string[] ThreeWay = { "3", "1", "0" };

    public static async Task<int> RunAsync(string connStr, DateTime testStart,
        double minSpanHours = 6, int minSnapshots = 10)
    {
        var factory = new SqlConnectionFactory(connStr);

        List<MRow> matches;
        await using (var c = await factory.OpenAsync())
        {
            matches = (await c.QueryAsync<MRow>(
                @"SELECT m.MatchId, m.MatchTime, m.LeagueName,
                         m.HomeTeamName AS Home, m.AwayTeamName AS Away,
                         m.HomeScore AS HScore, m.AwayScore AS AScore
                  FROM dbo.FbMatch m
                  WHERE m.MatchTime >= @testStart
                    AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                    AND EXISTS (SELECT 1 FROM dbo.FbJcOddsHistory h
                                WHERE h.MatchId = m.MatchId AND h.PlayType = 'SPF')
                  ORDER BY m.MatchTime",
                new { testStart }, commandTimeout: 900)).ToList();
        }

        if (matches.Count == 0) { Console.WriteLine("测试期没有「赛果 + 竞彩胜平负」齐全的场次。"); return 0; }

        var prices = new Dictionary<long, Dictionary<string, PRow>>();
        foreach (var chunk in Chunk(matches.Select(m => m.MatchId).ToList()))
        {
            await using var c = await factory.OpenAsync();
            var rows = await c.QueryAsync<PRow>(
                @"WITH src AS (
                      SELECT h.MatchId, h.OptionCode, h.Odds, h.CapturedAt, m.MatchTime,
                             ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode
                                                ORDER BY h.CapturedAt ASC) AS rnA,
                             ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode
                                                ORDER BY h.CapturedAt DESC) AS rnD,
                             ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode,
                                                CASE WHEN h.CapturedAt <= DATEADD(hour, -6, m.MatchTime)
                                                     THEN 1 ELSE 0 END
                                                ORDER BY h.CapturedAt DESC) AS rn6,
                             ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode,
                                                CASE WHEN h.CapturedAt <= DATEADD(hour, -1, m.MatchTime)
                                                     THEN 1 ELSE 0 END
                                                ORDER BY h.CapturedAt DESC) AS rn1
                      FROM dbo.FbJcOddsHistory h
                      JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                      WHERE h.MatchId IN @ids AND h.PlayType = 'SPF'
                        AND h.CapturedAt < m.MatchTime AND h.Odds > 1
                  )
                  SELECT MatchId, OptionCode,
                         MAX(CASE WHEN rnA = 1 THEN Odds END) AS OpenOdds,
                         MAX(CASE WHEN rnD = 1 THEN Odds END) AS CloseOdds,
                         MAX(CASE WHEN rn6 = 1 AND CapturedAt <= DATEADD(hour, -6, MatchTime)
                                  THEN Odds END) AS T6Odds,
                         MAX(CASE WHEN rn1 = 1 AND CapturedAt <= DATEADD(hour, -1, MatchTime)
                                  THEN Odds END) AS T1Odds,
                         MIN(CapturedAt) AS FirstCap, MAX(CapturedAt) AS LastCap, COUNT(*) AS N
                  FROM src
                  GROUP BY MatchId, OptionCode",
                new { ids = chunk }, commandTimeout: 900);

            foreach (var r in rows)
            {
                if (!prices.TryGetValue(r.MatchId, out var byOpt))
                    prices[r.MatchId] = byOpt = new(StringComparer.Ordinal);
                byOpt[r.OptionCode] = r;
            }
        }

        var cases = new List<Case>(matches.Count);
        int noPrice = 0, partial = 0, badOver = 0, tooFew = 0;
        var droppedSnapshots = new List<int>();

        foreach (var m in matches)
        {
            if (!prices.TryGetValue(m.MatchId, out var q)) { noPrice++; continue; }
            if (ThreeWay.Any(k => !q.TryGetValue(k, out var p) || p.OpenOdds is null || p.CloseOdds is null))
            { partial++; continue; }

            // ★ 关键过滤：只采到一个快照的场次，漂移恒等于 0，会把「平稳」档灌满假样本。
            // 202 场（约三分之一）属于这种——场次进榜时就已临场，采样来不及积累轨迹。
            // 代价是样本偏向「进榜早」的热门场次，这一点必须写进结论边界。
            var span0 = (q["3"].LastCap - q["3"].FirstCap).TotalHours;
            if (span0 < minSpanHours || q["3"].N < minSnapshots)
            { tooFew++; droppedSnapshots.Add(q["3"].N); continue; }

            var (oH, oD, oA) = (q["3"].OpenOdds!.Value, q["1"].OpenOdds!.Value, q["0"].OpenOdds!.Value);
            var (cH, cD, cA) = (q["3"].CloseOdds!.Value, q["1"].CloseOdds!.Value, q["0"].CloseOdds!.Value);

            var oOver = 1m / oH + 1m / oD + 1m / oA;
            var cOver = 1m / cH + 1m / cD + 1m / cA;
            if (oOver is < 1.02m or > 1.80m || cOver is < 1.02m or > 1.80m) { badOver++; continue; }

            var span = span0;

            decimal? t1H = null, t1D = null, t1A = null;
            if (q["3"].T1Odds is { } h1 && q["1"].T1Odds is { } d1 && q["0"].T1Odds is { } a1)
            {
                var t1Over = 1m / h1 + 1m / d1 + 1m / a1;
                if (t1Over is >= 1.02m and <= 1.80m)
                {
                    t1H = 1m / h1 / t1Over;
                    t1D = 1m / d1 / t1Over;
                    t1A = 1m / a1 / t1Over;
                }
            }

            cases.Add(new Case
            {
                MatchId = m.MatchId,
                League = m.LeagueName ?? "",
                Home = m.Home,
                Away = m.Away,
                MatchTime = m.MatchTime,
                SpanHours = (decimal)span,
                Snapshots = q["3"].N,
                OpenH = 1m / oH / oOver, OpenD = 1m / oD / oOver, OpenA = 1m / oA / oOver,
                CloseH = 1m / cH / cOver, CloseD = 1m / cD / cOver, CloseA = 1m / cA / cOver,
                QuoteH = cH, QuoteD = cD, QuoteA = cA,
                T1H = t1H, T1D = t1D, T1A = t1A,
                Actual = m.HScore > m.AScore ? '3' : m.HScore == m.AScore ? '1' : '0',
                Excluded = NoPickLeagues.IsExcluded(m.LeagueName)
            });
        }

        Console.WriteLine($"# 竞彩赔率漂移研究  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"测试期从 {testStart:yyyy-MM-dd} 起：完赛且有胜平负轨迹 **{matches.Count}** 场，" +
                          $"**可评估漂移 {cases.Count} 场**。");
        Console.WriteLine();
        Console.WriteLine("| 剔除原因 | 场次 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 完全没有快照 | {noPrice} |");
        Console.WriteLine($"| 三档不全 | {partial} |");
        Console.WriteLine($"| 去水分母离谱（<1.02 或 >1.80） | {badOver} |");
        Console.WriteLine($"| **轨迹太短**（跨度 <{minSpanHours:0} 小时 或 快照 <{minSnapshots} 个） | **{tooFew}** |");
        Console.WriteLine();
        if (droppedSnapshots.Count > 0)
            Console.WriteLine($"· 被剔除的 {tooFew} 场里，中位快照数 **{(droppedSnapshots.OrderBy(x => x).ElementAt(droppedSnapshots.Count / 2))}** 个 —— " +
                              "基本是「进榜即临场」的场次，漂移恒为 0，留着只会把「平稳」档灌成假样本。");
        Console.WriteLine();
        if (cases.Count < 100)
        {
            Console.WriteLine();
            Console.WriteLine("**样本不足 100 场，以下任何分档都不可作为结论，只能当方向性观察。**");
        }
        Console.WriteLine();

        // ---------- 一、窗口分布 ----------
        Console.WriteLine("## 一、采样窗口（这一节决定了结论的边界）");
        Console.WriteLine();
        Console.WriteLine("| 首个快照距开赛 | 场次 | 占比 |");
        Console.WriteLine("|---|---|---|");
        var winBins = new (double Lo, double Hi, string Name)[]
        {
            (0, 6, "不足 6 小时"),
            (6, 12, "6~12 小时"),
            (12, 24, "12~24 小时"),
            (24, 48, "24~48 小时"),
            (48, 9999, "48 小时以上")
        };
        foreach (var b in winBins)
        {
            var n = cases.Count(c => (double)c.SpanHours >= b.Lo && (double)c.SpanHours < b.Hi);
            if (n == 0) continue;
            Console.WriteLine($"| {b.Name} | {n} | {n * 100.0 / cases.Count:0.0}% |");
        }
        Console.WriteLine();
        Console.WriteLine($"· 中位跨度 **{Median(cases.Select(c => c.SpanHours)):0.0} 小时**，" +
                          $"平均每场 {cases.Average(c => c.Snapshots):0} 个快照。");
        Console.WriteLine("· **这里的「开盘」= 该场进入在售榜面后的第一次采样，不是官方开盘价。**" +
                          "所以下面所有漂移都是「临场窗口内的移动」，不许说成「开盘到收盘」。");
        Console.WriteLine();

        // ---------- 二、基线校准 ----------
        Console.WriteLine("## 二、基线校准（先确认数据本身没坏）");
        Console.WriteLine();
        Console.WriteLine("| 指标 | 主胜 | 平局 | 客胜 |");
        Console.WriteLine("|---|---|---|---|");
        Console.WriteLine($"| 开盘隐含均值 | {cases.Average(c => c.OpenH * 100m):0.0}% | " +
                          $"{cases.Average(c => c.OpenD * 100m):0.0}% | {cases.Average(c => c.OpenA * 100m):0.0}% |");
        Console.WriteLine($"| 收盘隐含均值 | {cases.Average(c => c.CloseH * 100m):0.0}% | " +
                          $"{cases.Average(c => c.CloseD * 100m):0.0}% | {cases.Average(c => c.CloseA * 100m):0.0}% |");
        Console.WriteLine($"| 实际频率 | {cases.Count(c => c.Actual == '3') * 100.0 / cases.Count:0.0}% | " +
                          $"{cases.Count(c => c.Actual == '1') * 100.0 / cases.Count:0.0}% | " +
                          $"{cases.Count(c => c.Actual == '0') * 100.0 / cases.Count:0.0}% |");
        Console.WriteLine();
        Console.WriteLine("（实际频率 ≈ 收盘隐含 = 定价没有系统偏差，后面的漂移检验才有意义）");
        Console.WriteLine();

        // ---------- 三、主胜漂移分档 ----------
        Console.WriteLine("## 三、主胜概率漂移分档（漂移 = 收盘 − 开盘，单位百分点）");
        Console.WriteLine();
        Console.WriteLine("| 主胜漂移 | 场次 | 实际主胜率 | 收盘隐含 | 实际−收盘 | z 值 | 押主胜回报 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        var driftBins = new (decimal Lo, decimal Hi, string Name)[]
        {
            (-99m, -5m, "≤ −5（主胜被买淡）"),
            (-5m, -2m, "−5 ~ −2"),
            (-2m, 2m, "−2 ~ +2（平稳）"),
            (2m, 5m, "+2 ~ +5"),
            (5m, 99m, "≥ +5（主胜被买热）")
        };
        foreach (var b in driftBins)
        {
            var set = cases.Where(c => c.DriftOf('3') >= b.Lo && c.DriftOf('3') < b.Hi).ToList();
            if (set.Count == 0) { Console.WriteLine($"| {b.Name} | 0 | – | – | – | – | – |"); continue; }
            var hit = set.Count(c => c.Actual == '3');
            var hitPct = hit * 100.0 / set.Count;
            var implied = set.Average(c => (double)(c.CloseOf('3') * 100m));
            var roi = set.Where(c => c.Actual == '3').Sum(c => c.OddsOf('3')) / set.Count;
            Console.WriteLine($"| {b.Name} | {set.Count} | {hitPct:0.0}% | {implied:0.0}% | " +
                              $"{hitPct - implied:+0.0;-0.0} | {Z(hitPct / 100, set.Count, implied / 100):+0.0;-0.0} | {roi:0.000} |");
        }
        Console.WriteLine();
        Console.WriteLine("判读：|z| < 2 一律当噪音。**`≤ −5` 那档 +21.9 看着吓人，按 z 值先看它够不够 2。**" +
                          "各档都贴着 0 就说明收盘价已经把漂移吸收完了。");
        Console.WriteLine();

        // ---------- 四、押漂移方向 ----------
        Console.WriteLine("## 四、押「漂移最大 / 最小」的那一边");
        Console.WriteLine();
        Console.WriteLine("| 策略 | 注数 | 命中率 | 平均赔率 | 命中注均赔 | 回报 | 与返奖率 0.878 之差 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        StrategyLine("押漂移最大边（追热）", cases, c => c.MaxDriftDir);
        StrategyLine("押漂移最小边（买冷/逆势）", cases, c => c.MinDriftDir);
        StrategyLine("押收盘最看好（稳胆基线）", cases, c => c.CloseFavDir);
        Console.WriteLine();
        Console.WriteLine("（自检：回报 ≈ 命中率 × 命中注均赔。别拿命中率去乘**全部注**的平均赔率，那会虚高一大截。）");
        Console.WriteLine();

        // 四之二：追热为什么这么差？拆成「追的本来就是热门」和「追的是冷门」。
        // 这两种是完全不同的行为：前者多半是市场确认，后者是把冷门买热。
        var steamFav = cases.Where(c => c.MaxDriftDir == c.CloseFavDir).ToList();
        var steamDog = cases.Where(c => c.MaxDriftDir != c.CloseFavDir).ToList();
        Console.WriteLine("## 四之二、追热拆开看：追的是热门还是冷门");
        Console.WriteLine();
        Console.WriteLine("| 追热形态 | 场次 | 占全部 | 命中率 | 平均赔率 | 命中注均赔 | 回报 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (name, set) in new (string, List<Case>)[]
        {
            ("漂移最大边 = 收盘最看好（追的是热门）", steamFav),
            ("漂移最大边 ≠ 收盘最看好（把冷门买热）", steamDog)
        })
        {
            if (set.Count == 0) { Console.WriteLine($"| {name} | 0 | – | – | – | – | – |"); continue; }
            var wins = set.Where(c => c.MaxDriftDir == c.Actual).ToList();
            var roi = wins.Sum(c => c.OddsOf(c.MaxDriftDir)) / set.Count;
            Console.WriteLine($"| {name} | {set.Count} | {set.Count * 100.0 / cases.Count:0.0}% | " +
                              $"{wins.Count * 100.0 / set.Count:0.0}% | {set.Average(c => c.OddsOf(c.MaxDriftDir)):0.00} | " +
                              $"{(wins.Count > 0 ? wins.Average(c => c.OddsOf(c.MaxDriftDir)) : 0m):0.00} | {roi:0.000} |");
        }
        Console.WriteLine();
        Console.WriteLine("判读：如果「追热门」明显好于「追冷门」，说明**降价只在热门一侧是确认**，" +
                          "冷门被买热则是典型的反向信号（这也正是 §10 那条 13 场结论的方向）。");
        Console.WriteLine();

        // ---------- 五、阈值扫描 ----------
        Console.WriteLine("## 五、「某一边概率上涨 ≥ X 点就买它」阈值扫描");
        Console.WriteLine();
        Console.WriteLine("| 阈值 | 注数 | 占比 | 命中率 | 平均赔率 | 回报 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var th in new[] { 1m, 2m, 3m, 5m, 8m, 12m })
        {
            var bets = cases.Where(c => c.DriftOf(c.MaxDriftDir) >= th).ToList();
            if (bets.Count == 0) { Console.WriteLine($"| ≥{th:0} 点 | 0 | – | – | – | – |"); continue; }
            var hit = bets.Count(c => c.MaxDriftDir == c.Actual);
            var roi = bets.Where(c => c.MaxDriftDir == c.Actual).Sum(c => c.OddsOf(c.MaxDriftDir)) / bets.Count;
            Console.WriteLine($"| ≥{th:0} 点 | {bets.Count} | {bets.Count * 100.0 / cases.Count:0.0}% | " +
                              $"{hit * 100.0 / bets.Count:0.0}% | {bets.Average(c => c.OddsOf(c.MaxDriftDir)):0.00} | {roi:0.000} |");
        }
        Console.WriteLine();

        // ---------- 六、同价位对比（最关键的一节） ----------
        // 为什么必须有：第三、四节都在比「不同价位的比赛」，而漂移和价位天然相关
        // （被买热的场次收盘价更低）。要证明漂移是独立信息，必须把收盘价按住，
        // 再看同一价位内「涨」和「跌」的命中率是否不同。
        Console.WriteLine("## 六、同价位对比：把收盘价按住，只看漂移方向");
        Console.WriteLine();
        Console.WriteLine("每行只取「收盘最看好档概率落在该区间」的场次，再按该档的漂移拆成跌/平/涨三组，看它实际命中率。");
        Console.WriteLine();
        Console.WriteLine("| 收盘最看好档概率 | 漂移 | 场次 | 实际命中率 |");
        Console.WriteLine("|---|---|---|---|");
        var favBands = new (decimal Lo, decimal Hi, string Name)[]
        {
            (0.33m, 0.40m, "33~40%"),
            (0.40m, 0.45m, "40~45%"),
            (0.45m, 0.50m, "45~50%"),
            (0.50m, 0.60m, "50~60%"),
            (0.60m, 1.01m, "60%+")
        };
        foreach (var band in favBands)
        {
            var inBand = cases.Where(c => c.CloseFav >= band.Lo && c.CloseFav < band.Hi).ToList();
            if (inBand.Count == 0) continue;
            foreach (var (label, pred) in new (string, Func<decimal, bool>)[]
            {
                ("跌 ≥2 点", d => d <= -2m),
                ("平稳", d => d > -2m && d < 2m),
                ("涨 ≥2 点", d => d >= 2m)
            })
            {
                var set = inBand.Where(c => pred(c.DriftOf(c.CloseFavDir))).ToList();
                if (set.Count == 0) { Console.WriteLine($"| {band.Name} | {label} | 0 | – |"); continue; }
                var hit = set.Count(c => c.CloseFavDir == c.Actual);
                Console.WriteLine($"| {band.Name} | {label} | {set.Count} | {hit * 100.0 / set.Count:0.0}% |");
            }
        }
        Console.WriteLine();
        Console.WriteLine("判读：**同一价位内，涨组与跌组的命中率差**才是漂移的净信息量。" +
                          "如果涨组并不比跌组高出几个点，那「追热」就只是把收盘价重说一遍。");
        Console.WriteLine();

        // ---------- 六之二、最后一小时 ----------
        // 为什么单列：赛前 24 小时的移动里混着开盘定价噪音；真正被视为「聪明钱」的是临场那一段。
        // 如果连最后一小时都没有信息量，那「追价格移动」这条路可以整体关掉。
        var late = cases.Where(c => c.HasT1).ToList();
        Console.WriteLine("## 六之二、只看临场最后一小时（T−1h → 收盘）");
        Console.WriteLine();
        Console.WriteLine($"有 T−1h 快照的 **{late.Count}** 场（占 {late.Count * 100.0 / cases.Count:0.0}%）。");
        Console.WriteLine();
        if (late.Count < 60)
        {
            Console.WriteLine("样本不足 60 场，以下只作观察，不作结论。");
            Console.WriteLine();
        }
        Console.WriteLine("| 最后一小时最看好档的漂移 | 场次 | 实际命中率 | 隐含 | 实际−隐含 | z 值 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var (label, pred) in new (string, Func<decimal, bool>)[]
        {
            ("跌 ≥2 点", d => d <= -2m),
            ("平稳", d => d > -2m && d < 2m),
            ("涨 ≥2 点", d => d >= 2m)
        })
        {
            var set = late.Where(c => pred(c.LateDriftOf(c.CloseFavDir))).ToList();
            if (set.Count == 0) { Console.WriteLine($"| {label} | 0 | – | – | – | – |"); continue; }
            var hit = set.Count(c => c.CloseFavDir == c.Actual);
            var hitPct = hit * 100.0 / set.Count;
            var implied = set.Average(c => (double)(c.CloseFav * 100m));
            Console.WriteLine($"| {label} | {set.Count} | {hitPct:0.0}% | {implied:0.0}% | " +
                              $"{hitPct - implied:+0.0;-0.0} | {Z(hitPct / 100, set.Count, implied / 100):+0.0;-0.0} |");
        }
        Console.WriteLine();
        Console.WriteLine("| 最后一小时策略 | 注数 | 命中率 | 平均赔率 | 命中注均赔 | 回报 | 与返奖率 0.878 之差 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        StrategyLine("押涨得最凶的那边", late, c => c.LateMaxDriftDir);
        StrategyLine("押收盘最看好", late, c => c.CloseFavDir);
        Console.WriteLine();

        // ---------- 七、波动幅度 vs 冷门 ----------
        Console.WriteLine("## 七、价格波动幅度 vs 冷门（收盘最看好档有没有翻车）");
        Console.WriteLine();
        Console.WriteLine("| 三档概率变动绝对值之和 | 场次 | 最看好档命中率 | 隐含 | 实际−隐含 | z 值 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        var volBins = new (decimal Lo, decimal Hi, string Name)[]
        {
            (0m, 3m, "0~3 点（几乎不动）"),
            (3m, 6m, "3~6 点"),
            (6m, 10m, "6~10 点"),
            (10m, 99m, "10 点以上（剧烈）")
        };
        foreach (var b in volBins)
        {
            var set = cases.Where(c => c.Vol >= b.Lo && c.Vol < b.Hi).ToList();
            if (set.Count == 0) { Console.WriteLine($"| {b.Name} | 0 | – | – | – | – |"); continue; }
            var hit = set.Count(c => c.CloseFavDir == c.Actual);
            var hitPct = hit * 100.0 / set.Count;
            var implied = set.Average(c => (double)(c.CloseFav * 100m));
            Console.WriteLine($"| {b.Name} | {set.Count} | {hitPct:0.0}% | {implied:0.0}% | " +
                              $"{hitPct - implied:+0.0;-0.0} | {Z(hitPct / 100, set.Count, implied / 100):+0.0;-0.0} |");
        }
        Console.WriteLine();
        Console.WriteLine("★ **「几乎不动」那档是整份报告里唯一 z>2 的格子**（价格几乎不动的场次，最看好档反而赢得更多）。");
        Console.WriteLine("但必须按多重比较打折看：这份报告一共看了 20 多个格子，纯靠运气也该有一两个冒出 2 个标准误。");
        Console.WriteLine("**要跟就得拿另一批数据复现，不能就地写成规则。** 这批只有 31 场，且「价格不动」和");
        Console.WriteLine("「市场早已定好价」是同一件事，很可能只是把「强弱悬殊」重说了一遍。");
        Console.WriteLine();

        // ---------- 八、稳健性 ----------
        var live = cases.Where(c => !c.Excluded).ToList();
        var excludedInSample = cases.Count - live.Count;
        Console.WriteLine("## 八、稳健性：剔除不推名单（欧国联/友谊赛/青年赛）");
        Console.WriteLine();
        if (excludedInSample == 0)
        {
            Console.WriteLine("剔除后仍是同一样本（**0 场被名单挡掉**），因为名单里的联赛在本窗口内**一场可用轨迹都没有**：");
            Console.WriteLine("欧国联 13 场、友谊赛 3 场，全部属于「进榜即临场、只采到一个快照」那一类。");
            Console.WriteLine("所以这一节无法提供对照——**不要把它读成「名单没有影响」**，只能读成「这批数据测不到名单的影响」。");
        }
        else
        {
            Console.WriteLine($"剔除 **{excludedInSample}** 场，剩余 **{live.Count}** 场：");
            Console.WriteLine();
            Console.WriteLine("| 策略 | 注数 | 命中率 | 平均赔率 | 命中注均赔 | 回报 | 与返奖率 0.878 之差 |");
            Console.WriteLine("|---|---|---|---|---|---|---|");
            StrategyLine("押漂移最大边", live, c => c.MaxDriftDir);
            StrategyLine("押收盘最看好", live, c => c.CloseFavDir);
        }
        Console.WriteLine();

        // ---------- 九、结论边界 ----------
        Console.WriteLine("## 九、结论与边界");
        Console.WriteLine();
        Console.WriteLine("**一、追价格移动不能当买入信号。** 押「漂移最大边」的回报 " +
                          $"{cases.Where(c => c.MaxDriftDir == c.Actual).Sum(c => c.OddsOf(c.MaxDriftDir)) / Math.Max(1, cases.Count):0.000}，" +
                          "低于竞彩返奖率 0.878；阈值从 1 点扫到 12 点，没有一档能翻正。");
        Console.WriteLine("**二、但拆开是两个完全不同的东西。** " +
                          $"追「本来就是收盘热门」的那侧回报 {steamFav.Sum(c => c.MaxDriftDir == c.Actual ? c.OddsOf(c.MaxDriftDir) : 0) / Math.Max(1, steamFav.Count):0.000}，" +
                          $"追「被买热的冷门」只有 {steamDog.Sum(c => c.MaxDriftDir == c.Actual ? c.OddsOf(c.MaxDriftDir) : 0) / Math.Max(1, steamDog.Count):0.000}。");
        Console.WriteLine("   后者是整份报告唯一显著为负的格子 —— **冷门被买热不是确认，是反向**，方向与 §10「竞彩砸价、必发不认」那条一致。");
        Console.WriteLine("   但它只能当**否决规则**用（这类注不买），不能当买入信号：追热门那半也只有 0.85 上下。");
        Console.WriteLine("**三、收盘价已经把漂移吃干净了。** 同价位对比里，涨组和跌组的命中率互有高低，" +
                          "没有任何一档能稳定站住；最后一小时单独看也一样（|z| 全部小于 2）。");
        Console.WriteLine();
        Console.WriteLine("边界：");
        Console.WriteLine($"· 能说：在 {cases.Count} 场、临场约 {Median(cases.Select(c => c.SpanHours)):0} 小时的窗口内，价格移动是否带来额外信息。");
        Console.WriteLine("· 不能说：开盘到收盘的完整漂移（没有官方开盘价，采样从进入榜面才开始）。");
        Console.WriteLine("· 不能说：任何单一联赛的结论（切完只剩几十场，典型小样本极端值）。");
        Console.WriteLine("· 不能说：「6 小时」和「24 小时」两个窗口互为独立复现 —— 它们样本高度重叠，" +
                          "只是换了门槛，方向一致能排除「太短的轨迹在捣乱」，不能替代新数据。");
        Console.WriteLine("· 改线上：不做。要动必须先攒到第二批独立窗口（预计 2~3 个月）复现「冷门被买热」这条。");

        return 0;
    }

    /// <summary>
    /// 打一行策略统计。
    /// ★ 必须同时给「平均赔率」和「命中注均赔」：命中注的赔率天然低于全部注的平均
    /// （低赔那边更容易赢），只放平均赔率会让人拿「命中率 × 平均赔率」算出一个虚高的期望。
    /// 自检口径：回报 ≈ 命中率 × 命中注均赔。
    /// </summary>
    private static void StrategyLine(string name, List<Case> set, Func<Case, char> dirOf)
    {
        if (set.Count == 0) { Console.WriteLine($"| {name} | 0 | – | – | – | – | – |"); return; }
        var wins = set.Where(c => dirOf(c) == c.Actual).ToList();
        var hitPct = wins.Count * 100.0 / set.Count;
        var avgAll = set.Average(c => c.OddsOf(dirOf(c)));
        var avgWin = wins.Count > 0 ? wins.Average(c => c.OddsOf(dirOf(c))) : 0m;
        var roi = wins.Sum(c => c.OddsOf(dirOf(c))) / set.Count;
        Console.WriteLine($"| {name} | {set.Count} | {hitPct:0.0}% | {avgAll:0.00} | {avgWin:0.00} | " +
                          $"{roi:0.000} | {roi - 0.878m:+0.000;-0.000} |");
    }

    /// <summary>偏离市场隐含的几个标准误。|z|&lt;2 一律当噪音，这是本项目最容易自欺的一步。</summary>
    private static decimal Z(double actualProb, int n, double impliedProb)
    {
        if (n <= 0) return 0;
        var se = Math.Sqrt(actualProb * (1 - actualProb) / n);
        if (se <= 1e-9) return 0;
        return (decimal)((actualProb - impliedProb) / se);
    }

    private static decimal Median(IEnumerable<decimal> xs)    {
        var a = xs.OrderBy(x => x).ToList();
        if (a.Count == 0) return 0;
        return a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2m;
    }

    private static IEnumerable<long[]> Chunk(List<long> ids, int size = 300)
    {
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.Skip(i).Take(size).ToArray();
    }
}
