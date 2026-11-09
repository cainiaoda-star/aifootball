using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>
/// 赔率变化轨迹与必发成交的分析查询。
///
/// 两类数据回答同一个问题：盘口/资金在赛前怎么动，动完的赛果是什么。
///  · FbOddsHistory —— 同一场比赛、同一家公司，在多个时点上的盘口快照（初盘 → 赛前 → 开赛）
///  · FbExchange    —— 必发交易所的成交量、成交价、庄家盈亏与指数快照
/// </summary>
public sealed class TrajectoryRepository
{
    private readonly SqlConnectionFactory _factory;

    public TrajectoryRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>单场赔率轨迹（按公司 × 玩法分组，时间升序）。</summary>
    public async Task<object?> GetOddsTrajectoryAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var points = (await conn.QueryAsync<AiFootBall.Core.Models.OddsHistoryPoint>(
            @"SELECT MatchId, CapturedAt, MinutesToKick, Phase, OddsType, CompanyId, Company,
                     HomeWin, Draw, AwayWin, ReturnRate, KellyWin, KellyDraw, KellyLoss,
                     Handicap, HomeWater, AwayWater, TotalLine, OverWater, UnderWater
              FROM dbo.FbOddsHistory
              WHERE MatchId = @matchId AND CompanyId IN (0, 3, 280, 5)
              ORDER BY OddsType, CompanyId, CapturedAt", new { matchId })).ToList();

        if (points.Count == 0) return null;

        var groups = points
            .GroupBy(p => new { p.OddsType, p.Company })
            .Select(g =>
            {
                var list = g.OrderBy(p => p.CapturedAt).ToList();
                var first = list[0];
                var last = list[^1];
                return new
                {
                    oddsType = g.Key.OddsType,
                    company = g.Key.Company,
                    pointCount = list.Count,
                    firstAt = first.CapturedAt,
                    lastAt = last.CapturedAt,
                    firstMinutesToKick = first.MinutesToKick,
                    lastMinutesToKick = last.MinutesToKick,
                    openHome = first.HomeWin,
                    lastHome = last.HomeWin,
                    homeDelta = first.HomeWin.HasValue && last.HomeWin.HasValue
                        ? last.HomeWin - first.HomeWin : null,
                    homeDeltaPct = first.HomeWin is > 0 && last.HomeWin.HasValue
                        ? (double?)Math.Round((double)((last.HomeWin / first.HomeWin - 1) * 100), 2)
                        : null,
                    openHandicap = first.Handicap,
                    lastHandicap = last.Handicap,
                    handicapDelta = first.Handicap.HasValue && last.Handicap.HasValue
                        ? last.Handicap - first.Handicap : null,
                    openTotalLine = first.TotalLine,
                    lastTotalLine = last.TotalLine,
                    points = list.Select(p => new
                    {
                        capturedAt = p.CapturedAt,
                        minutesToKick = p.MinutesToKick,
                        phase = p.Phase,
                        homeWin = p.HomeWin,
                        draw = p.Draw,
                        awayWin = p.AwayWin,
                        handicap = p.Handicap,
                        homeWater = p.HomeWater,
                        awayWater = p.AwayWater,
                        totalLine = p.TotalLine,
                        overWater = p.OverWater,
                        underWater = p.UnderWater
                    }).ToList()
                };
            })
            .ToList();

        return new { matchId, groupCount = groups.Count, pointCount = points.Count, groups };
    }

    /// <summary>
    /// 赔率轨迹特征 → 赛果。只取平均欧赔（CompanyId=0），
    /// 每场至少 3 个采样点才纳入统计。
    /// </summary>
    public async Task<object?> GetTrajectoryStatsAsync(int minPoints = 3, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var drift = await conn.QueryAsync(
            @"WITH eu AS (
                SELECT h.MatchId, h.CapturedAt, h.MinutesToKick, h.HomeWin, h.Draw, h.AwayWin,
                       ROW_NUMBER() OVER (PARTITION BY h.MatchId ORDER BY h.CapturedAt) AS rn,
                       COUNT(*) OVER (PARTITION BY h.MatchId) AS cnt
                FROM dbo.FbOddsHistory h
                WHERE h.OddsType = 'EU' AND h.CompanyId = 0 AND h.HomeWin > 1
              ), f AS (
                SELECT MatchId,
                       MAX(CASE WHEN rn = 1 THEN HomeWin END) AS OpenHome,
                       MAX(CASE WHEN rn = 1 THEN Draw END) AS OpenDraw,
                       MAX(CASE WHEN rn = 1 THEN AwayWin END) AS OpenAway,
                       MAX(CASE WHEN rn = cnt THEN HomeWin END) AS LastHome,
                       MAX(CASE WHEN rn = cnt THEN Draw END) AS LastDraw,
                       MAX(CASE WHEN rn = cnt THEN AwayWin END) AS LastAway,
                       MAX(cnt) AS Points,
                       MIN(MinutesToKick) AS OpenMtk,
                       MAX(MinutesToKick) AS CloseMtk
                FROM eu GROUP BY MatchId
              )
              SELECT CASE
                       WHEN f.LastHome <= f.OpenHome * 0.95 THEN N'1_主胜赔率大降(>5%)'
                       WHEN f.LastHome <  f.OpenHome * 0.99 THEN N'2_主胜赔率小降'
                       WHEN f.LastHome >= f.OpenHome * 1.05 THEN N'4_主胜赔率大升(>5%)'
                       WHEN f.LastHome >  f.OpenHome * 1.01 THEN N'5_主胜赔率小升'
                       ELSE N'3_基本持平' END AS Bucket,
                     COUNT(*) AS Matches,
                     CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct,
                     CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '1' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS DrawPct,
                     CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '0' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS AwayWinPct,
                     CAST(ROUND(AVG(CAST(f.LastHome - f.OpenHome AS FLOAT)), 3) AS FLOAT) AS AvgHomeDelta
              FROM f
              JOIN dbo.FbMatch m ON m.MatchId = f.MatchId AND m.Status = 3
              WHERE f.Points >= @minPoints AND f.OpenHome IS NOT NULL AND f.LastHome IS NOT NULL
              GROUP BY CASE
                       WHEN f.LastHome <= f.OpenHome * 0.95 THEN N'1_主胜赔率大降(>5%)'
                       WHEN f.LastHome <  f.OpenHome * 0.99 THEN N'2_主胜赔率小降'
                       WHEN f.LastHome >= f.OpenHome * 1.05 THEN N'4_主胜赔率大升(>5%)'
                       WHEN f.LastHome >  f.OpenHome * 1.01 THEN N'5_主胜赔率小升'
                       ELSE N'3_基本持平' END
              ORDER BY Bucket", new { minPoints });

        // 临场（最后一个采样点距开赛 ≤90 分钟）相对初盘的变化，单独看“尾盘异动”
        var late = await conn.QueryAsync(
            @"WITH eu AS (
                SELECT h.MatchId, h.CapturedAt, h.MinutesToKick, h.HomeWin,
                       ROW_NUMBER() OVER (PARTITION BY h.MatchId ORDER BY h.CapturedAt) AS rn,
                       ROW_NUMBER() OVER (PARTITION BY h.MatchId ORDER BY h.CapturedAt DESC) AS rrn,
                       COUNT(*) OVER (PARTITION BY h.MatchId) AS cnt
                FROM dbo.FbOddsHistory h
                WHERE h.OddsType = 'EU' AND h.CompanyId = 0 AND h.HomeWin > 1
              ), f AS (
                SELECT MatchId,
                       MAX(CASE WHEN rn = 1 THEN HomeWin END) AS OpenHome,
                       MAX(CASE WHEN rrn = 1 THEN HomeWin END) AS LastHome,
                       MAX(CASE WHEN rrn = 1 THEN MinutesToKick END) AS LastMtk,
                       MAX(cnt) AS Points
                FROM eu GROUP BY MatchId
              )
              SELECT COUNT(*) AS Matches,
                     SUM(CASE WHEN f.LastMtk <= 90 THEN 1 ELSE 0 END) AS LateSampled,
                     CAST(ROUND(AVG(CASE WHEN f.LastMtk <= 90 AND m.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct
              FROM f JOIN dbo.FbMatch m ON m.MatchId = f.MatchId AND m.Status = 3
              WHERE f.Points >= @minPoints", new { minPoints });

        return new { drift = drift.ToList(), summary = late.FirstOrDefault() };
    }

    /// <summary>必发成交信号 → 赛果。每场取最后一个快照。</summary>
    public async Task<object?> GetExchangeStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        const string latest = @"
WITH last AS (
    SELECT e.*, ROW_NUMBER() OVER (PARTITION BY e.MatchId ORDER BY e.CapturedAt DESC) AS rn
    FROM dbo.FbExchange e
)";

        // 1) 必发交易比例 → 赛果
        var ratio = await conn.QueryAsync(latest + @"
SELECT CASE WHEN l.RatioHome >= 60 THEN N'主队占比≥60%'
            WHEN l.RatioHome >= 45 THEN N'主队占比45~60%'
            WHEN l.RatioAway >= 60 THEN N'客队占比≥60%'
            WHEN l.RatioAway >= 45 THEN N'客队占比45~60%'
            ELSE N'主客均衡' END AS Bucket,
       CASE WHEN l.RatioHome >= 60 OR l.RatioAway >= 60 THEN 1 ELSE 0 END AS Extreme,
       COUNT(*) AS Matches,
       CAST(ROUND(AVG(CAST(l.TotalVolume AS FLOAT)), 0) AS FLOAT) AS AvgVolume,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '1' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS DrawPct,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '0' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS AwayWinPct
FROM last l
JOIN dbo.FbMatch m ON m.MatchId = l.MatchId AND m.Status = 3
WHERE l.rn = 1 AND l.RatioHome IS NOT NULL
GROUP BY CASE WHEN l.RatioHome >= 60 THEN N'主队占比≥60%'
              WHEN l.RatioHome >= 45 THEN N'主队占比45~60%'
              WHEN l.RatioAway >= 60 THEN N'客队占比≥60%'
              WHEN l.RatioAway >= 45 THEN N'客队占比45~60%'
              ELSE N'主客均衡' END,
         CASE WHEN l.RatioHome >= 60 OR l.RatioAway >= 60 THEN 1 ELSE 0 END
ORDER BY Matches DESC");

        // 2) 庄家盈亏符号 → 赛果（Pnl<0 表示该结果打出时庄家亏，即被买重的一方）
        var pnl = await conn.QueryAsync(latest + @"
SELECT CASE WHEN l.PnlHome < 0 AND l.PnlAway > 0 THEN N'主胜方向庄家亏'
            WHEN l.PnlAway < 0 AND l.PnlHome > 0 THEN N'客胜方向庄家亏'
            ELSE N'双向被买/不明' END AS Bucket,
       COUNT(*) AS Matches,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '0' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS AwayWinPct
FROM last l
JOIN dbo.FbMatch m ON m.MatchId = l.MatchId AND m.Status = 3
WHERE l.rn = 1 AND l.PnlHome IS NOT NULL
GROUP BY CASE WHEN l.PnlHome < 0 AND l.PnlAway > 0 THEN N'主胜方向庄家亏'
              WHEN l.PnlAway < 0 AND l.PnlHome > 0 THEN N'客胜方向庄家亏'
              ELSE N'双向被买/不明' END
ORDER BY Matches DESC");

        // 3) 必发成交量规模 vs 赛果（规模越大通常越“重”）
        var volume = await conn.QueryAsync(latest + @"
SELECT CASE WHEN l.TotalVolume >= 500000 THEN N'≥50万'
            WHEN l.TotalVolume >= 200000 THEN N'20~50万'
            WHEN l.TotalVolume >= 50000  THEN N'5~20万'
            ELSE N'<5万' END AS Bucket,
       COUNT(*) AS Matches,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '1' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS DrawPct,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '0' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS AwayWinPct
FROM last l
JOIN dbo.FbMatch m ON m.MatchId = l.MatchId AND m.Status = 3
WHERE l.rn = 1 AND l.TotalVolume IS NOT NULL
GROUP BY CASE WHEN l.TotalVolume >= 500000 THEN N'≥50万'
              WHEN l.TotalVolume >= 200000 THEN N'20~50万'
              WHEN l.TotalVolume >= 50000  THEN N'5~20万'
              ELSE N'<5万' END
ORDER BY Matches DESC");

        // 4) 大额交易方向 → 赛果
        var big = await conn.QueryAsync(latest + @"
SELECT CASE WHEN l.BigTotal IS NULL OR l.BigTotal <= 0 THEN N'无大额'
            WHEN l.BigHome > l.BigAway * 1.5 THEN N'大额偏主'
            WHEN l.BigAway > l.BigHome * 1.5 THEN N'大额偏客'
            ELSE N'大额均衡' END AS Bucket,
       COUNT(*) AS Matches,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct,
       CAST(ROUND(AVG(CASE WHEN m.ResultSpf = '0' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS AwayWinPct
FROM last l
JOIN dbo.FbMatch m ON m.MatchId = l.MatchId AND m.Status = 3
WHERE l.rn = 1
GROUP BY CASE WHEN l.BigTotal IS NULL OR l.BigTotal <= 0 THEN N'无大额'
              WHEN l.BigHome > l.BigAway * 1.5 THEN N'大额偏主'
              WHEN l.BigAway > l.BigHome * 1.5 THEN N'大额偏客'
              ELSE N'大额均衡' END
ORDER BY Matches DESC");

        return new
        {
            ratio = ratio.ToList(),
            pnl = pnl.ToList(),
            volume = volume.ToList(),
            big = big.ToList()
        };
    }

    /// <summary>
    /// 单场「水位变化」：胜平负三线欧赔（平均值 CompanyId=0）逐采样点变化 + 必发成交量逐点变化。
    /// 每个采样点相对前一点计算涨跌（delta），供前端画红色↑/绿色↓箭头。
    /// </summary>
    public async Task<object?> GetWaterMovementAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // 胜平负三线（欧赔平均值 CompanyId=0）时间序列
        var odds = (await conn.QueryAsync<AiFootBall.Core.Models.OddsHistoryPoint>(
            @"SELECT MatchId, CapturedAt, MinutesToKick, Phase, OddsType, CompanyId, Company,
                     HomeWin, Draw, AwayWin, ReturnRate
              FROM dbo.FbOddsHistory
              WHERE MatchId = @matchId AND OddsType = 'EU' AND CompanyId = 0
              ORDER BY CapturedAt", new { matchId })).ToList();

        // 必发成交量时间序列
        var ex = (await conn.QueryAsync(
            @"SELECT CapturedAt, MinutesToKick, VolumeHome, VolumeDraw, VolumeAway, TotalVolume,
                     RatioHome, RatioDraw, RatioAway
              FROM dbo.FbExchange WHERE MatchId = @matchId ORDER BY CapturedAt", new { matchId })).ToList();

        // 计算胜平负三线逐点涨跌
        var spfPoints = new List<object>();
        for (int i = 0; i < odds.Count; i++)
        {
            var cur = odds[i];
            var prev = i > 0 ? odds[i - 1] : null;
            spfPoints.Add(new
            {
                capturedAt = cur.CapturedAt,
                minutesToKick = cur.MinutesToKick,
                home = cur.HomeWin,
                draw = cur.Draw,
                away = cur.AwayWin,
                homeDelta = prev is not null && cur.HomeWin.HasValue && prev.HomeWin.HasValue
                    ? decimal.Round(cur.HomeWin.Value - prev.HomeWin.Value, 2) : (decimal?)null,
                drawDelta = prev is not null && cur.Draw.HasValue && prev.Draw.HasValue
                    ? decimal.Round(cur.Draw.Value - prev.Draw.Value, 2) : (decimal?)null,
                awayDelta = prev is not null && cur.AwayWin.HasValue && prev.AwayWin.HasValue
                    ? decimal.Round(cur.AwayWin.Value - prev.AwayWin.Value, 2) : (decimal?)null
            });
        }

        // 计算必发成交量逐点变化
        var exPoints = new List<object>();
        for (int i = 0; i < ex.Count; i++)
        {
            var cur = (IDictionary<string, object?>)ex[i];
            var prev = i > 0 ? (IDictionary<string, object?>)ex[i - 1] : null;
            decimal? D(string k) => cur[k] is decimal v ? v : null;
            decimal? PD(string k) => prev is not null && prev[k] is decimal pv ? pv : null;
            exPoints.Add(new
            {
                capturedAt = cur["CapturedAt"],
                minutesToKick = cur["MinutesToKick"],
                volumeHome = D("VolumeHome"),
                volumeDraw = D("VolumeDraw"),
                volumeAway = D("VolumeAway"),
                totalVolume = D("TotalVolume"),
                ratioHome = D("RatioHome"),
                ratioDraw = D("RatioDraw"),
                ratioAway = D("RatioAway"),
                volumeHomeDelta = D("VolumeHome").HasValue && PD("VolumeHome").HasValue
                    ? decimal.Round(D("VolumeHome")!.Value - PD("VolumeHome")!.Value, 0) : (decimal?)null,
                volumeDrawDelta = D("VolumeDraw").HasValue && PD("VolumeDraw").HasValue
                    ? decimal.Round(D("VolumeDraw")!.Value - PD("VolumeDraw")!.Value, 0) : (decimal?)null,
                volumeAwayDelta = D("VolumeAway").HasValue && PD("VolumeAway").HasValue
                    ? decimal.Round(D("VolumeAway")!.Value - PD("VolumeAway")!.Value, 0) : (decimal?)null
            });
        }

        if (spfPoints.Count == 0 && exPoints.Count == 0) return null;

        return new
        {
            matchId,
            spf = spfPoints,
            exchange = exPoints,
            // 最新一点的涨跌摘要（供列表页直接显示箭头）
            latestSpf = spfPoints.Count > 0 ? spfPoints[^1] : null,
            latestExchange = exPoints.Count > 0 ? exPoints[^1] : null,
            spfPointCount = spfPoints.Count,
            exchangePointCount = exPoints.Count
        };
    }

    /// <summary>单场必发成交时间序列 + 大额明细。</summary>
    public async Task<object?> GetExchangeTimelineAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var snaps = (await conn.QueryAsync(
            @"SELECT CapturedAt, MinutesToKick, PriceHome, PriceDraw, PriceAway,
                     VolumeHome, VolumeDraw, VolumeAway, TotalVolume,
                     RatioHome, RatioDraw, RatioAway, PnlHome, PnlDraw, PnlAway,
                     HotIdxHome, HotIdxDraw, HotIdxAway
              FROM dbo.FbExchange WHERE MatchId = @matchId ORDER BY CapturedAt", new { matchId })).ToList();

        var trades = (await conn.QueryAsync(
            @"SELECT TradeTime, Side, Direction, Volume, Ratio
              FROM dbo.FbExchangeTrade WHERE MatchId = @matchId ORDER BY TradeTime DESC",
            new { matchId })).ToList();

        if (snaps.Count == 0 && trades.Count == 0) return null;
        return new { matchId, snapshots = snaps, trades };
    }
}
