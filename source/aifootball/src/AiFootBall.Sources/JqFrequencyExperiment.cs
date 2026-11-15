using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 总进球价值判定的实验路径。不进入 <see cref="MilkPickEngine.BuildDetailed"/>，
/// 也不进入保存、逐场跟进或接口出榜。
///
/// 现行基线 <see cref="OptionBaseline"/> 的总进球频率是一张写死的全库表，
/// 不分联赛、不分盘口、也不随决策时刻截断。本实验只用决策时刻之前已经能知道赛果的比赛，
/// 按联赛和主胜赔率档估计这一档的频率，再套用与现行相同的实证期望上下限。
/// 样本不够时不给概率、不给建议，也不退回那张全库表。
///
/// 本轮不动赔率上下限、名额、排序、半全场公式和市场权重。
/// </summary>
public static class JqFrequencyExperiment
{
    /// <summary>与联赛场均的既有下限相同（不够 12 场不单独估计）。本轮不另搜这个数。</summary>
    public const int MinMatches = 12;

    public const string ReasonOk = "ok";
    public const string ReasonBelowFloor = "below-floor";
    public const string ReasonAboveCeiling = "above-ceiling";
    public const string ReasonInsufficient = "insufficient-sample";
    public const string ReasonMissingLeague = "missing-league";
    public const string ReasonMissingOdds = "missing-odds";
    public const string ReasonBadGoals = "bad-goals";

    /// <summary>一场已经结束、并且带有决策前主胜赔率的历史比赛。</summary>
    public readonly record struct PastMatch(
        DateTime Kickoff,
        DateTime? ResultKnownAt,
        string? League,
        decimal? HomeWinOdds,
        bool HomeWinOddsAreEuro,
        int HomeGoals,
        int AwayGoals);

    /// <summary>
    /// 实验结论。样本不足或条件缺失时 <see cref="ProbabilityPct"/> 为 null，
    /// <see cref="Recommend"/> 为 false。有概率但没过现行期望带时，概率仍保留，建议为 false。
    /// </summary>
    public readonly record struct Decision(
        bool Recommend,
        decimal? ProbabilityPct,
        int Sample,
        decimal? BaselineEv,
        string Reason);

    /// <summary>赛果最早可以拿来用的时刻。没写已知时刻时，用开赛加既有的 150 分钟。</summary>
    public static DateTime AvailableAt(PastMatch match)
        => match.ResultKnownAt ?? match.Kickoff.AddMinutes(ScorelineModel.MatchDurationMinutes);

    /// <summary>8 个总进球档的百分数，合计 100。样本不足时返回 null。</summary>
    public static decimal[]? Distribution(
        IReadOnlyList<PastMatch> history, DateTime asOf, string? league, decimal? homeWinOdds, bool homeWinOddsAreEuro)
    {
        var rows = Select(history, asOf, league, homeWinOdds, homeWinOddsAreEuro);
        if (rows.Count < MinMatches) return null;
        var counts = new int[8];
        foreach (var row in rows)
            counts[Bucket(row.HomeGoals, row.AwayGoals)]++;
        var pct = new decimal[8];
        decimal used = 0m;
        for (var i = 0; i < 7; i++)
        {
            pct[i] = decimal.Round(100m * counts[i] / rows.Count, 2, MidpointRounding.AwayFromZero);
            used += pct[i];
        }
        pct[7] = 100m - used;
        return pct;
    }

    /// <summary>
    /// 用条件频率做价值判定。期望的上下限与现行总进球实证闸相同，变的只有概率从哪来。
    /// </summary>
    public static Decision Judge(
        IReadOnlyList<PastMatch> history,
        DateTime asOf,
        string? league,
        decimal? homeWinOdds,
        bool homeWinOddsAreEuro,
        int goals,
        decimal optionOdds)
    {
        if (goals < 0) return new Decision(false, null, 0, null, ReasonBadGoals);
        if (string.IsNullOrWhiteSpace(league)) return new Decision(false, null, 0, null, ReasonMissingLeague);
        if (homeWinOdds is not > 0m) return new Decision(false, null, 0, null, ReasonMissingOdds);

        var sample = Select(history, asOf, league, homeWinOdds, homeWinOddsAreEuro).Count;
        var dist = Distribution(history, asOf, league, homeWinOdds, homeWinOddsAreEuro);
        if (dist is null || sample < MinMatches)
            return new Decision(false, null, sample, null, ReasonInsufficient);

        var bucket = goals >= 7 ? 7 : goals;
        var pct = dist[bucket];
        var ev = decimal.Round(pct / 100m * optionOdds, 3, MidpointRounding.AwayFromZero);
        var payback = MilkPickEngine.Payout(JcPlayTypes.Jq);
        if (ev > payback * MilkPickEngine.MaxBaselineEdgeRatio)
            return new Decision(false, pct, sample, ev, ReasonAboveCeiling);
        if (ev < payback * MilkPickEngine.MinBaselineEvRatio)
            return new Decision(false, pct, sample, ev, ReasonBelowFloor);
        return new Decision(true, pct, sample, ev, ReasonOk);
    }

    static List<PastMatch> Select(
        IReadOnlyList<PastMatch> history,
        DateTime asOf,
        string? league,
        decimal? homeWinOdds,
        bool homeWinOddsAreEuro)
    {
        var rows = new List<PastMatch>();
        if (string.IsNullOrWhiteSpace(league) || homeWinOdds is not > 0m) return rows;
        var name = LeagueNames.Resolve(league);
        var band = Band(homeWinOdds.Value, homeWinOddsAreEuro);
        foreach (var row in history)
        {
            if (AvailableAt(row) > asOf) continue;
            if (row.HomeWinOdds is not > 0m) continue;
            if (!string.Equals(LeagueNames.Resolve(row.League), name, StringComparison.OrdinalIgnoreCase)) continue;
            if (Band(row.HomeWinOdds.Value, row.HomeWinOddsAreEuro) != band) continue;
            if (row.HomeGoals < 0 || row.AwayGoals < 0) continue;
            rows.Add(row);
        }
        return rows;
    }

    static int Band(decimal homeWinOdds, bool alreadyEuro)
    {
        var euro = alreadyEuro ? homeWinOdds : homeWinOdds * OptionBaseline.JcToEuro;
        return OptionBaseline.BandOf(OptionBaseline.HomeOddsEdges, euro);
    }

    public static int Bucket(int homeGoals, int awayGoals)
    {
        var goals = homeGoals + awayGoals;
        return goals >= 7 ? 7 : goals;
    }
}
