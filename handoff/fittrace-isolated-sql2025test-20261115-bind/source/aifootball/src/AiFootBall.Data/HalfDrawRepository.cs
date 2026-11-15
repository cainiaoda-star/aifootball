using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 半全场「胜平/负平」公平价参考的取数层（2026-11-08 加）。
///
/// 只负责把「某天在售场次」的三组报价取齐：SPF 三项、JQ 总进球八档、BQ 胜平/负平两档。
/// 模型计算在 Sources 层（HalfDrawModel），因为那是纯函数、要能单测，Data 层不反依赖它。
///
/// 报价取**最新快照**而不是「赛前最高价」：这一区是给用户看「现在这个价贵不贵」，
/// 拿历史最高价去比会把所有注都显得偏贵。
/// </summary>
public sealed class HalfDrawRepository
{
    private readonly SqlConnectionFactory _factory;

    public HalfDrawRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// 取某受注日的全部场次报价。受注日按场次号前缀（「周日」）判定，不看开赛日期——
    /// 周日凌晨 02:45 开球的场次编号仍是「周六028」，按 MatchTime 取会把两天混在一起。
    /// </summary>
    public async Task<List<HalfDrawRow>> GetDayAsync(DateTime date, CancellationToken ct = default)
    {
        var d = date.Date;
        var wd = "周" + "日一二三四五六"[(int)d.DayOfWeek];

        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<HalfDrawRow>(new CommandDefinition(
            @"WITH nos AS (
                  SELECT h.MatchId, MAX(h.MatchNo) AS RawNo
                  FROM dbo.FbJcOddsHistory h
                  WHERE h.MatchId IN (SELECT f.MatchId FROM dbo.FbMatch f
                                      WHERE f.MatchTime >= DATEADD(day,-1,@d)
                                        AND f.MatchTime <  DATEADD(day,2,@d))
                  GROUP BY h.MatchId
              ), sel AS (
                  SELECT n.MatchId, n.RawNo, LTRIM(RTRIM(n.RawNo)) AS MatchNo
                  FROM nos n
                  WHERE LTRIM(RTRIM(n.RawNo)) LIKE @wd + '%'
              ), last AS (
                  -- 每场每玩法的每个档位只留最后一次快照
                  SELECT h.MatchId, h.PlayType, h.OptionCode, h.Odds,
                         ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.PlayType, h.OptionCode
                                            ORDER BY h.CapturedAt DESC) AS rn
                  FROM dbo.FbJcOddsHistory h
                  JOIN sel s ON s.RawNo = h.MatchNo
                  WHERE h.PlayType IN ('SPF','JQ','BQ')
              )
              SELECT s.MatchId,
                     s.MatchNo,
                     LTRIM(RTRIM(m.LeagueName))   AS LeagueName,
                     LTRIM(RTRIM(m.HomeTeamName)) AS HomeTeam,
                     LTRIM(RTRIM(m.AwayTeamName)) AS AwayTeam,
                     m.MatchTime,
                     m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore,
                     MAX(CASE WHEN l.PlayType='SPF' AND l.OptionCode='3' THEN l.Odds END) AS O3,
                     MAX(CASE WHEN l.PlayType='SPF' AND l.OptionCode='1' THEN l.Odds END) AS O1,
                     MAX(CASE WHEN l.PlayType='SPF' AND l.OptionCode='0' THEN l.Odds END) AS O0,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='0' THEN l.Odds END) AS J0,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='1' THEN l.Odds END) AS J1,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='2' THEN l.Odds END) AS J2,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='3' THEN l.Odds END) AS J3,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='4' THEN l.Odds END) AS J4,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='5' THEN l.Odds END) AS J5,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='6' THEN l.Odds END) AS J6,
                     MAX(CASE WHEN l.PlayType='JQ'  AND l.OptionCode='7' THEN l.Odds END) AS J7,
                     MAX(CASE WHEN l.PlayType='BQ'  AND l.OptionCode='3-1' THEN l.Odds END) AS B31,
                     MAX(CASE WHEN l.PlayType='BQ'  AND l.OptionCode='0-1' THEN l.Odds END) AS B01
              FROM sel s
              JOIN last l ON l.MatchId = s.MatchId AND l.rn = 1
              JOIN dbo.FbMatch m ON m.MatchId = s.MatchId
              GROUP BY s.MatchId, s.MatchNo, m.LeagueName, m.HomeTeamName, m.AwayTeamName,
                       m.MatchTime, m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore
              ORDER BY s.MatchNo",
            new { d, wd }, cancellationToken: ct))).ToList();
    }
}

/// <summary>一场比赛的三玩法最新报价 + 赛果（赛果仅用于回看验证，模型不用它）。</summary>
public sealed class HalfDrawRow
{
    public long MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string LeagueName { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public DateTime MatchTime { get; set; }

    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public int? HalfHomeScore { get; set; }
    public int? HalfAwayScore { get; set; }

    public decimal? O3 { get; set; }
    public decimal? O1 { get; set; }
    public decimal? O0 { get; set; }

    public decimal? J0 { get; set; }
    public decimal? J1 { get; set; }
    public decimal? J2 { get; set; }
    public decimal? J3 { get; set; }
    public decimal? J4 { get; set; }
    public decimal? J5 { get; set; }
    public decimal? J6 { get; set; }
    public decimal? J7 { get; set; }

    public decimal? B31 { get; set; }
    public decimal? B01 { get; set; }

    /// <summary>总进球八档，顺序 0..6、7+。供 HalfDrawModel 直接消费。</summary>
    public decimal?[] TotalGoals() => new[] { J0, J1, J2, J3, J4, J5, J6, J7 };
}
