using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 每日逐场跟进复盘台账（dbo.FbDailyFollow / dbo.FbDailySkill）。
///
/// 2026-11-06 起的新「每日复盘」口径（用户 2026-11-05 重定义）：
///   · 竞彩每场一个大奶（比分/半全场/总进球三选一）；
///   · 记录每次抓取时间、水位/必发变化，重大变化标注；
///   · 每场结束后在该场后面写复盘（当时为什么这么下 / 命中理由 / 错误原因）；
///   · 每天总结一条知识技能。
///
/// 与 FbMilkPick（大奶榜台账）的区别：那是「精选榜、只发竞彩、最多 N 注」，
/// 这张是「每场都跟、全程记录、逐场复盘」的跟进账。
/// </summary>
public sealed class DailyFollowRepository
{
    private readonly SqlConnectionFactory _factory;

    public DailyFollowRepository(SqlConnectionFactory factory) => _factory = factory;

    private static int _altColumnsEnsured;

    /// <summary>
    /// 幂等补 Alt* 列（2026-11-11 双选）。FbDailyFollow 没有进 01_schema.sql，
    /// 只能在代码里补；否则换台机器部署就少列，INSERT 直接失败。
    /// </summary>
    private static async Task EnsureAltColumnsAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _altColumnsEnsured, 1) == 1) return;
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF COL_LENGTH('dbo.FbDailyFollow','AltPlayType') IS NULL
              ALTER TABLE dbo.FbDailyFollow ADD
                  AltPlayType nvarchar(8) NULL,
                  AltOptionCode nvarchar(16) NULL,
                  AltOptionName nvarchar(64) NULL,
                  AltOdds decimal(8,3) NULL,
                  AltModelProbability decimal(6,2) NULL,
                  AltEv decimal(8,3) NULL,
                  AltHit bit NULL;",
            cancellationToken: ct));
    }

    /// <summary>
    /// 对比式保存当日逐场跟进。行为：
    ///   · 新算的注 vs 库里已有的未结算注，逐场对比（按 MatchId）；
    ///   · 若该场「玩法 + 档位」变了（比如总进球2 → 比分2:3），写入变更历史 FbDailyFollowChange 留痕，
    ///     再更新当前最新推荐；
    ///   · 没变的场，只刷新水位/必发/抓取时间，不写变更；
    ///   · 已结算/已写复盘的行不动（不冲掉赛果与复盘）。
    /// 返回 (upsert 场数, 变更场数)。
    /// </summary>
    public async Task<(int Upserted, int Changed)> SaveDayDiffAsync(
        DateTime followDate, IReadOnlyList<DailyFollowRow> rows, CancellationToken ct = default)
    {
        var d = followDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureAltColumnsAsync(conn, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 取当日「未结算」的现有行，按 MatchId 建索引。
        var existing = (await conn.QueryAsync<DailyFollowRow>(new CommandDefinition(
            @"SELECT FollowId, MatchId, PlayType, OptionCode, OptionName, FirstCapturedAt
              FROM dbo.FbDailyFollow
              WHERE FollowDate=@d AND SettledAt IS NULL AND ReviewedAt IS NULL",
            new { d }, tx, cancellationToken: ct)))
            .ToDictionary(x => x.MatchId);

        var upserted = 0;
        var changed = 0;
        foreach (var r in rows)
        {
            if (r.MatchId <= 0) continue;

            // 该场之前下过注，且玩法/档位变了 → 记变更历史 + 更新
            if (existing.TryGetValue(r.MatchId, out var prev) &&
                (prev.PlayType != r.PlayType || prev.OptionCode != r.OptionCode))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    @"INSERT INTO dbo.FbDailyFollowChange
                        (FollowDate, MatchId, MatchNo, HomeTeam, AwayTeam, ChangedAt,
                         FromPlayType, FromOptionCode, FromOptionName,
                         ToPlayType, ToOptionCode, ToOptionName, TriggerSignal, Reason)
                      VALUES (@FollowDate, @MatchId, @MatchNo, @HomeTeam, @AwayTeam, SYSUTCDATETIME(),
                              @FromPlayType, @FromOptionCode, @FromOptionName,
                              @ToPlayType, @ToOptionCode, @ToOptionName, @TriggerSignal, @Reason)",
                    new
                    {
                        r.FollowDate, r.MatchId, r.MatchNo, r.HomeTeam, r.AwayTeam,
                        FromPlayType = prev.PlayType, FromOptionCode = prev.OptionCode, FromOptionName = prev.OptionName,
                        ToPlayType = r.PlayType, ToOptionCode = r.OptionCode, ToOptionName = r.OptionName,
                        r.TriggerSignal, r.Reason
                    }, tx, cancellationToken: ct));
                changed++;
                upserted += await conn.ExecuteAsync(new CommandDefinition(
                    @"UPDATE dbo.FbDailyFollow SET
                        PlayType=@PlayType, OptionCode=@OptionCode, OptionName=@OptionName,
                        Odds=@Odds, ModelProbability=@ModelProbability, Ev=@Ev, Reason=@Reason,
                        AltPlayType=@AltPlayType, AltOptionCode=@AltOptionCode, AltOptionName=@AltOptionName,
                        AltOdds=@AltOdds, AltModelProbability=@AltModelProbability, AltEv=@AltEv,
                        LastCapturedAt=@LastCapturedAt,
                        OddsLastHome=@OddsLastHome, OddsLastDraw=@OddsLastDraw, OddsLastAway=@OddsLastAway,
                        ExRatioLast=@ExRatioLast, MajorChange=@MajorChange, UpdatedAt=SYSUTCDATETIME()
                      WHERE FollowDate=@FollowDate AND MatchId=@MatchId AND SettledAt IS NULL",
                    r, tx, cancellationToken: ct));
                continue;
            }

            // 该场之前没下过注 → 新增
            if (!existing.ContainsKey(r.MatchId))
            {
                upserted += await conn.ExecuteAsync(new CommandDefinition(
                    @"INSERT INTO dbo.FbDailyFollow
                        (FollowDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                         PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev, Reason,
                         FirstCapturedAt, LastCapturedAt,
                         OddsOpenHome, OddsOpenDraw, OddsOpenAway, OddsLastHome, OddsLastDraw, OddsLastAway,
                         ExRatioOpen, ExRatioLast, MajorChange,
                         AltPlayType, AltOptionCode, AltOptionName, AltOdds, AltModelProbability, AltEv)
                      VALUES (@FollowDate, @MatchId, @MatchNo, @LeagueName, @HomeTeam, @AwayTeam, @MatchTime,
                              @PlayType, @OptionCode, @OptionName, @Odds, @ModelProbability, @Ev, @Reason,
                              @FirstCapturedAt, @LastCapturedAt,
                              @OddsOpenHome, @OddsOpenDraw, @OddsOpenAway, @OddsLastHome, @OddsLastDraw, @OddsLastAway,
                              @ExRatioOpen, @ExRatioLast, @MajorChange,
                              @AltPlayType, @AltOptionCode, @AltOptionName, @AltOdds, @AltModelProbability, @AltEv)",
                    r, tx, cancellationToken: ct));
                continue;
            }

            // 该场下过注且玩法/档位没变 → 只刷新水位/必发/抓取时间/重大变化
            upserted += await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbDailyFollow SET
                    Odds=@Odds, ModelProbability=@ModelProbability, Ev=@Ev, Reason=@Reason,
                    AltPlayType=@AltPlayType, AltOptionCode=@AltOptionCode, AltOptionName=@AltOptionName,
                    AltOdds=@AltOdds, AltModelProbability=@AltModelProbability, AltEv=@AltEv,
                    LastCapturedAt=@LastCapturedAt,
                    OddsLastHome=@OddsLastHome, OddsLastDraw=@OddsLastDraw, OddsLastAway=@OddsLastAway,
                    ExRatioLast=@ExRatioLast, MajorChange=@MajorChange, UpdatedAt=SYSUTCDATETIME()
                  WHERE FollowDate=@FollowDate AND MatchId=@MatchId AND SettledAt IS NULL",
                r, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return (upserted, changed);
    }

    /// <summary>当日逐场跟进（按开赛时间排序）。</summary>
    public async Task<List<DailyFollowRow>> GetDayAsync(DateTime followDate, CancellationToken ct = default)
    {
        var d = followDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<DailyFollowRow>(new CommandDefinition(
            @"SELECT FollowId, FollowDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev, Reason,
                     FirstCapturedAt, LastCapturedAt,
                     OddsOpenHome, OddsOpenDraw, OddsOpenAway, OddsLastHome, OddsLastDraw, OddsLastAway,
                     ExRatioOpen, ExRatioLast, MajorChange,
                     AltPlayType, AltOptionCode, AltOptionName, AltOdds, AltModelProbability, AltEv,
                     ActualScore, ActualHalfScore, ActualGoals, Hit, AltHit, SettledAt, ReviewText, ReviewedAt
              FROM dbo.FbDailyFollow
              WHERE FollowDate=@d
              ORDER BY MatchTime, MatchNo",
            new { d }, cancellationToken: ct))).ToList();
    }

    /// <summary>回填单场赛果并写复盘。只更新未结算的行。</summary>
    public async Task<int> SettleAsync(
        long matchId, string? actualScore, string? actualHalfScore, int? actualGoals,
        bool hit, bool? altHit, string reviewText, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.FbDailyFollow
              SET ActualScore=@actualScore, ActualHalfScore=@actualHalfScore, ActualGoals=@actualGoals,
                  Hit=@hit, AltHit=@altHit, SettledAt=SYSUTCDATETIME(),
                  ReviewText=@reviewText, ReviewedAt=SYSUTCDATETIME(),
                  UpdatedAt=SYSUTCDATETIME()
              WHERE MatchId=@matchId AND SettledAt IS NULL",
            new { matchId, actualScore, actualHalfScore, actualGoals, hit, altHit, reviewText },
            cancellationToken: ct));
    }

    /// <summary>写/更新某天的技能总结（一天一条，覆盖式）。</summary>
    public async Task<int> SaveSkillAsync(DateTime skillDate, string title, string body, string? evidence, CancellationToken ct = default)
    {
        var d = skillDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.FbDailySkill WHERE SkillDate=@d", new { d }, cancellationToken: ct));
        if (exists > 0)
        {
            return await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbDailySkill SET Title=@title, Body=@body, Evidence=@evidence WHERE SkillDate=@d",
                new { d, title, body, evidence }, cancellationToken: ct));
        }
        return await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbDailySkill (SkillDate, Title, Body, Evidence)
              VALUES (@d, @title, @body, @evidence)",
            new { d, title, body, evidence }, cancellationToken: ct));
    }

    /// <summary>某天的技能总结。</summary>
    public async Task<DailySkillRow?> GetSkillAsync(DateTime skillDate, CancellationToken ct = default)
    {
        var d = skillDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<DailySkillRow>(new CommandDefinition(
            @"SELECT SkillId, SkillDate, Title, Body, Evidence, CreatedAt
              FROM dbo.FbDailySkill WHERE SkillDate=@d", new { d }, cancellationToken: ct));
    }

    /// <summary>近 N 天的技能总结（供技能库沉淀）。</summary>
    public async Task<List<DailySkillRow>> GetSkillsAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<DailySkillRow>(new CommandDefinition(
            @"SELECT SkillId, SkillDate, Title, Body, Evidence, CreatedAt
              FROM dbo.FbDailySkill
              WHERE SkillDate >= @from
              ORDER BY SkillDate DESC",
            new { from = DateTime.Today.AddDays(-days) }, cancellationToken: ct))).ToList();
    }

    /// <summary>某天所有场次的推荐变更历史（按变更时间升序）。</summary>
    public async Task<List<DailyFollowChangeRow>> GetChangesAsync(DateTime followDate, CancellationToken ct = default)
    {
        var d = followDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<DailyFollowChangeRow>(new CommandDefinition(
            @"SELECT ChangeId, FollowDate, MatchId, MatchNo, HomeTeam, AwayTeam, ChangedAt,
                     FromPlayType, FromOptionCode, FromOptionName,
                     ToPlayType, ToOptionCode, ToOptionName, TriggerSignal, Reason
              FROM dbo.FbDailyFollowChange
              WHERE FollowDate=@d
              ORDER BY ChangedAt",
            new { d }, cancellationToken: ct))).ToList();
    }
}

/// <summary>逐场跟进行。</summary>
public sealed class DailyFollowRow
{
    public long FollowId { get; set; }
    public DateTime FollowDate { get; set; }
    public long MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public string PlayType { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public string OptionName { get; set; } = "";
    public decimal? Odds { get; set; }
    public decimal? ModelProbability { get; set; }
    public decimal? Ev { get; set; }
    public string? Reason { get; set; }
    public DateTime? FirstCapturedAt { get; set; }
    public DateTime? LastCapturedAt { get; set; }
    public decimal? OddsOpenHome { get; set; }
    public decimal? OddsOpenDraw { get; set; }
    public decimal? OddsOpenAway { get; set; }
    public decimal? OddsLastHome { get; set; }
    public decimal? OddsLastDraw { get; set; }
    public decimal? OddsLastAway { get; set; }
    public decimal? ExRatioOpen { get; set; }
    public decimal? ExRatioLast { get; set; }
    public string? MajorChange { get; set; }
    public string? TriggerSignal { get; set; }

    // 双选的第二注（2026-11-11）。为空表示该场只出了 1 注。
    // 单独成列而不是拆成两行：本表按 MatchId 一对一，拆分要重做去重与改判留痕逻辑。
    public string? AltPlayType { get; set; }
    public string? AltOptionCode { get; set; }
    public string? AltOptionName { get; set; }
    public decimal? AltOdds { get; set; }
    public decimal? AltModelProbability { get; set; }
    public decimal? AltEv { get; set; }
    public bool? AltHit { get; set; }

    public string? ActualScore { get; set; }
    public string? ActualHalfScore { get; set; }
    public int? ActualGoals { get; set; }
    public bool? Hit { get; set; }
    public DateTime? SettledAt { get; set; }
    public string? ReviewText { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

/// <summary>每日技能行。</summary>
public sealed class DailySkillRow
{
    public long SkillId { get; set; }
    public DateTime SkillDate { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Evidence { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>逐场推荐的「变更历史」行：记录某场从初判 X 改成 Y 的过程与触发信号。</summary>
public sealed class DailyFollowChangeRow
{
    public long ChangeId { get; set; }
    public DateTime FollowDate { get; set; }
    public long MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? FromPlayType { get; set; }
    public string? FromOptionCode { get; set; }
    public string? FromOptionName { get; set; }
    public string ToPlayType { get; set; } = "";
    public string ToOptionCode { get; set; } = "";
    public string ToOptionName { get; set; } = "";
    public string? TriggerSignal { get; set; }
    public string? Reason { get; set; }
}
