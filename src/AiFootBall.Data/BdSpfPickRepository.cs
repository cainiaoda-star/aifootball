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
                  HandicapOdds  DECIMAL(8,2) NULL,          -- 让球盘 SP
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
                     HandicapProbability, HandicapOdds, HitProbability, MarketProb,
                     RatioHome, RatioAway, PnlIdxHome, PnlIdxAway, Reason, Confidence)
                  VALUES (@d, @kind, @mid, @no, @lg, @home, @away, @time,
                          @side, @sideName, @odds, @hcp, @hside, @hsideName,
                          @hprob, @hodds, @hit, @market,
                          @rh, @ra, @ph, @pa, @reason, @conf)",
                new
                {
                    d, kind, mid = p.MatchId, no = p.MatchNo, lg = p.LeagueName,
                    home = p.HomeTeam, away = p.AwayTeam, time = p.MatchTime,
                    side = p.Side.ToString(), sideName = p.SideName, odds = p.Odds,
                    hcp = p.Handicap, hside = p.HandicapSide?.ToString(), hsideName = p.HandicapSideName,
                    hprob = p.HandicapProbability, hodds = p.HandicapOdds,
                    hit = p.HitProbability, market = p.MarketProb,
                    rh = p.RatioHome, ra = p.RatioAway, ph = p.PnlIdxHome, pa = p.PnlIdxAway,
                    reason = p.Reason, conf = p.Confidence
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>读当天北单推荐（两组）。北单期次横跨午夜：指定日期无数据时回退到最近一个有数据的日期。</summary>
    public async Task<BdSpfPickDay?> GetDayAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<BdSpfPickRow>(new CommandDefinition(
            @"SELECT Kind, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Side, SideName, Odds, Handicap, HandicapSide, HandicapSideName,
                     HandicapProbability, HandicapOdds, HitProbability, MarketProb,
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
                             HandicapProbability, HandicapOdds, HitProbability, MarketProb,
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
