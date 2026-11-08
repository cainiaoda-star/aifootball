using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 单场 λ 与「模型是否认识这支球队」的逐项打印（2026-11-08 加）。
///
/// 起因：11-08 周日007 埃尔切 vs 贝蒂斯，模型给主胜 37.13%、半全场「胜胜」19.69%，
/// 市场只给主胜 23.7%。反推 λ 得 1.250 / 1.220 —— 两支积分 10 分 vs 23 分的球队被算成均势，
/// 这不像是「估计不准」，更像是「其中一队根本没被认出来，退回了联赛平均」。
///
/// 这个工具不改任何东西，只把中间量摊开：认不认得、多少个样本、λ 是多少。
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- team-check 埃尔切 贝蒂斯 西甲
/// </summary>
public static class TeamLambdaStudy
{
    public static async Task<int> RunAsync(string connStr, string home, string away, string league)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        var samples = (await conn.QueryAsync<ScoreSample>(
            @"SELECT f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
                     f.HomeScore, f.AwayScore
              FROM dbo.FbMatch f
              WHERE f.Status = 3
                AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                AND f.MatchTime >= DATEADD(month, -18, GETDATE())")).ToList();

        var model = ScorelineModel.Fit(samples);

        Console.WriteLine($"# 单场 λ 诊断：{home} vs {away}（{league}）");
        Console.WriteLine();
        Console.WriteLine($"训练样本合计 {samples.Count} 场");

        // ── 1. 认不认得 ─────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("## 1. 队名解析");
        Console.WriteLine();
        Console.WriteLine("| 查询名 | Resolve 结果 | 本联赛样本 | 全库样本 | 认识? |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var name in new[] { home, away })
        {
            var resolved = model.Resolve(league, name) ?? "（认不出）";
            var inLeague = model.TeamGames(league, name);
            var anyLeague = samples.Count(s =>
                TeamNames.Same(s.HomeTeam, name) || TeamNames.Same(s.AwayTeam, name)
                || TeamNames.Same(s.HomeTeam, resolved) || TeamNames.Same(s.AwayTeam, resolved));
            Console.WriteLine($"| {name} | {resolved} | {inLeague} | {anyLeague} | {(inLeague > 0 ? "是" : "**否**")} |");
        }

        // ── 2. λ ───────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("## 2. 模型 λ");
        Console.WriteLine();
        var eg = model.ExpectedGoals(league, home, away, null);
        if (eg is null)
        {
            Console.WriteLine("模型未就绪，拿不到 λ。");
            return 0;
        }
        var (lh, la) = eg.Value;
        Console.WriteLine($"λ主 {lh:0.000} / λ客 {la:0.000}，合计 {(lh + la):0.000}");
        Console.WriteLine();

        // 三向
        double ph = 0, pd = 0, pa = 0;
        for (var i = 0; i <= 10; i++)
        for (var j = 0; j <= 10; j++)
        {
            var p = Pois(i, lh) * Pois(j, la);
            if (i > j) ph += p;
            else if (i == j) pd += p;
            else pa += p;
        }
        Console.WriteLine($"三向：主 {ph * 100:0.00}% / 平 {pd * 100:0.00}% / 客 {pa * 100:0.00}%");

        // ── 3. 逐个联赛的平均，用来判断 λ 是不是退回了均值 ──────────
        Console.WriteLine();
        Console.WriteLine("## 3. 该联赛的场均（模型内部口径，18 个月、含 30 场先验收缩）");
        Console.WriteLine();
        var lg = samples.Where(s => TeamNames.Same(s.LeagueName, league)).ToList();
        if (lg.Count > 0)
        {
            var hAvg = lg.Average(s => (double)s.HomeScore);
            var aAvg = lg.Average(s => (double)s.AwayScore);
            Console.WriteLine($"| 项 | 值 |");
            Console.WriteLine($"|---|---|");
            Console.WriteLine($"| 样本 | {lg.Count} 场 |");
            Console.WriteLine($"| 主场场均 | {hAvg:0.000} |");
            Console.WriteLine($"| 客场场均 | {aAvg:0.000} |");
            Console.WriteLine($"| 合计 | {(hAvg + aAvg):0.000} |");
            Console.WriteLine();
            Console.WriteLine($"若两队都被认成「未知」（倍率 1.0），λ 就该正好等于 {hAvg:0.000} / {aAvg:0.000}。");
            Console.WriteLine($"实际 {lh:0.000} / {la:0.000} — "
                + (Math.Abs(lh - hAvg) < 0.02 && Math.Abs(la - aAvg) < 0.02
                    ? "**完全吻合：两队都没被认出来，λ 退回了联赛均值。**"
                    : "有偏离，说明至少一队的攻防倍率被用上了。"));
        }

        // ── 4. 真实进失球 ─────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("## 4. 两队的真实进失球（18 个月、本联赛）");
        Console.WriteLine();
        Console.WriteLine("| 队 | 场次 | 场均进 | 场均失 | 胜平负 | 积分 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var name in new[] { home, away })
        {
            var games = samples.Where(s =>
                TeamNames.Same(s.LeagueName, league)
                && (TeamNames.Same(s.HomeTeam, name) || TeamNames.Same(s.AwayTeam, name))).ToList();
            if (games.Count == 0) { Console.WriteLine($"| {name} | 0 | — | — | — | — |"); continue; }
            var gf = games.Average(s => TeamNames.Same(s.HomeTeam, name) ? s.HomeScore : s.AwayScore);
            var ga = games.Average(s => TeamNames.Same(s.HomeTeam, name) ? s.AwayScore : s.HomeScore);
            var w = games.Count(s => TeamNames.Same(s.HomeTeam, name) ? s.HomeScore > s.AwayScore : s.AwayScore > s.HomeScore);
            var d = games.Count(s => s.HomeScore == s.AwayScore);
            var l = games.Count - w - d;
            Console.WriteLine($"| {name} | {games.Count} | {gf:0.00} | {ga:0.00} | {w}胜{d}平{l}负 | {w * 3 + d} |");
        }

        return 0;
    }

    private static double Pois(int k, double l)
    {
        if (l < 1e-9) return k == 0 ? 1 : 0;
        var p = Math.Exp(-l);
        for (var i = 1; i <= k; i++) p *= l / i;
        return p;
    }
}
