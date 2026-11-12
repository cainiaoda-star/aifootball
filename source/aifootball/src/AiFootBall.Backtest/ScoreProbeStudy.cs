using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 比分（BF）的**只读**评估（2026-11-12 加，第三方校验方案的 P1-8）。
///
/// 要回答的问题只有一个：模型的 Top-1 / Top-3 比分猜得准不准，能不能达到方案里
/// 「Hit@1 落在 8%~15% 算可用」这条线。
///
/// 为什么现在必须补：`EnableScorePicks=false` 期间比分不出票，于是「比分对不对」
/// 这件事在项目里从来没有被量过。库里只有历史频率（1:0 9.81% / 2:1 8.13%），
/// 那是「这个比分在足球里常见」，不是「我们的模型能挑中它」。两者差着整整一层信息。
///
/// 口径（写清楚，否则数字会被误读）：
/// · **前向切分**：训练用截止日之前，测试用之后；`Fit(..., asOf: cutoff)` 内部也会再过滤一次
///   （2026-11-12 加的防线，见 ScorelineModel.Fit）。
/// · 只评「两队训练样本都 ≥ 5 场」的场次——和 weight-probe 同一批，否则模型退联赛均值，
///   算出来的命中率是「按联赛平均瞎猜」的成绩，不是模型的。
/// · 三个对照：模型 / 训练段历史频率（固定押最常见的那几个比分）/ 市场（竞彩比分赔率隐含）。
///   市场那组样本极少（`FbJcOddsHistory` 全库只有 634 场），只当参考，别拿来下结论。
/// · 排序只排**具体比分**（0:0 ~ 6:6）。「胜其它」这类尾档不参与——它没法判中，
///   混进来会把 Hit@1 抬高一个档。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- score-probe [训练占比]
/// 只读库，不改任何业务表，也不改变线上是否出票。
/// </summary>
public static class ScoreProbeStudy
{
    sealed class SRow
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    sealed class MRow
    {
        public long MatchId { get; set; }
        public string OptionCode { get; set; } = "";
        public decimal Odds { get; set; }
    }

    public static async Task<int> RunAsync(string connStr, double trainRatio = 0.67)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        var samples = (await conn.QueryAsync<SRow>(
            @"SELECT MatchId, LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam, MatchTime,
                     HomeScore, AwayScore
              FROM dbo.FbMatch
              WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                AND MatchTime >= DATEADD(month, -18, GETDATE())")).ToList();

        if (samples.Count < 500)
        {
            Console.WriteLine($"样本不足（完赛 {samples.Count}），跑不了。");
            return 1;
        }

        var times = samples.Select(s => s.MatchTime).OrderBy(t => t).ToList();
        var cutIndex = Math.Max(1, Math.Min(times.Count - 1, (int)(times.Count * trainRatio)));
        var cutoff = times[cutIndex];

        var trainSamples = samples.Where(s => s.MatchTime < cutoff).ToList();
        var testAll = samples.Where(s => s.MatchTime >= cutoff).ToList();

        // 两边都 ≥5 场才算「模型有观点」，与 weight-probe 完全同口径。
        var games = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in trainSamples)
        {
            var h = TeamAliases.Canonical(s.HomeTeam);
            var a = TeamAliases.Canonical(s.AwayTeam);
            games[h] = games.GetValueOrDefault(h) + 1;
            games[a] = games.GetValueOrDefault(a) + 1;
        }
        int Known(string n) => games.GetValueOrDefault(TeamAliases.Canonical(n));

        var test = testAll.Where(s => Math.Min(Known(s.HomeTeam), Known(s.AwayTeam)) >= 5).ToList();
        if (test.Count < 200)
        {
            Console.WriteLine($"测试场次不足（{test.Count}），跑不了。");
            return 1;
        }

        var model = ScorelineModel.Fit(
            trainSamples.Select(s => new ScoreSample
            {
                LeagueName = s.LeagueName,
                HomeTeam = s.HomeTeam,
                AwayTeam = s.AwayTeam,
                HomeScore = s.HomeScore,
                AwayScore = s.AwayScore,
                MatchTime = s.MatchTime
            }).ToList(),
            asOf: cutoff);
        if (!model.Ready)
        {
            Console.WriteLine("模型未就绪，跑不了。");
            return 1;
        }

        Console.WriteLine($"# 比分（BF）只读评估  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 18 个月完赛样本 | {samples.Count} |");
        Console.WriteLine($"| 切分时刻 | {cutoff:yyyy-MM-dd HH:mm} |");
        Console.WriteLine($"| 训练样本 | {trainSamples.Count} |");
        Console.WriteLine($"| 训练窗口（模型自报） | {model.FitFrom:yyyy-MM-dd} .. {model.FitTo:yyyy-MM-dd}（n={model.FitGames}） |");
        Console.WriteLine($"| 测试场次（两队各 ≥5 场） | {test.Count} |");
        Console.WriteLine($"| 模型版本 | `{model.VersionTag}` |");
        Console.WriteLine();

        // ---- 训练段历史频率（固定押最常见的几个比分）----
        var freq = trainSamples
            .GroupBy(s => $"{s.HomeScore}:{s.AwayScore}")
            .OrderByDescending(g => g.Count())
            .Take(6)
            .Select(g => (Code: g.Key, Pct: g.Count() * 100.0 / trainSamples.Count))
            .ToList();
        var histTop1 = freq[0].Code;
        var histTop3 = freq.Take(3).Select(x => x.Code).ToHashSet(StringComparer.Ordinal);

        // ---- 市场隐含（仅完整 9 档以上快照的场次）----
        var oddsRows = (await conn.QueryAsync<MRow>(
            @"SELECT h.MatchId, h.OptionCode, h.Odds
              FROM dbo.FbJcOddsHistory h
              JOIN (SELECT MatchId, MAX(CapturedAt) AS mx FROM dbo.FbJcOddsHistory
                    WHERE PlayType = 'BF' GROUP BY MatchId) t
                ON t.MatchId = h.MatchId AND t.mx = h.CapturedAt
              WHERE h.PlayType = 'BF' AND h.Odds > 1.01")).ToList();
        var marketByMatch = oddsRows
            .GroupBy(r => r.MatchId)
            .ToDictionary(
                g => g.Key,
                g => g.Where(x => x.OptionCode.Contains(':')).ToList());

        // ---- 逐场排名 ----
        var rows = new List<(double Top1Pct, bool Hit1, bool Hit3, bool MktHit1, bool MktHit3, int MktRank)>();
        var scoreHit1 = 0;
        var scoreHit3 = 0;
        var freqHit1 = 0;
        var freqHit3 = 0;
        var mktN = 0;
        var mktHit1 = 0;
        var mktHit3 = 0;

        foreach (var s in test)
        {
            var lg = model.ResolveLeague(s.LeagueName, s.HomeTeam, s.AwayTeam, 5);
            var grid = model.Distribution(lg, s.HomeTeam, s.AwayTeam, null);
            if (grid.Count == 0) continue;
            var mass = grid.Sum(c => c.P);
            if (mass <= 0) continue;

            var ranked = grid
                .Select(c => (Code: $"{c.Home}:{c.Away}", P: c.P / mass))
                .OrderByDescending(x => x.P)
                .ToList();
            var actual = $"{s.HomeScore}:{s.AwayScore}";
            var top1 = ranked[0];
            var hit1 = top1.Code == actual;
            var hit3 = ranked.Take(3).Any(x => x.Code == actual);

            if (hit1) scoreHit1++;
            if (hit3) scoreHit3++;
            if (actual == histTop1) freqHit1++;
            if (histTop3.Contains(actual)) freqHit3++;

            var mktHit1Flag = false;
            var mktHit3Flag = false;
            var mktRank = 0;
            if (marketByMatch.TryGetValue(s.MatchId, out var mkt) && mkt.Count >= 28)
            {
                var mktRanked = mkt
                    .Select(x => (Code: x.OptionCode, Implied: 1.0 / (double)x.Odds))
                    .OrderByDescending(x => x.Implied)
                    .ToList();
                mktN++;
                mktRank = mktRanked.FindIndex(x => x.Code == actual) + 1;
                mktHit1Flag = mktRank == 1;
                mktHit3Flag = mktRank is >= 1 and <= 3;
                if (mktHit1Flag) mktHit1++;
                if (mktHit3Flag) mktHit3++;
            }

            rows.Add((top1.P * 100, hit1, hit3, mktHit1Flag, mktHit3Flag, mktRank));
        }

        var n = rows.Count;
        Console.WriteLine("## 一、命中率对照（同一批测试场次）");
        Console.WriteLine();
        Console.WriteLine("| 口径 | Hit@1（首选即中） | Hit@3（前三含实际比分） |");
        Console.WriteLine("|---|---:|---:|");
        Console.WriteLine($"| **模型 Top-1/Top-3** | **{scoreHit1 * 100.0 / n:F2}%**（{scoreHit1}/{n}） | **{scoreHit3 * 100.0 / n:F2}%**（{scoreHit3}/{n}） |");
        Console.WriteLine($"| 历史频率（固定押 {histTop1}） | {freqHit1 * 100.0 / n:F2}%（{freqHit1}/{n}） | {freqHit3 * 100.0 / n:F2}%（{freqHit3}/{n}） |");
        if (mktN > 0)
            Console.WriteLine($"| 市场隐含（竞彩比分赔率，n={mktN}） | {mktHit1 * 100.0 / mktN:F2}%（{mktHit1}/{mktN}） | {mktHit3 * 100.0 / mktN:F2}%（{mktHit3}/{mktN}） |");
        else
            Console.WriteLine("| 市场隐含 | —（无完整比分快照场次） | — |");
        Console.WriteLine();
        Console.WriteLine("训练段最常见的六个比分（历史频率对照的基准）：");
        Console.WriteLine();
        Console.WriteLine("| 比分 | 训练段占比 |");
        Console.WriteLine("|---|---:|");
        foreach (var (code, pct) in freq) Console.WriteLine($"| {code} | {pct:F2}% |");
        Console.WriteLine();

        // ---- 概率分档校准：模型给这一档多少，实际就中多少 ----
        Console.WriteLine("## 二、模型首选档的概率 vs 实际命中（校准）");
        Console.WriteLine();
        Console.WriteLine("| 首选概率区间 | 场次 | 实际命中率 | 平均预测概率 | 差（实际−预测） |");
        Console.WriteLine("|---|---:|---:|---:|---:|");
        var bands = new (double Lo, double Hi, string Label)[]
        {
            (0, 8, "< 8%"), (8, 9, "8~9%"), (9, 10, "9~10%"), (10, 11, "10~11%"),
            (11, 13, "11~13%"), (13, 100, "≥ 13%")
        };
        foreach (var (lo, hi, label) in bands)
        {
            var g = rows.Where(x => x.Top1Pct >= lo && x.Top1Pct < hi).ToList();
            if (g.Count == 0) { Console.WriteLine($"| {label} | 0 | — | — | — |"); continue; }
            var hit = g.Count(x => x.Hit1) * 100.0 / g.Count;
            var pred = g.Average(x => x.Top1Pct);
            Console.WriteLine($"| {label} | {g.Count} | {hit:F2}% | {pred:F2}% | {hit - pred:+0.00;-0.00} |");
        }
        Console.WriteLine();

        // ---- 判定 ----
        var hit1Pct = scoreHit1 * 100.0 / n;
        Console.WriteLine("## 三、判定（按校验方案「Hit@1 在 8%~15% 算可用」）");
        Console.WriteLine();
        Console.WriteLine($"- 模型 Hit@1 = **{hit1Pct:F2}%**，"
            + (hit1Pct is >= 8 and <= 15
                ? "落在 8%~15% 区间内，**可用**。"
                : hit1Pct > 15
                    ? "高于 15%——先怀疑口径（是否有泄漏/是否只评了容易的场次），不要直接当成好消息。"
                    : "低于 8%，**不可用**：比「固定押最常见比分」还不如，不该恢复出票。"));
        Console.WriteLine($"- 与历史频率 {(freqHit1 * 100.0 / n):F2}% 相比：{(hit1Pct - freqHit1 * 100.0 / n):+0.00;-0.00} 点"
            + (hit1Pct > freqHit1 * 100.0 / n ? "（模型有增量）" : "（模型没有增量，多算的那些等于白算）"));
        Console.WriteLine($"- Hit@3 = {scoreHit3 * 100.0 / n:F2}%：三选覆盖率的上限，"
            + "它只说明「候选区够不够宽」，不说明能不能盈利——比分单注返奖率 0.710 是硬天花板。");
        if (mktN > 0 && mktN < 100)
            Console.WriteLine($"- ⚠ 市场对照只有 {mktN} 场，**不足以判断谁更准**，只作参考。");
        Console.WriteLine();
        Console.WriteLine("> 边界：这里评的是「模型排序准不准」，没有乘赔率、没有算 ROI。");
        Console.WriteLine("> 比分 31 档、返奖率 0.710，排序准不等于能赚——真要出票还得过价值闸。");
        return 0;
    }
}
