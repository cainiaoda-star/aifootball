using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 终版价回填（2026-11-10 加，用户拍板做「B：终版价口径」）。
///
/// 解决的是「战绩算出来的回报不可信」这个地基问题：三张台账存的都是**推荐生成那一刻**的价，
/// 而用户真正付出/真正拿到的是停售时的价。北单实测差 10~46%，稳胆赔率只有 1.0~1.75，
/// 差 3% 就能把「打平」算成「亏 5%」。
///
/// **不改 Odds**：那是当时页面上写的数字，改了就是篡改历史，且会让「推荐价 vs 终版价」
/// 这个对比本身失去意义。另加三列：
///   FinalOdds   终版价
///   FinalAt     该价对应的时刻
///   FinalSource 来源（jc-close / bd-resultsp），见 <see cref="FinalPriceSource"/>
///
/// 两条取价路径，都不打外部接口，只读本库：
///   · 竞彩（稳胆 / 空跑台账）：FbJcOddsHistory 里开赛前最后一次采样。
///     竞彩是固定奖金，停售前那一刻的价就是能买到的价。
///   · 北单：FbBdHistory 的开奖 SP（ResultSp）。北单按 SP 派奖，开奖 SP 就是实际赔付价。
///     只有命中的注有（没开出的选项没有 SP）——分母仍是全部已结算注数，未命中赔付本来就是 0，
///     所以算出来的回报是精确值，不是估计值。
///
/// 幂等：只写 FinalOdds IS NULL 的行，重复跑不会覆盖已回填的价。
/// </summary>
public sealed class FinalPriceRepository
{
    private readonly SqlConnectionFactory _factory;

    public FinalPriceRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>终版价的默认回填窗口（天）。够覆盖一轮「出榜 → 开赛 → 开奖」即可。</summary>
    public const int DefaultLookbackDays = 14;

    /// <summary>
    /// 建列（幂等）。逐列判断，不用「一次 ALTER 加三列」——
    /// 只要有一列已存在整条语句就会失败，部署到半途的库就再也加不上剩下的列。
    /// </summary>
    public async Task EnsureColumnsAsync(CancellationToken ct = default)
    {
        var sql = string.Concat(
            Col("FbSteadyPick", "FinalOdds", "DECIMAL(8,2) NULL"),
            Col("FbSteadyPick", "FinalAt", "DATETIME2(0) NULL"),
            Col("FbSteadyPick", "FinalSource", "NVARCHAR(12) NULL"),
            Col("FbPaperTrade", "FinalOdds", "DECIMAL(10,3) NULL"),
            Col("FbPaperTrade", "FinalAt", "DATETIME2(0) NULL"),
            Col("FbPaperTrade", "FinalSource", "NVARCHAR(12) NULL"),
            Col("FbBdSpfPick", "FinalOdds", "DECIMAL(8,2) NULL"),
            Col("FbBdSpfPick", "FinalAt", "DATETIME2(0) NULL"),
            Col("FbBdSpfPick", "FinalSource", "NVARCHAR(12) NULL"));

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    private static string Col(string table, string column, string type)
        => $"IF OBJECT_ID('dbo.{table}') IS NOT NULL AND COL_LENGTH('dbo.{table}','{column}') IS NULL "
         + $"ALTER TABLE dbo.{table} ADD {column} {type};";

    /// <summary>
    /// 回填终版价。返回 (稳胆, 空跑, 北单) 各自写入的注数。
    ///
    /// 三条 UPDATE 都只碰 FinalOdds IS NULL 的行，所以可以放心高频跑（作业里是每 20 分钟）。
    /// </summary>
    public async Task<(int Steady, int Paper, int Bd)> FinalizeAsync(
        int lookbackDays = DefaultLookbackDays, CancellationToken ct = default)
    {
        var days = Math.Clamp(lookbackDays, 1, 3650);
        await EnsureColumnsAsync(ct);
        await using var conn = await _factory.OpenAsync(ct);

        // ── 先自愈：清掉「不可能是有效报价」的终版价 ──
        // 2026-11-10 实测事故：竞彩停售那一刻采样里会出现 Odds=1.00 的占位（该档已封盘），
        // 首轮回填把 3 注写成了 1.00（奥萨苏纳 17.0→1.00、诺丁汉 8.35→1.00、霍芬海姆 4.05→1.00），
        // 价差 -94%~-75%，回报全被拖坏。竞彩任何一档都不可能报 1.00（最低 1.01），
        // 所以这里无条件把 ≤1 的终版价作废重取——比事后手工 UPDATE 靠得住。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.FbPaperTrade SET FinalOdds=NULL, FinalAt=NULL, FinalSource=NULL
               WHERE FinalOdds IS NOT NULL AND FinalOdds <= 1.0;
              UPDATE dbo.FbSteadyPick SET FinalOdds=NULL, FinalAt=NULL, FinalSource=NULL
               WHERE FinalOdds IS NOT NULL AND FinalOdds <= 1.0;",
            cancellationToken: ct));

        // ── 竞彩 · 稳胆 ──
        // 用 MatchId 关联，**不许用 MatchNo**：竞彩场次号（周日001）每周重号，
        // 按 MatchNo 关联会在本场缺采样时抓到上一周同号场次的价，而且完全无声。
        var steady = await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE p
                 SET FinalOdds = x.Odds, FinalAt = x.CapturedAt, FinalSource = @src
                FROM dbo.FbSteadyPick p
               CROSS APPLY (
                    SELECT TOP 1 h.Odds, h.CapturedAt
                      FROM dbo.FbJcOddsHistory h
                     WHERE h.MatchId = p.MatchId
                       AND h.PlayType = 'SPF'
                       AND h.OptionCode = CAST(p.Side AS NVARCHAR(4))
                       AND h.Odds > 1.0
                       AND h.CapturedAt <= p.MatchTime
                     ORDER BY h.CapturedAt DESC) x
               WHERE p.FinalOdds IS NULL
                 AND p.MatchTime IS NOT NULL
                 AND p.MatchTime <= SYSDATETIME()
                 AND p.PickDate >= DATEADD(day, -@days, CAST(SYSDATETIME() AS DATE))",
            new { days, src = FinalPriceSource.JcCloseSnapshot }, cancellationToken: ct));

        // ── 竞彩 · 空跑台账 ──
        // PlayType/OptionCode 与 FbJcOddsHistory 同源（SPF/NSPF/BQ/BF/JQ + 0/1/3、0-1、1:0、2 …），
        // 直接等值关联即可。撤注的行也回填：撤注 = 没下这笔，不影响回报，但明细页要能对比。
        var paper = await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE t
                 SET FinalOdds = x.Odds, FinalAt = x.CapturedAt, FinalSource = @src
                FROM dbo.FbPaperTrade t
               CROSS APPLY (
                    SELECT TOP 1 h.Odds, h.CapturedAt
                      FROM dbo.FbJcOddsHistory h
                     WHERE h.MatchId = t.MatchId
                       AND h.PlayType = t.PlayType
                       AND h.OptionCode = t.OptionCode
                       AND h.Odds > 1.0
                       AND h.CapturedAt <= t.MatchTime
                     ORDER BY h.CapturedAt DESC) x
               WHERE t.FinalOdds IS NULL
                 AND t.MatchTime IS NOT NULL
                 AND t.MatchTime <= SYSDATETIME()
                 AND t.TradeDate >= DATEADD(day, -@days, CAST(SYSDATETIME() AS DATE))",
            new { days, src = FinalPriceSource.JcCloseSnapshot }, cancellationToken: ct));

        // ── 北单 ──
        // 只有「这一注押的方向就是开出的方向」时才有开奖 SP，也就是只有命中的注能回填。
        // 拿它算回报是精确的：未命中赔付 0，不缺分子。
        // ResultCode 用 FbBdHistory 自己的（那是按北单让球数算出来的结果），
        // 与台账的 HandicapSide 同一套编码（3 让胜 / 1 让平 / 0 让负）。
        var bd = await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE p
                 SET FinalOdds = x.ResultSp, FinalAt = x.CapturedAt, FinalSource = @src
                FROM dbo.FbBdSpfPick p
               CROSS APPLY (
                    SELECT TOP 1 b.ResultSp, b.CapturedAt
                      FROM dbo.FbBdHistory b
                     WHERE b.MatchId = p.MatchId
                       AND b.ResultSp IS NOT NULL
                       AND b.ResultCode = CAST(ISNULL(p.HandicapSide, p.Side) AS CHAR(1))
                     ORDER BY b.CapturedAt DESC) x
               WHERE p.FinalOdds IS NULL
                 AND p.Hit = 1
                 AND p.PickDate >= DATEADD(day, -@days, CAST(SYSDATETIME() AS DATE))",
            new { days, src = FinalPriceSource.BdResultSp }, cancellationToken: ct));

        return (steady, paper, bd);
    }
}
