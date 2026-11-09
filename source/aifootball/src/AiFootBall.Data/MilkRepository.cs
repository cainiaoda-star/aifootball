using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>大奶历史样本与当日受注场查询。</summary>
public sealed class MilkRepository
{
    private readonly SqlConnectionFactory _factory;

    public MilkRepository(SqlConnectionFactory factory) => _factory = factory;

    public sealed class FinishedRow
    {
        public long MatchId { get; set; }
        public DateTime MatchTime { get; set; }
        public string? LeagueName { get; set; }
        public string? HomeTeam { get; set; }
        public string? AwayTeam { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
        public decimal? EuHome { get; set; }
        public decimal? EuAway { get; set; }
        public decimal? ExHome { get; set; }
        public decimal? ExAway { get; set; }
        public string? Board { get; set; }
        public string? MatchNo { get; set; }
    }

    public sealed class TodayCardRow
    {
        public string Board { get; set; } = "";
        public string MatchNo { get; set; } = "";
        public long? MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string? HomeTeam { get; set; }
        public string? AwayTeam { get; set; }
        public DateTime? MatchTime { get; set; }
        public decimal? SpfHome { get; set; }
        public decimal? SpfAway { get; set; }
    }

    /// <summary>
    /// scope: all | card | jc
    /// </summary>
    public async Task<List<FinishedRow>> GetFinishedAsync(string scope, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var s = (scope ?? "all").Trim().ToLowerInvariant();
        await using var conn = await _factory.OpenAsync(ct);

        var sql = s switch
        {
            "jc" => FinishedSql("AND bc.Board = 'jc'"),
            "card" => FinishedSql("AND bc.Board IN ('jc','bd')"),
            _ => FinishedSql(boardJoinRequired: false)
        };

        var rows = await conn.QueryAsync<FinishedRow>(new CommandDefinition(
            sql, new { from = from.Date, to = to.Date.AddDays(1) }, cancellationToken: ct));
        return rows.ToList();
    }

    private static string FinishedSql(string? boardFilter = null, bool boardJoinRequired = true)
    {
        if (!boardJoinRequired)
        {
            return @"
SELECT f.MatchId, f.MatchTime, f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
       f.HomeScore, f.AwayScore, f.HalfHomeScore, f.HalfAwayScore,
       eu.HomeWin AS EuHome, eu.AwayWin AS EuAway,
       ex.RatioHome AS ExHome, ex.RatioAway AS ExAway,
       CAST(NULL AS NVARCHAR(8)) AS Board, CAST(NULL AS NVARCHAR(20)) AS MatchNo
FROM dbo.FbMatch f
LEFT JOIN (
    SELECT MatchId,
           MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS HomeWin,
           MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS AwayWin
    FROM dbo.FbOdds
    WHERE OddsType = 'EU' AND CompanyId = 0
    GROUP BY MatchId
) eu ON eu.MatchId = f.MatchId
LEFT JOIN (
    SELECT MatchId, RatioHome, RatioAway
    FROM (
        SELECT MatchId, RatioHome, RatioAway,
               ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
        FROM dbo.FbExchange
    ) t WHERE rn = 1
) ex ON ex.MatchId = f.MatchId
WHERE f.Status = 3
  AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
  AND f.MatchTime >= @from AND f.MatchTime < @to";
        }

        return $@"
SELECT f.MatchId, f.MatchTime, COALESCE(bc.LeagueName, f.LeagueName) AS LeagueName,
       COALESCE(bc.HomeTeam, f.HomeTeamName) AS HomeTeam, COALESCE(bc.AwayTeam, f.AwayTeamName) AS AwayTeam,
       f.HomeScore, f.AwayScore, f.HalfHomeScore, f.HalfAwayScore,
       COALESCE(bc.SpfHome, eu.HomeWin) AS EuHome, COALESCE(bc.SpfAway, eu.AwayWin) AS EuAway,
       ex.RatioHome AS ExHome, ex.RatioAway AS ExAway,
       bc.Board, bc.MatchNo
FROM (
    SELECT MatchId, Board, MatchNo, LeagueName, HomeTeam, AwayTeam, SpfHome, SpfAway,
           ROW_NUMBER() OVER (PARTITION BY MatchId, Board ORDER BY CapturedAt DESC) AS rn
    FROM dbo.BettingCard
    WHERE MatchId IS NOT NULL
) bc
JOIN dbo.FbMatch f ON f.MatchId = bc.MatchId
LEFT JOIN (
    SELECT MatchId,
           MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS HomeWin,
           MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS AwayWin
    FROM dbo.FbOdds
    WHERE OddsType = 'EU' AND CompanyId = 0
    GROUP BY MatchId
) eu ON eu.MatchId = f.MatchId
LEFT JOIN (
    SELECT MatchId, RatioHome, RatioAway
    FROM (
        SELECT MatchId, RatioHome, RatioAway,
               ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
        FROM dbo.FbExchange
    ) t WHERE rn = 1
) ex ON ex.MatchId = f.MatchId
WHERE bc.rn = 1
  {boardFilter}
  AND f.Status = 3
  AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
  AND f.MatchTime >= @from AND f.MatchTime < @to";
    }

    public async Task<List<TodayCardRow>> GetOpenCardsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TodayCardRow>(new CommandDefinition(
            @"SELECT Board, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam, MatchTime, SpfHome, SpfAway
              FROM (
                  SELECT Board, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam, MatchTime, SpfHome, SpfAway, CapturedAt,
                         ROW_NUMBER() OVER (PARTITION BY Board, MatchNo ORDER BY CapturedAt DESC) AS rn
                  FROM dbo.BettingCard
                  WHERE Board IN ('jc','bd')
              ) t
              WHERE rn = 1
                AND (MatchTime IS NULL OR MatchTime > SYSDATETIME())
              ORDER BY Board, MatchTime, MatchNo",
            cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>取竞彩固定奖金最新一档里的指定选项赔率。</summary>
    public async Task<Dictionary<long, Dictionary<string, decimal>>> GetLatestJcOptionOddsAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        var result = new Dictionary<long, Dictionary<string, decimal>>();
        if (ids.Length == 0) return result;

        await using var conn = await _factory.OpenAsync(ct);
        foreach (var chunk in Chunk(ids, 400))
        {
            var rows = await conn.QueryAsync<(long MatchId, string PlayType, string OptionCode, decimal Odds)>(new CommandDefinition(
                @"SELECT MatchId, PlayType, OptionCode, Odds
                  FROM (
                      SELECT MatchId, PlayType, OptionCode, Odds,
                             ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt DESC) AS rn
                      FROM dbo.FbJcOddsHistory
                      WHERE MatchId IN @ids
                        AND (
                              (PlayType = @jq AND OptionCode IN ('7','7+'))
                           OR (PlayType = @bq AND OptionCode IN ('3-0','0-3','3-1','0-1'))
                           OR (PlayType = @bf AND (OptionCode LIKE N'%其它%' OR OptionCode LIKE N'%其他%'))
                        )
                  ) t
                  WHERE rn = 1",
                new { ids = chunk, jq = JcPlayTypes.Jq, bq = JcPlayTypes.Bq, bf = JcPlayTypes.Bf },
                cancellationToken: ct));
            foreach (var r in rows)
            {
                if (!result.TryGetValue(r.MatchId, out var map))
                {
                    map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                    result[r.MatchId] = map;
                }
                map[$"{r.PlayType}|{r.OptionCode}"] = r.Odds;
            }
        }
        return result;
    }

    /// <summary>C 口径：大奶命中场次上对应 SP（若有历史采样）的中位数。</summary>
    public async Task<Dictionary<string, decimal?>> GetJcSpMediansForHitsAsync(
        IReadOnlyList<(long MatchId, string Tag)> hits, CancellationToken ct = default)
    {
        var medians = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        if (hits.Count == 0) return medians;

        var byTag = hits.GroupBy(h => h.Tag, StringComparer.Ordinal);
        await using var conn = await _factory.OpenAsync(ct);

        foreach (var g in byTag)
        {
            var (play, codes) = TagToJcLookup(g.Key);
            if (play is null) continue;
            var ids = g.Select(x => x.MatchId).Distinct().ToArray();
            if (ids.Length == 0) continue;

            var odds = new List<decimal>();
            foreach (var chunk in Chunk(ids, 400))
            {
                var rows = await conn.QueryAsync<decimal>(new CommandDefinition(
                    @"SELECT Odds
                      FROM (
                          SELECT Odds,
                                 ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
                          FROM dbo.FbJcOddsHistory
                          WHERE MatchId IN @ids AND PlayType = @pt AND OptionCode IN @codes
                      ) t WHERE rn = 1",
                    new { ids = chunk, pt = play, codes }, cancellationToken: ct));
                odds.AddRange(rows);
            }

            medians[g.Key] = Median(odds);
        }

        return medians;
    }

    private static (string? Play, string[] Codes) TagToJcLookup(string tag) => tag switch
    {
        MilkEventTaxonomy.Tags.Goals7p => (JcPlayTypes.Jq, new[] { "7", "7+" }),
        MilkEventTaxonomy.Tags.HfWinLose => (JcPlayTypes.Bq, new[] { "3-0" }),
        MilkEventTaxonomy.Tags.HfLoseWin => (JcPlayTypes.Bq, new[] { "0-3" }),
        MilkEventTaxonomy.Tags.HfWinDraw => (JcPlayTypes.Bq, new[] { "3-1" }),
        MilkEventTaxonomy.Tags.HfLoseDraw => (JcPlayTypes.Bq, new[] { "0-1" }),
        MilkEventTaxonomy.Tags.ScoreOtherHome => (JcPlayTypes.Bf, new[] { "胜其它", "胜其他" }),
        MilkEventTaxonomy.Tags.ScoreOtherAway => (JcPlayTypes.Bf, new[] { "负其它", "负其他" }),
        _ => (null, Array.Empty<string>())
    };

    private static decimal? Median(List<decimal> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2m;
    }

    private static IEnumerable<T[]> Chunk<T>(T[] source, int size)
    {
        for (var i = 0; i < source.Length; i += size)
        {
            var n = Math.Min(size, source.Length - i);
            var chunk = new T[n];
            Array.Copy(source, i, chunk, 0, n);
            yield return chunk;
        }
    }
}
