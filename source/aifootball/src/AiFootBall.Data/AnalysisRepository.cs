using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 每日完场分析：固化「赔率变化 / 必发投注 / 亚盘让球 / 大小球」四类规律统计。
/// 供每日自动分析与 API 查询复用。
/// </summary>
public sealed class AnalysisRepository
{
    private readonly SqlConnectionFactory _factory;

    public AnalysisRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>对指定日期区间的完场比赛做全维度规律分析</summary>
    public async Task<DailyAnalysis> AnalyzeAsync(DateTime date, CancellationToken ct = default)
    {
        var from = date.Date;
        var to = date.Date.AddDays(1);
        await using var conn = await _factory.OpenAsync(ct);

        var matchCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMatch WHERE Status=3 AND MatchTime>=@f AND MatchTime<@t",
            new { f = from, t = to });

        var oddsMovement = (await conn.QueryAsync(@"
WITH today AS (SELECT MatchId, ResultSpf FROM dbo.FbMatch WHERE Status=3 AND MatchTime>=@f AND MatchTime<@t),
o AS (SELECT MatchId, AVG(HomeWin) h FROM dbo.FbOdds WHERE OddsType='EU' AND IsOpening=1 AND CompanyId=0 GROUP BY MatchId),
c AS (SELECT MatchId, AVG(HomeWin) h FROM dbo.FbOdds WHERE OddsType='EU' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId)
SELECT
  CASE WHEN c.h - o.h <= -0.20 THEN N'大降' WHEN c.h - o.h < -0.05 THEN N'小降'
       WHEN c.h - o.h BETWEEN -0.05 AND 0.05 THEN N'持平' ELSE N'上升' END AS grp,
  COUNT(*) AS n,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf='3' THEN 1 ELSE 0 END)/COUNT(*),1) AS homePct,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf='0' THEN 1 ELSE 0 END)/COUNT(*),1) AS awayPct
FROM today t JOIN o ON t.MatchId=o.MatchId JOIN c ON t.MatchId=c.MatchId
GROUP BY CASE WHEN c.h - o.h <= -0.20 THEN N'大降' WHEN c.h - o.h < -0.05 THEN N'小降'
       WHEN c.h - o.h BETWEEN -0.05 AND 0.05 THEN N'持平' ELSE N'上升' END
ORDER BY MIN(c.h - o.h)", new { f = from, t = to })).ToList();

        var exchange = (await conn.QueryAsync(@"
WITH today AS (SELECT MatchId, ResultSpf FROM dbo.FbMatch WHERE Status=3 AND MatchTime>=@f AND MatchTime<@t),
ex AS (SELECT MatchId, RatioHome, RatioAway FROM dbo.FbExchange)
SELECT
  CASE WHEN e.RatioHome >= 60 THEN N'主胜大热' WHEN e.RatioAway >= 60 THEN N'客胜大热' ELSE N'均衡' END AS grp,
  COUNT(*) AS n,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf='3' THEN 1 ELSE 0 END)/COUNT(*),1) AS homePct,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf='0' THEN 1 ELSE 0 END)/COUNT(*),1) AS awayPct
FROM today t JOIN ex e ON t.MatchId=e.MatchId
WHERE e.RatioHome IS NOT NULL OR e.RatioAway IS NOT NULL
GROUP BY CASE WHEN e.RatioHome >= 60 THEN N'主胜大热' WHEN e.RatioAway >= 60 THEN N'客胜大热' ELSE N'均衡' END
ORDER BY n DESC", new { f = from, t = to })).ToList();

        var asian = (await conn.QueryAsync(@"
WITH today AS (SELECT MatchId, ResultSpf FROM dbo.FbMatch WHERE Status=3 AND MatchTime>=@f AND MatchTime<@t),
a AS (SELECT MatchId, AVG(Handicap) h FROM dbo.FbOdds WHERE OddsType='AH' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId)
SELECT
  CASE WHEN a.h <= -0.75 THEN N'主让0.75+' WHEN a.h < 0 THEN N'主让0.25-0.5' ELSE N'客让/平手' END AS grp,
  COUNT(*) AS n,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf='3' THEN 1 ELSE 0 END)/COUNT(*),1) AS homePct,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf='0' THEN 1 ELSE 0 END)/COUNT(*),1) AS awayPct
FROM today t JOIN a ON t.MatchId=a.MatchId
GROUP BY CASE WHEN a.h <= -0.75 THEN N'主让0.75+' WHEN a.h < 0 THEN N'主让0.25-0.5' ELSE N'客让/平手' END
ORDER BY n DESC", new { f = from, t = to })).ToList();

        var totals = (await conn.QueryAsync(@"
WITH today AS (SELECT MatchId, HomeScore, AwayScore FROM dbo.FbMatch WHERE Status=3 AND MatchTime>=@f AND MatchTime<@t),
o AS (SELECT MatchId, AVG(TotalLine) tl FROM dbo.FbOdds WHERE OddsType='OU' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId)
SELECT
  CASE WHEN o.tl >= 2.5 THEN N'盘口大' ELSE N'盘口小' END AS grp,
  COUNT(*) AS n,
  ROUND(100.0*SUM(CASE WHEN t.HomeScore+t.AwayScore >= 3 THEN 1 ELSE 0 END)/COUNT(*),1) AS overPct
FROM today t JOIN o ON t.MatchId=o.MatchId
GROUP BY CASE WHEN o.tl >= 2.5 THEN N'盘口大' ELSE N'盘口小' END", new { f = from, t = to })).ToList();

        return new DailyAnalysis
        {
            Date = from,
            MatchCount = matchCount,
            OddsMovement = oddsMovement,
            Exchange = exchange,
            Asian = asian,
            Totals = totals
        };
    }
}

public sealed class DailyAnalysis
{
    public DateTime Date { get; set; }
    public int MatchCount { get; set; }
    public IEnumerable<dynamic> OddsMovement { get; set; } = new List<dynamic>();
    public IEnumerable<dynamic> Exchange { get; set; } = new List<dynamic>();
    public IEnumerable<dynamic> Asian { get; set; } = new List<dynamic>();
    public IEnumerable<dynamic> Totals { get; set; } = new List<dynamic>();
}
