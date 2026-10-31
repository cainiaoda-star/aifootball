using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 每日复盘仓储：聚合某日「有推荐记录且已完场」的全部场次，
/// 输出完整赛果字段（胜平负、让球胜平负、比分、总进球、半全场、比赛类型），
/// 并生成推荐思路总结（推荐依据 + 命中/未中原因）。
/// </summary>
public sealed class SummaryRepository
{
    private readonly SqlConnectionFactory _factory;

    public SummaryRepository(SqlConnectionFactory factory) => _factory = factory;

    public async Task<List<FinishedSummaryItem>> GetFinishedSummaryAsync(DateTime date, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // 当日推荐，同一场跨板块只留最新一条。没有 MatchId 的全包各算各的。
        var recs = (await conn.QueryAsync<RecommendationRecord>(new CommandDefinition(
            @"WITH cte AS (
                  SELECT *,
                         ROW_NUMBER() OVER (
                             PARTITION BY MatchId, CASE WHEN MatchId IS NULL THEN RecId ELSE 0 END
                             ORDER BY RecId DESC) AS rn
                  FROM dbo.Recommendation
                  WHERE RecDate = @d
              )
              SELECT * FROM cte WHERE rn = 1",
            new { d = date.Date }, cancellationToken: ct))).ToList();

        var matchIds = recs.Where(r => r.MatchId is not null).Select(r => r.MatchId!.Value).Distinct().ToArray();

        var finished = matchIds.Length == 0
            ? new Dictionary<long, FinishedMatch>()
            : (await conn.QueryAsync<FinishedMatch>(new CommandDefinition(
                @"SELECT MatchId, HomeTeamName, AwayTeamName, LeagueName, MatchTime,
                         HomeScore, AwayScore, HalfHomeScore, HalfAwayScore, Handicap
                  FROM dbo.FbMatch
                  WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                    AND MatchId IN @ids",
                new { ids = matchIds }, cancellationToken: ct))).ToDictionary(m => m.MatchId);

        var exchange = matchIds.Length == 0
            ? new List<ExchangeRatio>()
            : (await conn.QueryAsync<ExchangeRatio>(new CommandDefinition(
                @"SELECT MatchId, RatioHome, RatioAway, CapturedAt
                  FROM (
                      SELECT MatchId, RatioHome, RatioAway, CapturedAt,
                             ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
                      FROM dbo.FbExchange
                      WHERE MatchId IN @ids
                  ) t WHERE rn = 1",
                new { ids = matchIds }, cancellationToken: ct))).ToList();
        var exchangeMap = exchange.ToDictionary(e => e.MatchId);

        // 只取和当日比赛对得上的购买明细
        var bought = (await conn.QueryAsync<BoughtLine>(new CommandDefinition(
            @"SELECT pl.MatchNo, pl.Option1, pl.Option2, pl.HomeTeam, pl.AwayTeam, pl.MatchId
              FROM dbo.PurchaseLine pl
              WHERE pl.MatchId IN @ids
                 OR EXISTS (
                     SELECT 1
                     FROM dbo.Recommendation r
                     INNER JOIN dbo.FbMatch m ON m.MatchId = r.MatchId
                     WHERE r.RecDate = @d
                       AND m.HomeTeamName = pl.HomeTeam
                       AND m.AwayTeamName = pl.AwayTeam)",
            new { ids = matchIds.Length == 0 ? new long[] { -1 } : matchIds, d = date.Date }, cancellationToken: ct))).ToList();
        var boughtMap = bought
            .Where(b => !string.IsNullOrWhiteSpace(b.MatchNo))
            .GroupBy(b => b.MatchNo!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(b => new[] { b.Option1, b.Option2 })
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct()
                .Select(o => o!)
                .ToList(), StringComparer.OrdinalIgnoreCase);

        // 已购场次用「主客球队名对」匹配（MatchNo 在不同板块格式不同，球队名更稳定）
        // 同时把购买选项按球队名对映射，便于展示「你买了什么」
        var boughtByMatch = bought
            .Where(b => b.MatchId is not null)
            .GroupBy(b => b.MatchId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(b => new[] { b.Option1, b.Option2 })
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Distinct()
                    .Select(o => o!)
                    .ToList());

        var boughtTeams = bought
            .Where(b => !string.IsNullOrWhiteSpace(b.HomeTeam) && !string.IsNullOrWhiteSpace(b.AwayTeam))
            .GroupBy(b => $"{b.HomeTeam!.Trim().ToLowerInvariant()}|{b.AwayTeam!.Trim().ToLowerInvariant()}")
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(b => new[] { b.Option1, b.Option2 })
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Distinct()
                    .Select(o => o!)
                    .ToList());

        var result = new List<FinishedSummaryItem>();
        foreach (var r in recs.Where(r => r.MatchId is not null))
        {
            if (!finished.TryGetValue(r.MatchId!.Value, out var m)) continue; // 未完场则跳过

            var item = new FinishedSummaryItem
            {
                Board = r.Board,
                MatchNo = r.MatchNo,
                MatchId = m.MatchId,
                LeagueName = m.LeagueName,
                HomeTeam = m.HomeTeamName,
                AwayTeam = m.AwayTeamName,
                MatchTime = m.MatchTime,
                Handicap = m.Handicap,

                FullHome = m.HomeScore,
                FullAway = m.AwayScore,
                HalfHome = m.HalfHomeScore,
                HalfAway = m.HalfAwayScore,
                TotalGoals = (m.HomeScore ?? 0) + (m.AwayScore ?? 0),

                RecResult = r.RecResult,
                Confidence = r.Confidence,
                SignalDesc = r.SignalDesc,
                RecScore1 = r.Score1,
                RecScore2 = r.Score2,
                RecGoals1 = r.Goals1,
                RecGoals2 = r.Goals2,
                RecHalfFull = r.RecHalfFull,
            };

            item.ActualResult = m.HomeScore > m.AwayScore ? '3' : m.HomeScore == m.AwayScore ? '1' : '0';
            item.ActualResultText = SpfText(item.ActualResult);
            item.IsCoverAll = r.IsCoverAll || r.RecResult is null || (r.Confidence is null && r.RecResult == '1');
            item.RecResult = item.IsCoverAll ? null : r.RecResult;
            item.RecResultText = item.IsCoverAll ? "全包" : SpfText(item.RecResult);
            var directional = RecommendationScoring.DirectionalHit(item.RecResult, item.IsCoverAll, item.ActualResult);
            item.Hit = directional == true;
            item.CountsInStats = RecommendationScoring.CountsInHitRate(item.RecResult, item.IsCoverAll, r.Confidence);

            item.HandicapResult = BuildHandicapResult(m.HomeScore, m.AwayScore, m.Handicap);
            item.RecHandicapText = r.RecHandicap switch { '3' => "让胜", '1' => "让平", '0' => "让负", _ => "—" };
            item.HandicapHit = r.RecHandicap is null || item.HandicapResult is null
                ? null
                : r.RecHandicap == item.HandicapResult;

            // 半全场：半场胜负 + 全场胜负
            item.HalfFull = BuildHalfFull(m.HalfHomeScore, m.HalfAwayScore, m.HomeScore, m.AwayScore);

            // 推荐思路总结
            item.Analysis = BuildAnalysis(item, exchangeMap.TryGetValue(m.MatchId, out var ex) ? ex : null);

            // 购买标注：MatchId 优先，其次球队名，最后场次号
            if (boughtByMatch.TryGetValue(m.MatchId, out var boughtById))
            {
                item.IsBought = true;
                item.BoughtOptions = boughtById;
            }
            var teamKey = $"{m.HomeTeamName?.Trim().ToLowerInvariant()}|{m.AwayTeamName?.Trim().ToLowerInvariant()}";
            if (!item.IsBought && boughtTeams.TryGetValue(teamKey, out var boughtOptsByTeam))
            {
                item.IsBought = true;
                item.BoughtOptions = boughtOptsByTeam ?? new List<string>();
            }
            else if (!item.IsBought && !string.IsNullOrWhiteSpace(r.MatchNo) && boughtMap.TryGetValue(r.MatchNo.Trim(), out var opts2))
            {
                item.BoughtOptions = opts2;
                item.IsBought = true;
            }

            result.Add(item);
        }

        return result
            .OrderBy(x => x.MatchTime)
            .ToList();
    }

    private static string SpfText(char? r) => r switch { '3' => "胜", '1' => "平", '0' => "负", _ => "全包" };

    private static char? BuildHandicapResult(int? h, int? a, decimal? handicap)
    {
        if (h is null || a is null) return null;
        if (handicap is null) return null; // 无让球盘，让球胜平负无意义
        var diff = h.Value + handicap.Value - a.Value;
        return diff > 0 ? '3' : Math.Abs(diff) < 0.001m ? '1' : '0';
    }

    private static string? BuildHalfFull(int? hh, int? ha, int? fh, int? fa)
    {
        if (hh is null || ha is null || fh is null || fa is null) return null;
        char half = hh > ha ? '3' : hh == ha ? '1' : '0';
        char full = fh > fa ? '3' : fh == fa ? '1' : '0';
        return SpfText(half) + "/" + SpfText(full);
    }

    private static string BuildAnalysis(FinishedSummaryItem it, ExchangeRatio? ex)
    {
        var ratioHome = ex?.RatioHome;
        var ratioAway = ex?.RatioAway;
        if (it.IsCoverAll)
            return $"本场为全包，不计入命中率。赛果 {it.ActualResultText}。";

        var parts = new List<string>();
        var basis = it.SignalDesc ?? "";
        parts.Add($"推荐思路：{basis}。");

        if (it.Hit)
            parts.Add($"赛果 {it.ActualResultText}，命中推荐方向「{it.RecResultText}」。");
        else
            parts.Add($"赛果 {it.ActualResultText}，与推荐方向「{it.RecResultText}」不符，未命中。");

        if (!it.Hit)
        {
            if (it.RecResult == '3' && ratioHome is not null && ratioHome >= 75)
                parts.Add($"必发主胜占比 {ratioHome:0.#}% 属于极端大热，赛果没有跟随热门。");
            else if (it.RecResult == '0' && ratioAway is not null && ratioAway >= 75)
                parts.Add($"必发客胜占比 {ratioAway:0.#}% 属于极端大热，赛果没有跟随热门。");
        }

        if (!it.CountsInStats)
            parts.Add("置信度低于 60%，这条不计入默认命中率。");

        return string.Join(" ", parts);
    }

    private sealed class FinishedMatch
    {
        public long MatchId { get; set; }
        public string HomeTeamName { get; set; } = "";
        public string AwayTeamName { get; set; } = "";
        public string LeagueName { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
        public decimal? Handicap { get; set; }
    }

    private sealed class BoughtLine
    {
        public string? MatchNo { get; set; }
        public long? MatchId { get; set; }
        public string? Option1 { get; set; }
        public string? Option2 { get; set; }
        public string? HomeTeam { get; set; }
        public string? AwayTeam { get; set; }
    }

    private sealed class ExchangeRatio
    {
        public long MatchId { get; set; }
        public decimal? RatioHome { get; set; }
        public decimal? RatioAway { get; set; }
        public DateTime CapturedAt { get; set; }
    }
}

/// <summary>每日复盘单场</summary>
public sealed class FinishedSummaryItem
{
    public string Board { get; set; } = "";
    public string? MatchNo { get; set; }
    public long MatchId { get; set; }
    public string LeagueName { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public DateTime MatchTime { get; set; }
    public decimal? Handicap { get; set; }

    // 实际赛果
    public int? FullHome { get; set; }
    public int? FullAway { get; set; }
    public int? HalfHome { get; set; }
    public int? HalfAway { get; set; }
    public int? TotalGoals { get; set; }
    public string? HalfFull { get; set; }
    public char ActualResult { get; set; }
    public string ActualResultText { get; set; } = "";
    public char? HandicapResult { get; set; }
    public string HandicapResultText => HandicapResult is null ? "—" : SpfTextPub(HandicapResult.Value);

    // 推荐
    public char? RecResult { get; set; }
    public string RecResultText { get; set; } = "";
    public bool IsCoverAll { get; set; }
    public bool CountsInStats { get; set; }
    public string RecHandicapText { get; set; } = "";
    public string? RecHalfFull { get; set; }
    public decimal? Confidence { get; set; }
    public string? SignalDesc { get; set; }
    public string? RecScore1 { get; set; }
    public string? RecScore2 { get; set; }
    public int? RecGoals1 { get; set; }
    public int? RecGoals2 { get; set; }

    /// <summary>推荐方向、半全场和最终比分对应的赔率，如「胜：2.69」。</summary>
    public List<string> OddsNotes { get; set; } = new();

    public bool Hit { get; set; }
    public bool? HandicapHit { get; set; }

    /// <summary>推荐思路总结（依据 + 命中/未中原因）</summary>
    public string Analysis { get; set; } = "";

    public List<string> BoughtOptions { get; set; } = new();

    /// <summary>是否为用户实际购买的场次</summary>
    public bool IsBought { get; set; }

    /// <summary>
    /// 推荐质量分层：bought=实购场次 high=高置信(≥70) medium=中置信(60~70) low=低置信(&lt;60)
    /// </summary>
    public string Quality
    {
        get
        {
            if (IsCoverAll || RecResult is null) return "cover";
            if (IsBought) return "bought";
            if (Confidence >= 70m) return "high";
            if (Confidence >= 60m) return "medium";
            return "low";
        }
    }

    public string QualityText => Quality switch
    {
        "bought" => "实购",
        "high" => "高置信",
        "medium" => "中置信",
        _ => "低置信"
    };

    private static string SpfTextPub(char r) => r switch { '3' => "胜", '1' => "平", '0' => "负", _ => "—" };
}
