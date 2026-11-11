using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 北单胜平负（不让球）推荐台账（dbo.FbBdSpfPick，2026-11-06 加）。
/// 「今日北单推荐」板块的落库快照：两组各 5 注（确定性正路 / 反向价值），
/// 赛后按赛果判中，复盘「北单不让球这套技能到底赚不赚」。
/// </summary>
public sealed class BdSpfPickRepository
{
    private readonly SqlConnectionFactory _factory;

    public BdSpfPickRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>确保台账表存在（首次部署时建表）。幂等。</summary>
    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID('dbo.FbBdSpfPick') IS NULL
              CREATE TABLE dbo.FbBdSpfPick
              (
                  PickId        BIGINT IDENTITY(1,1) PRIMARY KEY,
                  PickDate      DATE NOT NULL,
                  Kind          NVARCHAR(10) NOT NULL,     -- steady / reverse
                  MatchId       BIGINT NOT NULL,
                  MatchNo       NVARCHAR(20) NOT NULL,
                  LeagueName    NVARCHAR(80) NULL,
                  HomeTeam      NVARCHAR(80) NULL,
                  AwayTeam      NVARCHAR(80) NULL,
                  MatchTime     DATETIME2(0) NULL,
                  Side          CHAR(1) NOT NULL,          -- 3 主胜 / 1 平 / 0 客胜
                  SideName      NVARCHAR(10) NULL,
                  Odds          DECIMAL(8,2) NULL,
                  Handicap      DECIMAL(4,1) NULL,          -- 让球数（rangqiuNum，主队让球）
                  HandicapSide  CHAR(1) NULL,               -- 让球盘方向 3 让胜/1 让平/0 让负
                  HandicapSideName NVARCHAR(10) NULL,
                  HandicapProbability DECIMAL(6,2) NULL,    -- 让球方向打出概率 0~100
                  HandicapOdds  DECIMAL(8,2) NULL,          -- 让球盘 SP（推荐时点）
                  HandicapModelProb  DECIMAL(6,2) NULL,     -- 模型对该方向的概率，2026-11-11 加
                  HandicapMarketProb DECIMAL(6,2) NULL,     -- 市场（去水）对该方向的概率，同上
                  FinalOdds     DECIMAL(8,2) NULL,          -- 开奖 SP（终版价，只有命中的注有），2026-11-10 加
                  FinalAt       DATETIME2(0) NULL,
                  FinalSource   NVARCHAR(12) NULL,
                  HitProbability DECIMAL(6,2) NULL,
                  MarketProb    DECIMAL(6,2) NULL,
                  RatioHome     DECIMAL(6,2) NULL,
                  RatioAway     DECIMAL(6,2) NULL,
                  PnlIdxHome    DECIMAL(6,2) NULL,
                  PnlIdxAway    DECIMAL(6,2) NULL,
                  Reason        NVARCHAR(500) NULL,
                  ActualScore   NVARCHAR(20) NULL,
                  Hit           BIT NULL,
                  SettledAt     DATETIME2(0) NULL,
                  Confidence    INT NULL,
                  CreatedAt     DATETIME2(0) NOT NULL DEFAULT SYSDATETIME()
              );", cancellationToken: ct));

        // 老表兜底：给已有表补 Confidence 列（幂等）。
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID('dbo.FbBdSpfPick') IS NOT NULL
                AND COL_LENGTH('dbo.FbBdSpfPick','Confidence') IS NULL
              ALTER TABLE dbo.FbBdSpfPick ADD Confidence INT NULL;", cancellationToken: ct));

        // 老表兜底：给已有表补让球盘列（幂等，逐列判断）。
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID('dbo.FbBdSpfPick') IS NOT NULL
              BEGIN
                IF COL_LENGTH('dbo.FbBdSpfPick','Handicap') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD Handicap DECIMAL(4,1) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','HandicapSide') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD HandicapSide CHAR(1) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','HandicapSideName') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD HandicapSideName NVARCHAR(10) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','HandicapProbability') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD HandicapProbability DECIMAL(6,2) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','HandicapOdds') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD HandicapOdds DECIMAL(8,2) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','HandicapModelProb') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD HandicapModelProb DECIMAL(6,2) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','HandicapMarketProb') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD HandicapMarketProb DECIMAL(6,2) NULL;
              END;", cancellationToken: ct));

        // 老表兜底：终版价三列（2026-11-10 加，逐列判断，幂等）。
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID('dbo.FbBdSpfPick') IS NOT NULL
              BEGIN
                IF COL_LENGTH('dbo.FbBdSpfPick','FinalOdds') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD FinalOdds DECIMAL(8,2) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','FinalAt') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD FinalAt DATETIME2(0) NULL;
                IF COL_LENGTH('dbo.FbBdSpfPick','FinalSource') IS NULL
                  ALTER TABLE dbo.FbBdSpfPick ADD FinalSource NVARCHAR(12) NULL;
              END;", cancellationToken: ct));
    }

    /// <summary>重写当日北单推荐（先删当日未结算行再写）。</summary>
    public async Task<int> SaveDayAsync(DateTime pickDate, BdSpfPickDay day, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.FbBdSpfPick WHERE PickDate=@d AND Hit IS NULL AND SettledAt IS NULL",
            new { d }, tx, cancellationToken: ct));

        var rows = 0;
        foreach (var (kind, list) in new[] { ("steady", day.Steady), ("reverse", day.Reverse) })
        foreach (var p in list)
        {
            if (p.MatchId <= 0) continue;
            rows += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbBdSpfPick
                    (PickDate, Kind, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Side, SideName, Odds, Handicap, HandicapSide, HandicapSideName,
                     HandicapProbability, HandicapOdds, HandicapModelProb, HandicapMarketProb,
                     FinalOdds, FinalAt, FinalSource, HitProbability, MarketProb,
                     RatioHome, RatioAway, PnlIdxHome, PnlIdxAway, Reason, Confidence)
                  VALUES (@d, @kind, @mid, @no, @lg, @home, @away, @time,
                          @side, @sideName, @odds, @hcp, @hside, @hsideName,
                          @hprob, @hodds, @hmodel, @hmarket,
                          @fo, @fa, @fs, @hit, @market,
                          @rh, @ra, @ph, @pa, @reason, @conf)",
                new
                {
                    d, kind, mid = p.MatchId, no = p.MatchNo, lg = p.LeagueName,
                    home = p.HomeTeam, away = p.AwayTeam, time = p.MatchTime,
                    side = p.Side.ToString(), sideName = p.SideName, odds = p.Odds,
                    hcp = p.Handicap, hside = p.HandicapSide?.ToString(), hsideName = p.HandicapSideName,
                    hprob = p.HandicapProbability, hodds = p.HandicapOdds,
                    // 2026-11-11 加：模型概率此前只在内存里算过、从没落库，
                    // 于是「这套推荐比盘口准不准」永远无法回答（方向本身就是按市场概率选的）。
                    hmodel = p.HandicapModelProb, hmarket = p.HandicapMarketProb,
                    // 2026-11-10 修：这三列在列名里、VALUES 里却没有，Dapper 直接把
                    // 「列的数目大于 VALUES 子句」抛回来，北单从 20:41 起一条都写不进库。
                    // 加终版价三列时只改了 INSERT 列表，没同步参数 —— 这种错编译期抓不到。
                    fo = p.FinalOdds, fa = p.FinalAt, fs = p.FinalSource,
                    hit = p.HitProbability, market = p.MarketProb,
                    rh = p.RatioHome, ra = p.RatioAway, ph = p.PnlIdxHome, pa = p.PnlIdxAway,
                    reason = p.Reason, conf = p.Confidence
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// 按赛果结算北单让球胜平负台账（2026-11-09 加）。
    ///
    /// 在此之前这张表**从来没有结算过**：46 行 Hit 全是 NULL，所以「北单成功率多少」
    /// 只能靠临时写 SQL 回算，系统自己不知道。缺的就是这一段。
    ///
    /// 判据：
    ///  · 让球后主队净胜 = 主队进球 − 客队进球 + 让球数（Handicap 负=主让球、正=主受让）。
    ///    北单让球数挂在主队，所以这里直接用台账自己的 Handicap，**不要用 FbMatch.Handicap**
    ///    （那是竞彩盘，11-07 的对账报告踩过这个坑）。
    ///  · &gt;0 让胜(3) / =0 让平(1，走盘) / &lt;0 让负(0)。
    ///  · 完场不信 Status（重采会把 3 洗回 0）：比分齐全 && (Status=3 || 开赛已过 150 分钟)。
    ///  · 比分或让球数缺失的一律不结算，宁缺勿假——北单腰斩/取消的场次本来就不该算进命中率。
    ///
    /// 幂等：只动 Hit IS NULL 的行，重复跑不会改写已结算结果。
    /// </summary>
    public async Task<int> SettleAsync(int lookbackDays = 7, CancellationToken ct = default)
    {
        var lookback = Math.Clamp(lookbackDays, 1, 90);
        await using var conn = await _factory.OpenAsync(ct);
        var pending = (await conn.QueryAsync<PendingRow>(new CommandDefinition(
            @"SELECT p.PickId,
                     p.Side,
                     p.HandicapSide,
                     p.Handicap,
                     CAST(m.HomeScore AS INT) AS HomeScore,
                     CAST(m.AwayScore AS INT) AS AwayScore
                FROM dbo.FbBdSpfPick p
                JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
               WHERE p.Hit IS NULL
                 AND p.Handicap IS NOT NULL
                 AND p.PickDate >= DATEADD(day, -@lookback, CAST(SYSDATETIME() AS DATE))
                 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                 AND (m.Status = 3 OR p.MatchTime <= DATEADD(minute, -150, SYSDATETIME()))",
            new { lookback }, cancellationToken: ct))).ToList();
        if (pending.Count == 0) return 0;

        await using var tx = await conn.BeginTransactionAsync(ct);
        var settled = 0;
        foreach (var r in pending)
        {
            var side = !string.IsNullOrEmpty(r.HandicapSide) ? r.HandicapSide![0] : r.Side[0];
            var hit = BdSpfSettlement.Judge(side, r.HomeScore, r.AwayScore, r.Handicap);
            settled += await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbBdSpfPick
                     SET ActualScore = @score, Hit = @hit, SettledAt = SYSDATETIME()
                   WHERE PickId = @id AND Hit IS NULL",
                new
                {
                    id = r.PickId,
                    score = $"{r.HomeScore}:{r.AwayScore}",
                    hit = hit ? 1 : 0
                }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return settled;
    }

    /// <summary>
    /// 北单战绩（近 N 天，按 steady / reverse 分组）。
    ///
    /// ★ 口径：分母只算**已结算**注数（Hit 非空）。
    /// ★ 两栏（2026-11-10 加）：
    ///   · <c>Roi</c> = 命中注**推荐时点 SP** 合计 ÷ 注数。实购终版 SP 与它常有 10~46% 差距，
    ///     只能看方向，不能当账本（场16 让胜 2.91→实购 4.25、场36 让负 2.07→3.74）。
    ///   · <c>RoiFinal</c> = 命中注**开奖 SP**（FbBdHistory.ResultSp）合计 ÷ 注数。
    ///     北单按 SP 派奖，这是**真实赔付**，跟单能不能赚只看这一栏。
    ///     只有命中的注有开奖 SP（没开出的选项没有 SP），未命中赔付本来就是 0，
    ///     分母仍是全部已结算注数，所以这个回报是精确值而非估计值。
    /// </summary>
    public async Task<BdSpfRecord> GetRecordAsync(int days = 30, CancellationToken ct = default)
    {
        var n = Math.Clamp(days, 1, 365);
        await using var conn = await _factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<RecordRow>(new CommandDefinition(
            @"SELECT Kind,
                     COUNT(CASE WHEN Hit IS NOT NULL THEN 1 END) AS Settled,
                     SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) AS Hits,
                     SUM(CASE WHEN Hit IS NULL THEN 1 ELSE 0 END) AS Pending,
                     CAST(AVG(CASE WHEN Hit IS NOT NULL THEN Odds END) AS DECIMAL(8,2)) AS AvgOdds,
                     CAST(SUM(CASE WHEN Hit = 1 THEN ISNULL(Odds, 0) ELSE 0 END) AS DECIMAL(10,2)) AS HitOddsSum,
                     SUM(CASE WHEN Hit = 1 AND FinalOdds IS NOT NULL THEN 1 ELSE 0 END) AS FinalHits,
                     CAST(SUM(CASE WHEN Hit = 1 THEN ISNULL(FinalOdds, 0) ELSE 0 END) AS DECIMAL(10,2)) AS FinalOddsSum,
                     MIN(PickDate) AS FromDate, MAX(PickDate) AS ToDate
              FROM dbo.FbBdSpfPick
              WHERE PickDate >= DATEADD(day, -@n, CAST(SYSDATETIME() AS DATE))
              GROUP BY Kind",
            new { n }, cancellationToken: ct))).ToList();

        var record = new BdSpfRecord { Days = n };
        foreach (var r in rows)
        {
            var g = new BdSpfRecordGroup
            {
                Kind = r.Kind,
                Settled = r.Settled,
                Hits = r.Hits,
                Pending = r.Pending,
                AvgOdds = r.AvgOdds,
                HitPct = r.Settled > 0 ? Math.Round(r.Hits * 100m / r.Settled, 1) : 0m,
                Roi = FinalPriceMath.Roi(r.Settled, r.HitOddsSum),
                RoiFinal = FinalPriceMath.Roi(r.Settled, r.FinalOddsSum),
                FinalHits = r.FinalHits,
                FinalCoverage = FinalPriceMath.HitCoverage(r.Hits, r.FinalHits)
            };
            if (r.Kind == "reverse") record.Reverse = g; else record.Steady = g;
            record.From ??= r.FromDate;
            if (r.FromDate is DateTime f && (record.From is null || f < record.From)) record.From = f;
            if (r.ToDate is DateTime t && (record.To is null || t > record.To)) record.To = t;
        }

        var sum = new BdSpfRecordGroup { Kind = "total" };
        foreach (var g in new[] { record.Steady, record.Reverse })
        {
            if (g is null) continue;
            sum.Settled += g.Settled;
            sum.Hits += g.Hits;
            sum.Pending += g.Pending;
            sum.FinalHits += g.FinalHits;
        }
        var oddsSum = rows.Sum(r => r.HitOddsSum);
        var finalOddsSum = rows.Sum(r => r.FinalOddsSum);
        sum.HitPct = sum.Settled > 0 ? Math.Round(sum.Hits * 100m / sum.Settled, 1) : 0m;
        sum.Roi = FinalPriceMath.Roi(sum.Settled, oddsSum);
        sum.RoiFinal = FinalPriceMath.Roi(sum.Settled, finalOddsSum);
        sum.FinalCoverage = FinalPriceMath.HitCoverage(sum.Hits, sum.FinalHits);
        sum.AvgOdds = sum.Settled > 0
            ? Math.Round(rows.Where(r => r.AvgOdds is not null)
                             .Select(r => r.AvgOdds!.Value * r.Settled).Sum() / sum.Settled, 2)
            : null;
        record.Total = sum;
        record.Rows = (await conn.QueryAsync<BdSpfPickRecordItem>(new CommandDefinition(
            @"SELECT PickDate, Kind, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Handicap, SideName, Odds, FinalOdds, ActualScore, Hit
              FROM dbo.FbBdSpfPick
              WHERE PickDate >= DATEADD(day, -@n, CAST(SYSDATETIME() AS DATE))
              ORDER BY PickDate DESC, MatchTime DESC",
            new { n }, cancellationToken: ct))).ToList();
        return record;
    }

    private sealed class PendingRow
    {
        public long PickId { get; set; }
        public string Side { get; set; } = "3";
        public string? HandicapSide { get; set; }
        public decimal Handicap { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    private sealed class RecordRow
    {
        public string Kind { get; set; } = "steady";
        public int Settled { get; set; }
        public int Hits { get; set; }
        public int Pending { get; set; }
        public decimal? AvgOdds { get; set; }
        public decimal HitOddsSum { get; set; }
        /// <summary>命中注里拿得到开奖 SP 的注数（2026-11-10 加）。</summary>
        public int FinalHits { get; set; }
        /// <summary>命中注开奖 SP 合计。</summary>
        public decimal FinalOddsSum { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
    }

    /// <summary>读当天北单推荐（两组）。北单期次横跨午夜：指定日期无数据时回退到最近一个有数据的日期。</summary>
    public async Task<BdSpfPickDay?> GetDayAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<BdSpfPickRow>(new CommandDefinition(
            @"SELECT Kind, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Side, SideName, Odds, Handicap, HandicapSide, HandicapSideName,
                     HandicapProbability, HandicapOdds, FinalOdds, FinalAt, FinalSource, HitProbability, MarketProb,
                     RatioHome, RatioAway, PnlIdxHome, PnlIdxAway, Reason, Confidence,
                     ActualScore, Hit, SettledAt
              FROM dbo.FbBdSpfPick WHERE PickDate=@d
              ORDER BY CASE Kind WHEN 'steady' THEN 0 ELSE 1 END,
                       CASE WHEN Kind='steady' THEN TRY_CAST(MatchNo AS INT) END ASC,
                       ISNULL(Confidence,0) DESC",
            new { d }, cancellationToken: ct))).ToList();

        // 午夜回退：北单场次开到次日凌晨，过了 00:00 按"今天"查会落空，
        // 此时回退到最近一个有数据的 PickDate（昨晚落库的那批就是现在在踢的这期）。
        if (rows.Count == 0)
        {
            var last = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(PickDate) FROM dbo.FbBdSpfPick WHERE PickDate < @d",
                new { d }, cancellationToken: ct));
            if (last is DateTime ld)
            {
                d = ld.Date;
                rows = (await conn.QueryAsync<BdSpfPickRow>(new CommandDefinition(
                    @"SELECT Kind, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                             Side, SideName, Odds, Handicap, HandicapSide, HandicapSideName,
                             HandicapProbability, HandicapOdds, FinalOdds, FinalAt, FinalSource, HitProbability, MarketProb,
                             RatioHome, RatioAway, PnlIdxHome, PnlIdxAway, Reason, Confidence,
                             ActualScore, Hit, SettledAt
                      FROM dbo.FbBdSpfPick WHERE PickDate=@d
                      ORDER BY CASE Kind WHEN 'steady' THEN 0 ELSE 1 END,
                               CASE WHEN Kind='steady' THEN TRY_CAST(MatchNo AS INT) END ASC,
                               ISNULL(Confidence,0) DESC",
                    new { d }, cancellationToken: ct))).ToList();
            }
        }

        if (rows.Count == 0) return null;

        var day = new BdSpfPickDay { Day = d };
        foreach (var r in rows)
        {
            var pick = new BdSpfPick
            {
                MatchId = r.MatchId,
                MatchNo = r.MatchNo,
                LeagueName = r.LeagueName,
                HomeTeam = r.HomeTeam ?? "",
                AwayTeam = r.AwayTeam ?? "",
                MatchTime = r.MatchTime,
                Side = r.Side.Length > 0 ? r.Side[0] : '3',
                SideName = r.SideName ?? "",
                Odds = r.Odds,
                Handicap = r.Handicap,
                HandicapSide = string.IsNullOrEmpty(r.HandicapSide) ? null : r.HandicapSide[0],
                HandicapSideName = r.HandicapSideName,
                HandicapProbability = r.HandicapProbability,
                HandicapOdds = r.HandicapOdds,
                FinalOdds = r.FinalOdds,
                FinalAt = r.FinalAt,
                FinalSource = r.FinalSource,
                HitProbability = r.HitProbability,
                MarketProb = r.MarketProb,
                RatioHome = r.RatioHome,
                RatioAway = r.RatioAway,
                PnlIdxHome = r.PnlIdxHome,
                PnlIdxAway = r.PnlIdxAway,
                Kind = r.Kind == "reverse" ? "reverse" : "steady",
                Confidence = r.Confidence ?? 0,
                Reason = r.Reason ?? ""
            };
            (r.Kind == "reverse" ? day.Reverse : day.Steady).Add(pick);
        }
        return day;
    }

    private sealed class BdSpfPickRow
    {
        public string Kind { get; set; } = "steady";
        public long MatchId { get; set; }
        public string MatchNo { get; set; } = "";
        public string? LeagueName { get; set; }
        public string? HomeTeam { get; set; }
        public string? AwayTeam { get; set; }
        public DateTime? MatchTime { get; set; }
        public string Side { get; set; } = "3";
        public string? SideName { get; set; }
        public decimal? Odds { get; set; }
        public decimal? Handicap { get; set; }
        public string? HandicapSide { get; set; }
        public string? HandicapSideName { get; set; }
        public decimal? HandicapProbability { get; set; }
        public decimal? HandicapOdds { get; set; }
        public decimal? FinalOdds { get; set; }
        public DateTime? FinalAt { get; set; }
        public string? FinalSource { get; set; }
        public decimal? HitProbability { get; set; }
        public decimal? MarketProb { get; set; }
        public decimal? RatioHome { get; set; }
        public decimal? RatioAway { get; set; }
        public decimal? PnlIdxHome { get; set; }
        public decimal? PnlIdxAway { get; set; }
        public string? Reason { get; set; }
        public int? Confidence { get; set; }
        public string? ActualScore { get; set; }
        public bool? Hit { get; set; }
        public DateTime? SettledAt { get; set; }
    }
}
