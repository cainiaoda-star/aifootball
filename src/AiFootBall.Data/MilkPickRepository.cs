using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 大奶推荐台账（dbo.FbMilkPick）。出票时落库快照，赛后按比分/半场比分/总进球判中。
/// 这是「大奶到底赚不赚」唯一的账——页面榜是排序参考，台账才是结算凭证。
/// </summary>
public sealed class MilkPickRepository
{
    private readonly SqlConnectionFactory _factory;

    public MilkPickRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>重写当日未结算的大奶推荐；已结算的行（含取消终态）不动（重算不冲掉赛果）。</summary>
    public async Task<int> SaveDayAsync(DateTime pickDate, IReadOnlyList<MilkPick> picks, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.FbMilkPick WHERE PickDate=@d AND Hit IS NULL AND SettledAt IS NULL",
            new { d }, tx, cancellationToken: ct));

        var rows = 0;
        foreach (var p in picks)
        {
            if (p.MatchId is not long mid) continue;
            rows += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbMilkPick
                    (PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev)
                  VALUES (@d, @mid, @no, @lg, @home, @away, @time,
                          @play, @code, @name, @odds, @prob, @ev)",
                new
                {
                    d, mid, no = p.MatchNo, lg = p.LeagueName, home = p.HomeTeam, away = p.AwayTeam,
                    time = p.MatchTime, play = p.PlayType, code = p.OptionCode, name = p.OptionName,
                    odds = p.Odds, prob = p.ModelProbability, ev = p.Ev
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// 重写当日方向闸诊断（2026-11-05 加）。诊断是「这场为什么没发」的唯一凭证，
    /// 和榜同一批数据算出来，就必须和榜同一时刻落库——赔率快照一滚，赛后谁也还原不出当时的市场方向。
    /// 表没建时抛给调用方，由上层决定吞不吞（出票不能因为台账写不进去就停）。
    /// </summary>
    public async Task<int> SaveGateAsync(
        DateTime gateDate, IReadOnlyList<MilkDirectionDiagnosis> gates, CancellationToken ct = default)
    {
        var d = gateDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.FbMilkGate WHERE GateDate=@d", new { d }, tx, cancellationToken: ct));

        var rows = 0;
        foreach (var g in gates)
        {
            if (g.MatchId is not long mid) continue;
            rows += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbMilkGate
                    (GateDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     ModelDirection, ModelHomePct, ModelDrawPct, ModelAwayPct, PickDirection,
                     MarketDirection, MarketSource, MarketHomePct, MarketDrawPct, MarketAwayPct,
                     AsianDirection, AsianOpen, AsianClose, ExchangeDirection, RatioHome, RatioAway,
                     Verdict, AlertCode, AlertNote, BlockedPicks, BlockedBest, BlockedBestProbability)
                  VALUES (@d, @mid, @no, @lg, @home, @away, @time,
                          @mDir, @mh, @md2, @ma, @pDir,
                          @kDir, @src, @kh, @kd, @ka,
                          @aDir, @aOpen, @aClose, @eDir, @rh, @ra,
                          @verdict, @alert, @note, @blocked, @best, @bestProb)",
                new
                {
                    d, mid, no = g.MatchNo, lg = g.LeagueName, home = g.HomeTeam, away = g.AwayTeam,
                    time = g.MatchTime,
                    mDir = g.ModelDirection?.ToString(), mh = g.ModelHomePct, md2 = g.ModelDrawPct, ma = g.ModelAwayPct,
                    pDir = g.PickDirection?.ToString(),
                    kDir = g.MarketDirection?.ToString(), src = g.MarketSource,
                    kh = g.MarketHomePct, kd = g.MarketDrawPct, ka = g.MarketAwayPct,
                    aDir = g.AsianDirection?.ToString(), aOpen = g.AsianOpen, aClose = g.AsianClose,
                    eDir = g.ExchangeDirection?.ToString(), rh = g.RatioHome, ra = g.RatioAway,
                    verdict = g.Verdict, alert = g.AlertCode, note = g.AlertNote,
                    blocked = g.BlockedPicks, best = g.BlockedBest, bestProb = g.BlockedBestProbability
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>当日方向闸台账。背离的排前面，其余按开赛时间——页面上先看被挡掉的。</summary>
    public async Task<List<MilkDirectionDiagnosis>> GetGateAsync(DateTime gateDate, CancellationToken ct = default)
    {
        var d = gateDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<MilkDirectionDiagnosis>(new CommandDefinition(
            @"SELECT MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     ModelDirection, ModelHomePct, ModelDrawPct, ModelAwayPct, PickDirection,
                     MarketDirection, MarketSource, MarketHomePct, MarketDrawPct, MarketAwayPct,
                     AsianDirection, AsianOpen, AsianClose, ExchangeDirection, RatioHome, RatioAway,
                     Verdict, AlertCode, AlertNote, BlockedPicks, BlockedBest, BlockedBestProbability
              FROM dbo.FbMilkGate
              WHERE GateDate=@d
              ORDER BY CASE WHEN Verdict='diverge' THEN 0 ELSE 1 END, MatchTime, MatchNo",
            new { d }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// 当日大奶榜（页面展示）。
    ///
    /// 排序口径 = 命中率（模型概率）优先，EV 只做次序。
    /// 2026-11-04 从 `Ev DESC` 改过来：EV = 概率 × 赔率，赔率越离谱越占便宜，
    /// 会把 1.9% 的 3:0@80 顶到 17.8% 的胜胜前面——榜单第一行变成废票，
    /// 而这张榜回答的是「哪注最可能中」。EV 不丢，前端只给最高的一注打角标。
    /// </summary>
    public async Task<List<MilkPickRow>> GetDayAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<MilkPickRow>(new CommandDefinition(
            @"SELECT PickId, PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev,
                     ActualScore, ActualHalfScore, ActualGoals, Hit, SettledAt
              FROM dbo.FbMilkPick WHERE PickDate=@d
              ORDER BY ModelProbability DESC, Ev DESC", new { d }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// 近 N 天大奶战绩报表：按玩法分组 + 整体 + 未结算数 + 热门选项。
    /// 只统计已结算（Hit 非空）的行；未结算的单独报数，免得「刚发的榜」把命中率稀释成 0。
    /// </summary>
    public async Task<MilkStatsReport> GetReportAsync(int days, CancellationToken ct = default)
    {
        var span = Math.Max(1, days);
        var from = DateTime.Today.AddDays(-(span - 1));
        await using var conn = await _factory.OpenAsync(ct);

        const string groupSql =
            @"SELECT PlayType,
                     COUNT(*) AS Total,
                     SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) AS Hit,
                     ISNULL(SUM(Odds), 0) AS TotalOdds,
                     ISNULL(SUM(CASE WHEN Hit = 1 THEN Odds ELSE 0 END), 0) AS HitOdds,
                     CAST(ISNULL(AVG(Odds), 0) AS DECIMAL(8,2)) AS AvgOdds,
                     CAST(ISNULL(AVG(ModelProbability), 0) AS DECIMAL(6,2)) AS AvgProbability,
                     CAST(ISNULL(AVG(Ev), 0) AS DECIMAL(8,3)) AS AvgEv
              FROM dbo.FbMilkPick
              WHERE PickDate >= @from AND Hit IS NOT NULL";

        var plays = (await conn.QueryAsync<MilkPlayStat>(new CommandDefinition(
            groupSql + " GROUP BY PlayType ORDER BY PlayType", new { from }, cancellationToken: ct))).ToList();

        var overall = await conn.QuerySingleOrDefaultAsync<MilkPlayStat>(new CommandDefinition(
            groupSql.Replace("SELECT PlayType,", "SELECT 'ALL' AS PlayType,"), new { from }, cancellationToken: ct));

        var pending = await conn.QuerySingleAsync<int>(new CommandDefinition(
            // 取消场次写了 SettledAt 终态（Hit 仍 NULL、ActualScore='取消'），不算 pending
            "SELECT COUNT(*) FROM dbo.FbMilkPick WHERE PickDate >= @from AND Hit IS NULL AND SettledAt IS NULL",
            new { from }, cancellationToken: ct));

        var top = (await conn.QueryAsync<MilkOptionStat>(new CommandDefinition(
            @"SELECT TOP 12 PlayType, OptionCode,
                     COUNT(*) AS Total,
                     SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) AS Hit,
                     CAST(ISNULL(AVG(Odds), 0) AS DECIMAL(8,2)) AS AvgOdds
              FROM dbo.FbMilkPick
              WHERE PickDate >= @from AND Hit IS NOT NULL
              GROUP BY PlayType, OptionCode
              HAVING COUNT(*) >= 3
              ORDER BY COUNT(*) DESC, SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) DESC",
            new { from }, cancellationToken: ct))).ToList();

        return new MilkStatsReport
        {
            Days = span,
            From = from,
            Overall = overall ?? new MilkPlayStat { PlayType = "ALL" },
            Plays = plays,
            Pending = pending,
            TopOptions = top
        };
    }

    /// <summary>
    /// 结算未判定的大奶推荐。半全场需要半场比分；FbMatch 没采到半场比分的场留着下轮。
    /// 返回本次判定条数。
    /// </summary>
    public async Task<int> SettleAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var pending = (await conn.QueryAsync<MilkPendingRow>(new CommandDefinition(
            @"SELECT p.PickId, p.PlayType, p.OptionCode,
                     m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore
              FROM dbo.FbMilkPick p
              JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
              WHERE p.Hit IS NULL AND m.Status = 3
                AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL",
            new { }, cancellationToken: ct))).ToList();

        var settled = 0;
        foreach (var row in pending)
        {
            bool? hit;
            if (row.PlayType == JcPlayTypes.Bq)
            {
                if (row.HalfHomeScore is not int hh || row.HalfAwayScore is not int ha) continue;
                hit = MilkPickMath.IsHit(row.PlayType, row.OptionCode,
                    row.HomeScore!.Value, row.AwayScore!.Value, hh, ha);
            }
            else
            {
                hit = MilkPickMath.IsHit(row.PlayType, row.OptionCode,
                    row.HomeScore!.Value, row.AwayScore!.Value, null, null);
            }

            var score = $"{row.HomeScore}:{row.AwayScore}";
            var half = row.HalfHomeScore is int h1 && row.HalfAwayScore is int h2 ? $"{h1}:{h2}" : null;
            var goals = row.HomeScore + row.AwayScore;

            settled += await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbMilkPick
                  SET Hit=@hit, ActualScore=@score, ActualHalfScore=@half, ActualGoals=@goals,
                      SettledAt=SYSDATETIME()
                  WHERE PickId=@id", new { hit, score, half, goals, id = row.PickId }, cancellationToken: ct));
        }

        // 取消场次（Status=2）永远等不到赛果：写死「取消」终态退出 pending。
        // Hit 保持 NULL 不进战绩分母，ActualScore='取消' 当标记。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE p SET p.ActualScore = N'取消', p.SettledAt = SYSDATETIME()
              FROM dbo.FbMilkPick p
              JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
              WHERE p.Hit IS NULL AND p.SettledAt IS NULL AND m.Status = 2",
            new { }, cancellationToken: ct));

        return settled;
    }

    /// <summary>
    /// 临场转向告警（2026-11-05 加）：开赛前持续盯着「已发出的大奶注」，
    /// 一旦市场的方向在临场悄悄漂走，立刻落一条告警——而不是等赛后/临场被用户戳破才去查。
    ///
    /// 背景：方向闸（MilkDirectionGate）只在出注那一刻跑一次，存的是静态快照。
    /// 而市场转向是时间序列上的漂移——让胜赔率一路爬升、净胜球期望持续下修、
    /// 深档整批降价——净胜球诊断（FbScoreDiag）每 2 分钟在刷新，却没有一条代码
    /// 把「已出注的场次临场发生了市场转向」主动推出来。这就是「早干啥去了」的答案。
    ///
    /// 本方法由 turn-guard 定时任务调用，扫描当天未开赛、已发出、未告警过的大奶注。
    /// </summary>
    public async Task<int> SaveTurnAlertsAsync(
        DateTime alertDate, IReadOnlyList<TurnAlertRow> alerts, CancellationToken ct = default)
    {
        var d = alertDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = 0;
        foreach (var a in alerts)
        {
            if (a.MatchId <= 0) continue;
            rows += await conn.ExecuteAsync(new CommandDefinition(
                @"IF NOT EXISTS (SELECT 1 FROM dbo.FbTurnAlert WHERE AlertDate=@d AND MatchId=@mid AND Reason=@reason)
                  INSERT INTO dbo.FbTurnAlert
                    (AlertDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PickOption, Reason, Severity, ScoreDiagTag, ExpGdOpen, ExpGdNow, ExpGdMove,
                     CreatedAt)
                  VALUES (@d, @mid, @no, @lg, @home, @away, @time,
                          @pick, @reason, @sev, @tag, @open, @now, @move, SYSDATETIME())",
                new
                {
                    d, mid = a.MatchId, no = a.MatchNo, lg = a.LeagueName,
                    home = a.HomeTeam, away = a.AwayTeam, time = a.MatchTime,
                    pick = a.PickOption, reason = a.Reason, sev = a.Severity,
                    tag = a.ScoreDiagTag, open = a.ExpGdOpen, now = a.ExpGdNow, move = a.ExpGdMove
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>当日未解决的临场转向告警，按严重度降序、开赛时间正序。</summary>
    public async Task<List<TurnAlertRow>> GetTurnAlertsAsync(DateTime alertDate, CancellationToken ct = default)
    {
        var d = alertDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TurnAlertRow>(new CommandDefinition(
            @"SELECT AlertId, AlertDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PickOption, Reason, Severity, ScoreDiagTag, ExpGdOpen, ExpGdNow, ExpGdMove, CreatedAt
              FROM dbo.FbTurnAlert
              WHERE AlertDate=@d
              ORDER BY CASE WHEN Severity='high' THEN 0 ELSE 1 END, MatchTime, MatchNo",
            new { d }, cancellationToken: ct))).ToList();
    }

    /// <summary>当天已发出、未开赛、未结算的大奶注，供临场转向哨兵扫描。</summary>
    public async Task<List<MilkPickRow>> GetOpenDayAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<MilkPickRow>(new CommandDefinition(
            @"SELECT p.PickId, p.PickDate, p.MatchId, p.MatchNo, p.LeagueName, p.HomeTeam, p.AwayTeam,
                     p.MatchTime, p.PlayType, p.OptionCode, p.OptionName, p.Odds, p.ModelProbability, p.Ev,
                     p.ActualScore, p.ActualHalfScore, p.ActualGoals, p.Hit, p.SettledAt
              FROM dbo.FbMilkPick p
              JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
              WHERE p.PickDate=@d AND p.Hit IS NULL AND p.SettledAt IS NULL
                AND m.Status <> 3 AND m.MatchTime > SYSDATETIME()
              ORDER BY m.MatchTime, p.MatchNo",
            new { d }, cancellationToken: ct))).ToList();
    }

    private sealed class MilkPendingRow
    {
        public long PickId { get; set; }
        public string PlayType { get; set; } = "";
        public string OptionCode { get; set; } = "";
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
    }
}

/// <summary>
/// 临场转向告警一行（2026-11-05 加）。
/// 记录「哪场、哪注、市场的方向怎么在临场漂走了」——系统主动喊出来的报警凭证。
/// </summary>
public sealed class TurnAlertRow
{
    public long AlertId { get; set; }
    public DateTime AlertDate { get; set; }
    public long MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }

    /// <summary>这一注的玩法+选项显示名，如「半全场 胜胜」。</summary>
    public string PickOption { get; set; } = "";

    /// <summary>一句话说明为什么报警。</summary>
    public string Reason { get; set; } = "";

    /// <summary>high / mid。high = 净胜球期望下修且深档整批降价；mid = 单一转向信号。</summary>
    public string Severity { get; set; } = "mid";

    /// <summary>净胜球诊断的尾盘标签（如「客队深档整批降价」）。</summary>
    public string? ScoreDiagTag { get; set; }
    public decimal? ExpGdOpen { get; set; }
    public decimal? ExpGdNow { get; set; }
    public decimal? ExpGdMove { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>FbMilkPick 读回一行。</summary>
public sealed class MilkPickRow
{
    public long PickId { get; set; }
    public DateTime PickDate { get; set; }
    public long MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public string PlayType { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public string OptionName { get; set; } = "";
    public decimal Odds { get; set; }
    public decimal ModelProbability { get; set; }
    public decimal Ev { get; set; }
    public string? ActualScore { get; set; }
    public string? ActualHalfScore { get; set; }
    public int? ActualGoals { get; set; }
    public bool? Hit { get; set; }
    public DateTime? SettledAt { get; set; }
}

/// <summary>按玩法聚合的大奶命中统计。</summary>
public sealed class MilkPlayStat
{
    public string PlayType { get; set; } = "";
    /// <summary>已结算注数（Hit 非空）。</summary>
    public int Total { get; set; }
    public int Hit { get; set; }
    public decimal TotalOdds { get; set; }
    public decimal HitOdds { get; set; }
    public decimal AvgOdds { get; set; }
    /// <summary>模型给这些注的平均概率（%）。</summary>
    public decimal AvgProbability { get; set; }
    public decimal AvgEv { get; set; }

    public decimal HitRatePct => Total == 0 ? 0 : Math.Round(Hit * 100m / Total, 1);

    /// <summary>
    /// 平注回报率：每注 1 元，命中拿回赔率、不中归零。ROI = 命中赔率合计 ÷ 注数。
    /// （早前用「÷ 全部赔率合计」是错的，那算的是命中赔率占比，不是回报率。）
    /// </summary>
    public decimal Roi => Total == 0 ? 0 : Math.Round(HitOdds / Total, 3);

    /// <summary>模型概率 − 实际命中率，正数说明模型高估（大奶最常见的问题）。</summary>
    public decimal OverconfidencePct => Math.Round(AvgProbability - HitRatePct, 1);
}

/// <summary>某个玩法下具体选项的历史表现（如「半全场 负胜」）。</summary>
public sealed class MilkOptionStat
{
    public string PlayType { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public int Total { get; set; }
    public int Hit { get; set; }
    public decimal AvgOdds { get; set; }
    public string OptionName => MilkPickMath.ShortName(PlayType, OptionCode);
    public decimal HitRatePct => Total == 0 ? 0 : Math.Round(Hit * 100m / Total, 1);
}

/// <summary>大奶战绩报表（页面 /api/milk/pick-stats）。</summary>
public sealed class MilkStatsReport
{
    public int Days { get; set; }
    public DateTime From { get; set; }
    public MilkPlayStat Overall { get; set; } = new();
    public List<MilkPlayStat> Plays { get; set; } = new();
    public int Pending { get; set; }
    public List<MilkOptionStat> TopOptions { get; set; } = new();
}
