using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 市场权重 w 的下注级回放（2026-11-11 加）：按玩法分别 sweep w，报「总投入 / 总返还 / ROI / 命中率」，
/// 并且**按时间切分成调参段与验证段**，避免在同一批场次上挑出看起来最好的常数。
///
/// 为什么不能用 Brier 定 w：Brier 只说「概率准不准」，说不出「按这个 w 买下去赚不赚」。
/// w→1 时 EV 恒等于返奖率，Brier 最优恰恰是引擎一注都发不出来的时候。
///
/// 口径（必须写清楚，否则结果会被误读）：
/// · 只读库，不写任何业务表。
/// · 只用**开赛前最后一个快照**（CapturedAt &lt;= MatchTime），滚球价一律不进；
/// · 占位赔率（≤1.01）整场丢弃——1.00 会被去水算成「100%」，这条过滤是拿真实事故换来的；
/// · 快照必须**整玩法齐全**（半全场 9 / 总进球 8），与 <c>SnapshotComplete</c> 同口径；
/// · 去水用**官方固定水位** <c>StandardOverRound</c>，不用本场 Σ1/赔率（盘口没开全时会失真）；
/// · 每场每玩法**最多 1 注**，取「过闸的注里 EV 最高」的那一注。这条规则不是复刻大奶榜——
///   大奶榜先按档位（反转档优先）再按概率，那份档位逻辑依赖十几个人工标记，
///   回测台重建不出来。所以这里比的是**同一个选注规则下不同 w 的相对表现**，
///   不是「当晚榜面会有几注」。
/// · 实证闸 <c>PassesBaselineEv</c> 在本次回放里**不参与**：它读的是联赛静态基线表，
///   与 w 完全无关，去掉它不改变 w 之间的相对结论，只改变绝对注数。
///
/// 用法：
///   dotnet run --project src/AiFootBall.Backtest -c Release -- weight-replay
///   dotnet run --project src/AiFootBall.Backtest -c Release -- weight-replay 0.7
/// 参数为调参段占比，默认 0.6（前 60% 场次调参、后 40% 验证）。
/// </summary>
public static class WeightReplayStudy
{
    sealed class MRow
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
    }

    sealed class PRow
    {
        public long MatchId { get; set; }
        public string OptionCode { get; set; } = "";
        public decimal Odds { get; set; }
    }

    static readonly string[] BqCodes = { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" };
    static readonly string[] JqCodes = { "0", "1", "2", "3", "4", "5", "6", "7" };

    static readonly decimal[] Weights = { 0.3m, 0.5m, 0.7m };

    static int Side(char c) => c == '3' ? 0 : c == '1' ? 1 : 2;

    public static async Task<int> RunAsync(string connStr, double tuneShare = 0.6)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        Console.WriteLine($"# 市场权重 w 的下注级回放  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"调参段 = 前 {tuneShare:P0} 场次，验证段 = 其余。每场每玩法最多 1 注（过闸中 EV 最高）。");
        Console.WriteLine();

        var (bq, bqCut) = await LoadAsync(conn, "BQ", BqCodes);
        var (jq, jqCut) = await LoadAsync(conn, "JQ", JqCodes);

        if (bq.Count == 0 && jq.Count == 0)
        {
            Console.WriteLine("两个玩法都没有可用场次（快照不全或全是占位赔率）。");
            return 1;
        }

        var cutoff = new[] { bqCut, jqCut }.Where(x => x is not null).Min()!.Value;
        var trainSamples = (await conn.QueryAsync<ScoreSample>(
            @"SELECT LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam, MatchTime,
                     HomeScore, AwayScore
              FROM dbo.FbMatch
              WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                AND MatchTime < @cutoff
                AND MatchTime >= DATEADD(month, -18, @cutoff)",
            new { cutoff })).ToList();

        var model = ScorelineModel.Fit(trainSamples);
        Console.WriteLine($"训练样本 **{trainSamples.Count}** 场（< {cutoff:yyyy-MM-dd}），模型就绪：{(model.Ready ? "是" : "**否**")}");
        Console.WriteLine();
        if (!model.Ready)
        {
            Console.WriteLine("模型未就绪，回放没有意义。");
            return 1;
        }

        if (bq.Count > 0) await ReplayAsync(model, "BQ", "半全场", bq, tuneShare);
        if (jq.Count > 0) await ReplayAsync(model, "JQ", "总进球", jq, tuneShare);

        Console.WriteLine("## 结论怎么读");
        Console.WriteLine();
        Console.WriteLine("- ROI = 总返还 ÷ 总投入（平注 1 单位）。这个玩法的天花板就是返奖率，");
        Console.WriteLine("  比分 0.710 / 半全场 0.797 / 总进球 0.797，高于天花板说明样本小到不可信。");
        Console.WriteLine("- 命中率高不代表赚：专挑低赔同样会把 ROI 压到返奖率以下，所以两列必须一起看。");
        Console.WriteLine("- **只有验证段的排序与调参段一致，那个 w 才算站得住**；两段打架就是过拟合。");
        Console.WriteLine("- 每场 1 注 × 几百场，同档命中个位数是常态，别把 2~3 注的差当成结论。");
        Console.WriteLine("- 比分（BF）不在这里：`EnableScorePicks = false`，当前不发注，回放 ROI 没有落点。");
        return 0;
    }

    /// <summary>取某玩法「开赛前最后一个快照」齐全的场次，返回 (场次, 该玩法最早开赛时刻)。</summary>
    static async Task<(List<(MRow M, decimal[] Odds)> Rows, DateTime? Cutoff)> LoadAsync(
        System.Data.Common.DbConnection conn, string playType, string[] codes)
    {
        const string lastSnap = @"
WITH last AS (
  SELECT h.MatchId, MAX(h.CapturedAt) AS ca
  FROM dbo.FbJcOddsHistory h
  JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
  WHERE h.PlayType = @play AND h.CapturedAt <= m.MatchTime
  GROUP BY h.MatchId)";

        var matches = (await conn.QueryAsync<MRow>(lastSnap + @"
SELECT m.MatchId, m.LeagueName, m.HomeTeamName AS HomeTeam, m.AwayTeamName AS AwayTeam, m.MatchTime,
       m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore
FROM last l
JOIN dbo.FbMatch m ON m.MatchId = l.MatchId
WHERE m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
  AND m.MatchTime >= CONVERT(date, '2026-10-01')
ORDER BY m.MatchTime", new { play = playType })).ToList();

        var prices = (await conn.QueryAsync<PRow>(lastSnap + @"
SELECT h.MatchId, h.OptionCode, h.Odds
FROM dbo.FbJcOddsHistory h
JOIN last l ON l.MatchId = h.MatchId AND l.ca = h.CapturedAt
WHERE h.PlayType = @play", new { play = playType })).ToList();

        var byMatch = prices.GroupBy(p => p.MatchId).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<(MRow, decimal[])>();
        foreach (var m in matches)
        {
            if (!byMatch.TryGetValue(m.MatchId, out var ps)) continue;
            var arr = new decimal[codes.Length];
            var ok = true;
            for (var i = 0; i < codes.Length; i++)
            {
                var hit = ps.FirstOrDefault(p => p.OptionCode == codes[i]);
                // 占位赔率必须整场丢：1.00 会在去水时被算成 100%，把整场的隐含概率压垮。
                if (hit is null || hit.Odds <= 1.01m || hit.Odds > 500m) { ok = false; break; }
                arr[i] = hit.Odds;
            }
            // 半全场必须能还原半场；没有真半场的场次不进半全场回放（否则实际档位是编的）。
            if (playType == "BQ" && (m.HalfHomeScore is null || m.HalfAwayScore is null)) ok = false;
            if (ok) rows.Add((m, arr));
        }

        return (rows, rows.Count == 0 ? null : rows.Min(r => r.Item1.MatchTime));
    }

    static async Task ReplayAsync(ScorelineModel model, string playType, string label,
        List<(MRow M, decimal[] Odds)> rows, double tuneShare)
    {
        var codes = playType == "BQ" ? BqCodes : JqCodes;
        var overRound = MilkPickEngine.StandardOverRound(playType);
        var payback = MilkPickEngine.Payout(playType);

        // 逐场预算纯模型概率与市场隐含，避免在 w 循环里重复算分布。
        var prepared = new List<(DateTime T, decimal[] PurePct, decimal[] MarketFrac, decimal[] Odds, int Actual)>();
        var noModel = 0;
        foreach (var (m, odds) in rows)
        {
            var pure = new decimal[codes.Length];
            if (playType == "BQ")
            {
                var joint = MilkPickEngine.HalfFullJoint(model, m.LeagueName, m.HomeTeam, m.AwayTeam, null);
                if (joint is null) { noModel++; continue; }
                for (var i = 0; i < codes.Length; i++)
                    pure[i] = joint[Side(codes[i][0]), Side(codes[i][2])] * 100m;

                // 联合分布若因截断略偏离 1，归一到 100，否则 EV 会被系统性压低。
                var sum = pure.Sum();
                if (sum <= 0m) { noModel++; continue; }
                for (var i = 0; i < codes.Length; i++) pure[i] = pure[i] / sum * 100m;

                var hh = m.HalfHomeScore!.Value;
                var ha = m.HalfAwayScore!.Value;
                var ht = hh > ha ? '3' : hh == ha ? '1' : '0';
                var ft = m.HomeScore > m.AwayScore ? '3' : m.HomeScore == m.AwayScore ? '1' : '0';
                var code = $"{ht}-{ft}";
                prepared.Add((m.MatchTime, pure,
                    odds.Select(o => 1m / o / overRound).ToArray(),
                    odds, Array.IndexOf(codes, code)));
            }
            else
            {
                var grid = model.Distribution(m.LeagueName, m.HomeTeam, m.AwayTeam, null);
                if (grid.Count == 0) { noModel++; continue; }
                var byGoals = new decimal[8];
                foreach (var (h, a, p) in grid)
                {
                    var t = Math.Min(h + a, 7);
                    byGoals[t] += (decimal)p * 100m;
                }
                for (var i = 0; i < 8; i++) pure[i] = byGoals[i];
                var sum = pure.Sum();
                if (sum <= 0m) { noModel++; continue; }
                for (var i = 0; i < 8; i++) pure[i] = pure[i] / sum * 100m;

                var goals = m.HomeScore + m.AwayScore;
                var mkt = new decimal[codes.Length];
                for (var i = 0; i < codes.Length; i++) mkt[i] = 1m / odds[i] / overRound;
                prepared.Add((m.MatchTime, pure, mkt, odds, Math.Min(goals, 7)));
            }
        }

        if (prepared.Count == 0)
        {
            Console.WriteLine($"## {label}（{playType}）：一场都算不出来，跳过。");
            Console.WriteLine();
            return;
        }

        prepared.Sort((x, y) => x.T.CompareTo(y.T));
        var split = (int)Math.Round(prepared.Count * tuneShare);
        if (split < 1) split = 1;
        if (split >= prepared.Count) split = prepared.Count - 1;

        Console.WriteLine($"## {label}（{playType}）可用 **{prepared.Count}** 场" +
                          $"（模型算不出 λ 的 {noModel} 场已剔除）；" +
                          $"调参段 {split} 场 / 验证段 {prepared.Count - split} 场");
        Console.WriteLine();

        var segments = new (string Name, int From, int To)[]
        {
            ("调参段", 0, split),
            ("验证段", split, prepared.Count),
        };

        // 两条选注规则都跑：单用一条会被规则自身的偏好污染——「EV 最高」在 w 上升时
        // 会自动偏向更长赔率（EV 对赔率单调），于是「变了 w」和「换了选注偏好」两件事
        // 混在一起，分不清是权重起作用还是规则在漂。两条规则同向，结论才算稳。
        var rules = new (string Name, bool ByEv)[]
        {
            ("规则A：混合概率最高（贴近生产的 perMatch 排序）", false),
            ("规则B：EV 最高", true),
        };

        foreach (var (ruleName, byEv) in rules)
        {
            Console.WriteLine($"#### {ruleName}");
            Console.WriteLine();
            foreach (var (name, from, to) in segments)
            {
                Console.WriteLine($"**{name}（{to - from} 场）**");
                Console.WriteLine();
                Console.WriteLine("| w（市场占比） | 注数 | 投入 | 返还 | ROI | 命中率 | 平均赔率 |");
                Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");
                foreach (var w in Weights)
                {
                    var (bets, ret, hits, oddsSum) = Simulate(prepared, from, to, codes, playType, w, byEv);
                    var roi = bets == 0 ? 0m : ret / bets;
                    Console.WriteLine($"| {w:0.0} | {bets} | {bets} | {ret:F2} | {roi:F3} | " +
                                      $"{(bets == 0 ? 0 : hits * 100.0 / bets):F1}% | " +
                                      $"{(bets == 0 ? 0m : oddsSum / bets):F2} |");
                }
                Console.WriteLine();
            }
        }
        Console.WriteLine($"> 该玩法返奖率（ROI 天花板）：**{payback:F3}**。");
        Console.WriteLine();

        // 两段同向才算站得住
        Console.WriteLine($"**{label}判定**：");
        foreach (var (ruleName, byEv) in rules)
        {
            var tuneBest = BestW(prepared, 0, split, codes, playType, byEv);
            var verifyBest = BestW(prepared, split, prepared.Count, codes, playType, byEv);
            Console.WriteLine(tuneBest == verifyBest
                ? $"- {ruleName}：调参段 w={tuneBest:0.0}，验证段 w={verifyBest:0.0} —— **一致**。"
                : $"- {ruleName}：调参段 w={tuneBest:0.0}，验证段 w={verifyBest:0.0} —— **不一致**（小样本挑出来的）。");
        }
        Console.WriteLine();
    }

    /// <summary>在给定区间与权重下跑一遍：每场最多 1 注，返回 (注数, 返还, 命中, 赔率合计)。</summary>
    static (int Bets, decimal Ret, int Hits, decimal OddsSum) Simulate(
        List<(DateTime T, decimal[] PurePct, decimal[] MarketFrac, decimal[] Odds, int Actual)> prepared,
        int from, int to, string[] codes, string playType, decimal w, bool byEv)
    {
        int bets = 0, hits = 0;
        decimal ret = 0m, oddsSum = 0m;
        for (var i = from; i < to; i++)
        {
            var (_, pure, market, odds, actual) = prepared[i];
            var bestScore = decimal.MinValue;
            var bestIdx = -1;
            for (var k = 0; k < codes.Length; k++)
            {
                var blendedPct = (1m - w) * pure[k] + w * market[k] * 100m;
                var (safety, value) = MilkPickEngine.JudgeGates(pure[k], blendedPct, odds[k], playType, codes[k]);
                if (!safety || !value) continue;
                var score = byEv ? blendedPct / 100m * odds[k] : blendedPct;
                if (score <= bestScore) continue;
                bestScore = score;
                bestIdx = k;
            }
            if (bestIdx < 0) continue;
            bets++;
            oddsSum += odds[bestIdx];
            if (bestIdx == actual) { hits++; ret += odds[bestIdx]; }
        }
        return (bets, ret, hits, oddsSum);
    }

    static decimal BestW(
        List<(DateTime T, decimal[] PurePct, decimal[] MarketFrac, decimal[] Odds, int Actual)> prepared,
        int from, int to, string[] codes, string playType, bool byEv)
    {
        var best = 0.3m;
        var bestRoi = decimal.MinValue;
        foreach (var w in Weights)
        {
            var (bets, ret, _, _) = Simulate(prepared, from, to, codes, playType, w, byEv);
            // 注数太少不参与比较：十注以内的 ROI 没有信息量，别让它抢走「最优」的名额。
            if (bets < 30) continue;
            var roi = ret / bets;
            if (roi > bestRoi) { bestRoi = roi; best = w; }
        }
        return best;
    }
}
