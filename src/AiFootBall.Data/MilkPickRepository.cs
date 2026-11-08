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

    /// <summary>
    /// 台账新列自愈（2026-11-08）。此前每次加列都得手工 ALTER，一旦哪次漏了，
    /// 表现是「INSERT 直接报列不存在，整张榜一行不写」——11-07 已经因为注入缺失踩过一次同类坑，
    /// 这里改成幂等补列，宁可多一条 DDL 也不要静默失败。
    /// </summary>
    private async Task EnsureColumnsAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (_columnsReady) return;
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF COL_LENGTH('dbo.FbMilkPick','HalfDrawRefined') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD HalfDrawRefined bit NOT NULL CONSTRAINT DF_FbMilkPick_HalfDrawRefined DEFAULT(0);
              IF COL_LENGTH('dbo.FbMilkPick','FatigueTag') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD FatigueTag nvarchar(60) NULL;",
            cancellationToken: ct));
        _columnsReady = true;
    }

    private static bool _columnsReady;

    /// <summary>重写当日未结算的大奶推荐；已结算的行（含取消终态）不动（重算不冲掉赛果）。</summary>
    public async Task<int> SaveDayAsync(DateTime pickDate, IReadOnlyList<MilkPick> picks, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        // 补列必须在开事务之前：连接一旦进入本地挂起事务，BeginExecuteNonQuery 就要求
        // 命令自己携带 Transaction，否则直接抛「命令的 Transaction 属性尚未初始化」。
        await EnsureColumnsAsync(conn, ct);
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
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev, CounterHigh,
                     BaselineProbability, BaselineEv, ModelLowConfidence, ModelOverconfident,
                     DivergenceTag, DivergenceNote, HalfDrawRefined, FatigueTag)
                  VALUES (@d, @mid, @no, @lg, @home, @away, @time,
                          @play, @code, @name, @odds, @prob, @ev, @counterHigh,
                          @basePct, @baseEv, @lowConf, @overConf,
                          @divTag, @divNote, @halfDraw, @fatigue)",
                new
                {
                    d, mid, no = p.MatchNo, lg = p.LeagueName, home = p.HomeTeam, away = p.AwayTeam,
                    time = p.MatchTime, play = p.PlayType, code = p.OptionCode, name = p.OptionName,
                    odds = p.Odds, prob = p.ModelProbability, ev = p.Ev,
                    // 高赔反转单列标记必须落库（2026-11-07）：只在内存里置位，API 一投影就丢，
                    // 第二天回看台账谁也分不清哪一注是单列额外出的大赔率票。
                    counterHigh = p.CounterHigh,
                    // 基线口径的期望值（2026-11-07 夜）：单列注的可信度来自联赛实测频率，
                    // 不来自模型概率。不落库的话，赛后复盘只能看到模型那条被低估到 0.658 的 EV，
                    // 会把这个通道判成「发垃圾注」，而真实口径是 0.777。
                    basePct = p.BaselineProbability,
                    baseEv = p.BaselineEv,
                    // 模型低可信标记（2026-11-07 夜）：赛后复盘要能回答
                    // 「这条被模型低估的注，最后到底中没中」——没有这一列，
                    // 标记只活在前端，长期验证就断了。
                    lowConf = p.ModelLowConfidence,
                    // 模型高估标记（2026-11-08）：名义 EV 超本玩法返奖率 ×1.05。
                    // 落库的理由和 lowConf 一样——赛后要能回答「被判模型高估的那几注，
                    // 最后命中率是不是真的只有市场隐含水平」，否则这个标记永远无法被验证。
                    overConf = p.ModelOverconfident,
                    // 定价背离标签（2026-11-08）：竞彩单边砸价/抬价、必发不认。
                    // 不落库的话，「背离闸到底挡掉了哪些场」赛后完全无法复核，
                    // 而这条闸的全部依据就是 13 场回测，必须让它可被持续验证。
                    divTag = p.DivergenceTag,
                    divNote = p.DivergenceNote,
                    // 胜平/负平换了概率源（2026-11-08）：不落这个标记的话，赛后看到一注 3-1
                    // 根本分不清它的概率是联合分布算的还是 HalfDraw 算的，模型校准就无从回看。
                    halfDraw = p.HalfDrawRefined,
                    // 体能/状态标注（2026-11-08）：只标注不扣分，落库是为了攒样本——
                    // 等「热门方欧战后 96 小时内」这类场次过百，才能回答该不该真扣概率。
                    fatigue = p.FatigueTag
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
        var rows = (await conn.QueryAsync<MilkPickRow>(new CommandDefinition(
            @"SELECT PickId, PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev, CounterHigh,
                     BaselineProbability, BaselineEv, ModelLowConfidence, ModelOverconfident,
                     DivergenceTag, DivergenceNote,
                     HalfDrawRefined, FatigueTag,
                     ActualScore, ActualHalfScore, ActualGoals, Hit, SettledAt
              FROM dbo.FbMilkPick WHERE PickDate=@d
              ORDER BY ModelProbability DESC, Ev DESC", new { d }, cancellationToken: ct))).ToList();

        // 当前挂单价（2026-11-08 加）：出榜价和现价的差，决定了「榜上写的赔率还能不能买到」。
        //
        // 性能是这里的主要约束：FbJcOddsHistory 505 万行，按 MatchNo 粗筛会把当天 8 场
        // 的**全部 54 个档位**都拉进来（约 44 万行），第一版实测稳定 1.2~1.4 秒/次，
        // 页面每次刷新都要等这么久。现在改成逐注三元组精确过滤（每注只留自己那一个档位，
        // 约 1 万行）+ 窗口函数取最新，实测降到百毫秒级。
        if (rows.Count > 0)
        {
            var keys = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.MatchNo) && !string.IsNullOrWhiteSpace(r.PlayType)
                            && !string.IsNullOrWhiteSpace(r.OptionCode))
                .Select(r => (r.MatchNo, r.PlayType, r.OptionCode))
                .Distinct()
                .ToList();

            if (keys.Count > 0)
            {
                var dp = new DynamicParameters();
                var ors = new List<string>(keys.Count);
                for (var i = 0; i < keys.Count; i++)
                {
                    dp.Add($"n{i}", keys[i].MatchNo);
                    dp.Add($"p{i}", keys[i].PlayType);
                    dp.Add($"c{i}", keys[i].OptionCode);
                    ors.Add($"(h.MatchNo=@n{i} AND h.PlayType=@p{i} AND h.OptionCode=@c{i})");
                }

                var latest = (await conn.QueryAsync<OddsNowRow>(new CommandDefinition(
                    $@"WITH src AS (
                           SELECT h.MatchNo, h.PlayType, h.OptionCode, h.Odds,
                                  ROW_NUMBER() OVER (PARTITION BY h.MatchNo, h.PlayType, h.OptionCode
                                                     ORDER BY h.CapturedAt DESC) AS rn
                           FROM dbo.FbJcOddsHistory h
                           WHERE {string.Join(" OR ", ors)}
                       )
                       SELECT MatchNo, PlayType, OptionCode, Odds FROM src WHERE rn = 1",
                    dp, cancellationToken: ct))).ToList();

                var map = latest
                    .GroupBy(x => (x.MatchNo, x.PlayType, x.OptionCode))
                    .ToDictionary(g => g.Key, g => g.First().Odds);
                foreach (var r in rows)
                    if (map.TryGetValue((r.MatchNo, r.PlayType, r.OptionCode), out var now)) r.CurrentOdds = now;
            }
        }

        return rows;
    }

    /// <summary>指定日期之前最近一个有大奶注的日期（跨午夜回退用），没有则 null。</summary>
    public async Task<DateTime?> GetLatestPickDateBeforeAsync(DateTime pickDate, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(PickDate) FROM dbo.FbMilkPick WHERE PickDate < @d",
            new { d = pickDate.Date }, cancellationToken: ct));
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
              WHERE p.Hit IS NULL
                AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND (m.Status = 3 OR m.MatchTime <= DATEADD(minute, -150, GETDATE()))",
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
        //
        // 2026-11-06 修 bug：数据源在比赛「未开赛」时会把 Status 短暂标成 2 又改回 0/1，
        // 旧逻辑只要 Status=2 就写死「取消」，导致还没开赛的注被永久误杀、退出结算队列。
        // 加两个前置条件：① 开赛时间已过（m.MatchTime < 当前时刻）；② 比分仍为 NULL。
        // 只有「时间过了 + 比分仍空 + Status=2」才是真取消；未开赛的短暂波动绝不判取消。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE p SET p.ActualScore = N'取消', p.SettledAt = SYSDATETIME()
              FROM dbo.FbMilkPick p
              JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
              WHERE p.Hit IS NULL AND p.SettledAt IS NULL AND m.Status = 2
                AND m.MatchTime < SYSDATETIME()
                AND m.HomeScore IS NULL AND m.AwayScore IS NULL",
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

    /// <summary>
    /// 读「开放中的大奶注」：未结算 + 比赛未开赛。
    /// 2026-11-07 修复跨午夜翻篇：原来按 PickDate=今天过滤，午夜一过就查不到昨晚落库的注，
    /// TurnGuard 对凌晨开赛的场次集体失明（恰是最需要监控的时段）。
    /// 现在只看「未结算 + 未开赛」这个真正的开放语义，pickDate 参数保留兼容但不再参与过滤。
    /// </summary>
    public async Task<List<MilkPickRow>> GetOpenDayAsync(DateTime pickDate, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<MilkPickRow>(new CommandDefinition(
            @"SELECT p.PickId, p.PickDate, p.MatchId, p.MatchNo, p.LeagueName, p.HomeTeam, p.AwayTeam,
                     p.MatchTime, p.PlayType, p.OptionCode, p.OptionName, p.Odds, p.ModelProbability, p.Ev,
                     p.ActualScore, p.ActualHalfScore, p.ActualGoals, p.Hit, p.SettledAt
              FROM dbo.FbMilkPick p
              JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
              WHERE p.Hit IS NULL AND p.SettledAt IS NULL
                AND m.Status <> 3 AND m.MatchTime > SYSDATETIME()
              ORDER BY m.MatchTime, p.MatchNo",
            cancellationToken: ct))).ToList();
    }

    /// <summary>单列候选判定表的建表脚本（幂等）。Save/Ensure 两处共用一份，避免两边漂移。</summary>
    private const string CounterCandidateDdl =
        @"IF OBJECT_ID('dbo.FbMilkCounterCandidate') IS NULL
          CREATE TABLE dbo.FbMilkCounterCandidate
          (
              PickDate          DATE NOT NULL,
              MatchId           BIGINT NOT NULL,
              OptionCode        NVARCHAR(10) NOT NULL,
              MatchNo           NVARCHAR(20) NULL,
              LeagueName        NVARCHAR(80) NULL,
              HomeTeam          NVARCHAR(80) NULL,
              AwayTeam          NVARCHAR(80) NULL,
              MatchTime         DATETIME2(0) NULL,
              Odds              DECIMAL(8,2) NOT NULL,
              ModelProbability  DECIMAL(6,2) NULL,
              BasePct           DECIMAL(6,3) NULL,
              Ratio             DECIMAL(6,3) NULL,
              Verdict           NVARCHAR(24) NOT NULL,
              Reason            NVARCHAR(300) NULL,
              CreatedAt         DATETIME2(0) NOT NULL DEFAULT SYSDATETIME(),
              CONSTRAINT PK_FbMilkCounterCandidate PRIMARY KEY (PickDate, MatchId, OptionCode)
          );";

    /// <summary>确保单列候选判定表存在（2026-11-08 加）。幂等。</summary>
    public async Task EnsureCounterCandidateTableAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(CounterCandidateDdl, cancellationToken: ct));
    }

    /// <summary>
    /// 重写当日单列候选判定（2026-11-08 加）。与榜单同批落库。
    ///
    /// 为什么这次要落库、而候选列表本身仍不落库：候选列表是「赔率快照 + 赛果」的纯派生视图，
    /// 任何一天都能重算；但**判定原因**（模型否决 / 比值越界 / 联赛无基线）是出榜那一刻
    /// 模型算出来的，模型每天重拟合，事后永远还原不出「当时为什么没选它」。
    /// 所以只把判据和结论存下来，列表仍旧实时派生。
    /// </summary>
    public async Task<int> SaveCounterCandidatesAsync(DateTime pickDate, IReadOnlyList<MilkCounterCandidate> rows, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 表不存在就建（幂等）：这条链路由「出榜」触发，不能因为运维忘了建表就整批丢失。
        await conn.ExecuteAsync(new CommandDefinition(CounterCandidateDdl, transaction: tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.FbMilkCounterCandidate WHERE PickDate=@d", new { d }, tx, cancellationToken: ct));

        var n = 0;
        foreach (var r in rows)
        {
            if (r.MatchId <= 0) continue;
            n += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbMilkCounterCandidate
                    (PickDate, MatchId, OptionCode, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Odds, ModelProbability, BasePct, Ratio, Verdict, Reason)
                  VALUES (@d, @mid, @code, @no, @lg, @home, @away, @time,
                          @odds, @prob, @basePct, @ratio, @verdict, @reason)",
                new
                {
                    d, mid = r.MatchId, code = r.OptionCode, no = r.MatchNo,
                    lg = r.LeagueName, home = r.HomeTeam, away = r.AwayTeam, time = r.MatchTime,
                    odds = r.Odds, prob = r.ModelProbability, basePct = r.BasePct, ratio = r.Ratio,
                    verdict = r.Verdict,
                    // Reason 列 300 字符，超长直接截断——宁可少一句解释，不能让整批落库失败。
                    reason = r.Reason is null ? null : (r.Reason.Length > 300 ? r.Reason[..300] : r.Reason)
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return n;
    }

    /// <summary>
    /// 读当日单列候选判定（2026-11-08 加）。赛果与「是否已入单列」实时关联，不存进判定表，
    /// 否则又会出现「快照过期」那类对账问题。
    /// </summary>
    public async Task<List<MilkCounterCandidateRow>> GetCounterCandidatesAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<MilkCounterCandidateRow>(new CommandDefinition(
            @"SELECT c.MatchId,
                     LTRIM(RTRIM(c.MatchNo))      AS MatchNo,
                     LTRIM(RTRIM(c.LeagueName))   AS LeagueName,
                     LTRIM(RTRIM(c.HomeTeam))     AS HomeTeam,
                     LTRIM(RTRIM(c.AwayTeam))     AS AwayTeam,
                     c.MatchTime, c.OptionCode, c.Odds, c.ModelProbability,
                     c.BasePct, c.Ratio, c.Verdict, c.Reason,
                     m.HalfHomeScore, m.HalfAwayScore, m.HomeScore, m.AwayScore,
                     CAST(CASE WHEN p.MatchId IS NULL THEN 0 ELSE 1 END AS bit) AS Chosen
              FROM dbo.FbMilkCounterCandidate c
              JOIN dbo.FbMatch m ON m.MatchId = c.MatchId
              LEFT JOIN dbo.FbMilkPick p
                     ON p.PickDate = c.PickDate AND p.MatchId = c.MatchId
                    AND p.OptionCode = c.OptionCode AND p.CounterHigh = 1
              WHERE c.PickDate = @d
              ORDER BY c.Odds DESC", new { d }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// 「够格但没上榜」的反转档候选（2026-11-08）。
    ///
    /// 单列通道每天只留 1 个名额（MilkPickEngine 里 FirstOrDefault），所以够格的
    /// 30~40 倍 胜负/负胜 档绝大多数不会出现在榜上——用户看不到的信息，等于不存在。
    /// 这里把它们原样查出来，只做展示，不参与推荐、不落库。
    ///
    /// 为什么能不落库：输入全是历史快照（FbJcOddsHistory 赔率 + FbMatch 半场/全场比分），
    /// 任何一天都能重算，幂等。落一张表反而会引入「快照过期」这种新的对账问题。
    ///
    /// 门槛过滤（联赛基线、比值区间、模型否决）不在这里做——基线表在 Sources 层，
    /// Data 层不反依赖它。这里只负责把「30~40 倍的 胜负/负胜 档」连赛果一起取出来。
    ///
    /// 赔率区间（2026-11-08 修订）：30 ~ &lt;40，必须与 MilkPickEngine.CounterHighMinOdds /
    /// CounterHighMaxOdds 保持一致。原为 ≥12，那时单列的定义也是「12 倍以上取最高」；
    /// 两边同时改是因为 40 倍以上 197 注只中 2 条（1.02%），30-39 倍 201 注中 4 条（1.99%）。
    /// </summary>
    public async Task<List<CounterCandidateRow>> GetCounterCandidateRowsAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        // 受注日口径（2026-11-08 修）：按 MatchTime 取"当天开赛"会把周六028 罗马(11-08 02:45 开球、
        // 周六受注) 混进周日候选里。竞彩页面上写的是「周日022」，用户脑子里的"今天"就是编号前缀，
        // 所以用编号前缀过滤，而不是开赛日期。
        var wd = "周" + "日一二三四五六"[(int)d.DayOfWeek];
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<CounterCandidateRow>(new CommandDefinition(
            @"WITH rev AS (
                  SELECT h.MatchId, MAX(h.MatchNo) AS MatchNo, h.OptionCode, h.OptionName,
                         CAST(MAX(h.Odds) AS decimal(7,2)) AS Odds
                  FROM dbo.FbJcOddsHistory h
                  WHERE h.PlayType = 'BQ'
                    AND h.OptionCode IN ('3-0','0-3')
                    AND h.MatchId IN (SELECT MatchId FROM dbo.FbMatch
                                      WHERE MatchTime >= DATEADD(day,-1,@d)
                                        AND MatchTime < DATEADD(day,2,@d))
                  GROUP BY h.MatchId, h.OptionCode, h.OptionName
                  HAVING MAX(h.Odds) >= 30 AND MAX(h.Odds) < 40
              )
              SELECT r.MatchId,
                     LTRIM(RTRIM(r.MatchNo))       AS MatchNo,
                     LTRIM(RTRIM(m.LeagueName))    AS LeagueName,
                     LTRIM(RTRIM(m.HomeTeamName))  AS HomeTeam,
                     LTRIM(RTRIM(m.AwayTeamName))  AS AwayTeam,
                     m.MatchTime, r.OptionCode, r.OptionName, r.Odds,
                     m.HalfHomeScore, m.HalfAwayScore, m.HomeScore, m.AwayScore,
                     CAST(CASE WHEN ch.MatchId IS NULL THEN 0 ELSE 1 END AS bit) AS Chosen,
                     CAST(CASE WHEN mb.MatchId IS NULL THEN 0 ELSE 1 END AS bit) AS OnMainBoard
              FROM rev r
              JOIN dbo.FbMatch m ON m.MatchId = r.MatchId
              LEFT JOIN (SELECT DISTINCT MatchId, OptionCode
                         FROM dbo.FbMilkPick
                         WHERE PickDate = @d AND CounterHigh = 1) ch
                     ON ch.MatchId = r.MatchId AND ch.OptionCode = r.OptionCode
              LEFT JOIN (SELECT DISTINCT MatchId
                         FROM dbo.FbMilkPick
                         WHERE PickDate = @d) mb
                     ON mb.MatchId = r.MatchId
              WHERE LTRIM(RTRIM(r.MatchNo)) LIKE @wd + '%'
              ORDER BY r.Odds DESC",
            new { d, wd }, cancellationToken: ct))).ToList();
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

/// <summary>某场某玩法某档当前最新快照价（2026-11-08，GetDayAsync 内部用）。</summary>
public sealed class OddsNowRow
{
    public string MatchNo { get; set; } = "";
    public string PlayType { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public decimal Odds { get; set; }
}

/// <summary>FbMilkCounterCandidate 读回一行（2026-11-08）：判定 + 实时赛果 + 是否已入单列。</summary>
public sealed class MilkCounterCandidateRow
{
    public long MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public string OptionCode { get; set; } = "";
    public decimal Odds { get; set; }
    public decimal ModelProbability { get; set; }
    public decimal? BasePct { get; set; }
    public decimal? Ratio { get; set; }
    /// <summary>MilkCounterVerdicts 常量。</summary>
    public string Verdict { get; set; } = "";
    public string? Reason { get; set; }
    public int? HalfHomeScore { get; set; }
    public int? HalfAwayScore { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public bool Chosen { get; set; }

    /// <summary>
    /// 赛后判定：半场领先方与全场胜负方相反才算中；未完赛为 null。
    ///
    /// 比旧 <see cref="CounterCandidateRow.Hit"/> 多一道完场闸——旧版只要比分非空就判，
    /// 比赛中途（半场 1:0、全场此刻 1:2）会被显示成「已中」，而它还会继续进球。
    /// 这里沿用全库统一口径（比分齐全 + 开赛过 150 分钟），不信 Status。
    /// </summary>
    public bool? Hit => HalfHomeScore is null || HalfAwayScore is null || HomeScore is null || AwayScore is null
        ? null
        : !(MatchTime is DateTime mt && mt <= DateTime.Now.AddMinutes(-150))
            ? null
            : OptionCode == "3-0"
                ? HalfHomeScore > HalfAwayScore && HomeScore < AwayScore
                : HalfHomeScore < HalfAwayScore && HomeScore > AwayScore;
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
    /// <summary>高赔反转单列（2026-11-07）。true = 12 倍以上的次名额，不在主榜 3 个半全场名额里。</summary>
    public bool CounterHigh { get; set; }
    /// <summary>联赛基线的实测概率（%，2026-11-07 夜加）。单列注必有；主榜反转档被标记时也会补。</summary>
    public decimal? BaselineProbability { get; set; }
    /// <summary>按联赛基线算的 EV（2026-11-07 夜加）。单列注/低可信注该给用户看的期望值。</summary>
    public decimal? BaselineEv { get; set; }
    /// <summary>模型在该注上概率低可信（2026-11-07 夜加）：模型概率 &lt; 联赛基线×0.70。</summary>
    public bool ModelLowConfidence { get; set; }
    /// <summary>模型在该注上概率高估（2026-11-08 加）：名义 EV &gt; 本玩法返奖率×1.05。</summary>
    public bool ModelOverconfident { get; set; }
    /// <summary>
    /// 竞彩/必发定价背离标签（2026-11-08 加）：fake-hot-away / fake-hot-home /
    /// undervalued-home / undervalued-away。判定见 Core.Models.PriceDivergence。
    /// </summary>
    public string? DivergenceTag { get; set; }
    /// <summary>定价背离的中文说明（前端直接显示）。</summary>
    public string? DivergenceNote { get; set; }
    /// <summary>胜平/负平的概率来自 HalfDrawModel 而非 HalfFullJoint（2026-11-08 加）。</summary>
    public bool HalfDrawRefined { get; set; }
    /// <summary>热门方体能/状态标注（2026-11-08 加）：连败3场 / 欧战后N小时 / 休息N小时。只标注不扣分。</summary>
    public string? FatigueTag { get; set; }
    /// <summary>
    /// 当前挂单价（2026-11-08 加）。不是数据库列——由 <c>GetDayAsync</c> 关联当天最新快照填入。
    ///
    /// 用途：出榜价是出榜那一刻的快照，临场会动。单列注还有额外一层风险——它是区间票
    /// （赔率必须落在 30~&lt;40），价格滑出区间后引擎下一次重算就会换注，但页面上还挂着旧价。
    /// 页面据此显示「现价」并在滑出区间时打标，避免用户按一个已经不存在的赔率去下单。
    /// </summary>
    public decimal? CurrentOdds { get; set; }
    /// <summary>
    /// 本玩法的 EV 名义天花板（2026-11-08 加）。不是数据库列——由 API 接口按 PlayType
    /// 调 <c>MilkPickEngine.EvCapFor</c> 填进来。放在行对象上是因为前端要拿它和 ev 比大小，
    /// 页面里再硬编码一份「半全场 0.837 / 比分 0.745」，两处迟早对不上。
    /// </summary>
    public decimal EvCap { get; set; }
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

/// <summary>
/// 够格但未上榜的反转档候选（2026-11-08 加）。只做展示，不落库、不进推荐、不占名额。
/// 来由：单列通道每天只留 1 个名额，够格的 12 倍以上 胜负/负胜 档一天常有 5~6 条，
/// 全被挤掉，用户看不到就等于不存在。
/// </summary>
public sealed class CounterCandidateRow
{
    public long MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string LeagueName { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public DateTime MatchTime { get; set; }
    public string OptionCode { get; set; } = "";
    public string OptionName { get; set; } = "";
    public decimal Odds { get; set; }
    public int? HalfHomeScore { get; set; }
    public int? HalfAwayScore { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    /// <summary>这一注是否就是当天入选单列的那一条。</summary>
    public bool Chosen { get; set; }
    /// <summary>该场是否已经进了当天主榜（引擎规则：已进主榜的场次不再占单列名额）。</summary>
    public bool OnMainBoard { get; set; }

    /// <summary>赛后判定：半场领先方与全场胜负方相反才算中。未完赛为 null。</summary>
    public bool? Hit => HalfHomeScore is null || HomeScore is null
        ? null
        : OptionCode == "3-0"
            ? HalfHomeScore > HalfAwayScore && HomeScore < AwayScore
            : HalfHomeScore < HalfAwayScore && HomeScore > AwayScore;
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
