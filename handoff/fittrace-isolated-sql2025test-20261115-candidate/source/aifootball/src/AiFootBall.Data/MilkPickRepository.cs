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
                  ALTER TABLE dbo.FbMilkPick ADD FatigueTag nvarchar(60) NULL;
              -- 主胜赔率偏贵档标记（2026-11-11）：收盘欧赔主胜落在 [2.90,3.60) 且该注押主胜。
              -- 只标不撤，证据强度见 MilkPickEngine.HomePriceRichMin 的注释。
              IF COL_LENGTH('dbo.FbMilkPick','HomePriceRich') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD HomePriceRich bit NOT NULL CONSTRAINT DF_FbMilkPick_HomePriceRich DEFAULT(0);
              -- 纯模型概率（2026-11-11）：掺盘口之前的值，安全闸读它。
              -- 没有这一列，安全闸只能从混合值反解，w→1 时会被舍入误差放大。
              -- 默认 NULL 而不是 0：0 会被当成「模型判定概率为零」直接拦掉，历史行必须能和真 0 区分。
              IF COL_LENGTH('dbo.FbMilkPick','ModelPctPure') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD ModelPctPure decimal(6,2) NULL;
              -- 临场告警回写列（2026-11-08）：告警必须落在注单上，推荐页才看得见。
              IF COL_LENGTH('dbo.FbMilkPick','AlertSeverity') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD AlertSeverity nvarchar(8) NULL;
              IF COL_LENGTH('dbo.FbMilkPick','AlertReason') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD AlertReason nvarchar(400) NULL;
              IF COL_LENGTH('dbo.FbMilkPick','Voided') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD Voided bit NOT NULL CONSTRAINT DF_FbMilkPick_Voided DEFAULT(0);
              IF COL_LENGTH('dbo.FbMilkPick','VoidedAt') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD VoidedAt datetime2 NULL;
              -- 让球数快照（2026-11-10）：让球胜平负（NSPF）判中必须知道出注时的让球数。
              -- 不落这一列就只能回退 FbMatch.Handicap，而那是「当前」盘口——
              -- 竞彩让球在受注期间会被官方调整，赛后回看根本分不清当时让的是几球。
              IF COL_LENGTH('dbo.FbMilkPick','Handicap') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD Handicap decimal(6,2) NULL;
              -- 模型版本快照（2026-11-12）：拟合窗口起止、样本数、rho、构建 MVID、五个玩法的 w。
              -- 第三方校验方案的第一条 P0。榜上概率每天重拟合，没有这一列，
              -- 任何复盘都只能说「大概是那天的模型」，无法证明也无法反驳。
              IF COL_LENGTH('dbo.FbMilkPick','ModelVersion') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD ModelVersion nvarchar(160) NULL;
              -- 轮次标识（2026-11-13 第二轮）：注单要能和同一轮的输入凭证/存档/闸门对上。
              -- 没有它，一期一天重算三五次之后，「这几行是不是同一批算出来的」只能靠 CreatedAt 猜。
              IF COL_LENGTH('dbo.FbMilkPick','RunId') IS NULL
                  ALTER TABLE dbo.FbMilkPick ADD RunId nvarchar(40) NULL;
              -- ★ 唯一键必须扩到含 RunId（2026-11-13 第三轮）。
              -- 旧的 UX_FbMilkPick (PickDate, MatchId, PlayType, OptionCode) 只能存**一行**，
              -- 也就是「保留历史」根本插不进第二轮——这一条是自检在真库上打出来的：
              -- 第二轮写同一注，撞唯一键直接抛 2601，四类落库里的 picks 一步失败。
              -- 换成 (PickDate, RunId, ...)：同一轮内仍然不许重复（防交错重写写成两份），
              -- 不同轮各留一份（复盘要的就是「每一轮当时推了什么」）。
              IF EXISTS (SELECT 1 FROM sys.indexes i
                         WHERE i.object_id=OBJECT_ID(N'dbo.FbMilkPick') AND i.name='UX_FbMilkPick'
                           AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                                           JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                                           WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id
                                             AND ic.key_ordinal>0 AND c.name='RunId'))
                  DROP INDEX UX_FbMilkPick ON dbo.FbMilkPick;
              IF NOT EXISTS (SELECT 1 FROM sys.indexes
                             WHERE object_id=OBJECT_ID(N'dbo.FbMilkPick') AND name='UX_FbMilkPick_Run')
                  CREATE UNIQUE INDEX UX_FbMilkPick_Run
                      ON dbo.FbMilkPick (PickDate, RunId, MatchId, PlayType, OptionCode);
              -- 告警去重键（2026-11-08）：Reason 带实时数值，拿它去重等于不去重。
              IF COL_LENGTH('dbo.FbTurnAlert','SignalCode') IS NULL
                  ALTER TABLE dbo.FbTurnAlert ADD SignalCode nvarchar(80) NULL;
              IF COL_LENGTH('dbo.FbTurnAlert','LastSeenAt') IS NULL
                  ALTER TABLE dbo.FbTurnAlert ADD LastSeenAt datetime2 NULL;
              IF COL_LENGTH('dbo.FbTurnAlert','PickId') IS NULL
                  ALTER TABLE dbo.FbTurnAlert ADD PickId bigint NULL;
              IF COL_LENGTH('dbo.FbTurnAlert','PlayType') IS NULL
                  ALTER TABLE dbo.FbTurnAlert ADD PlayType varchar(10) NULL;
              IF COL_LENGTH('dbo.FbTurnAlert','OptionCode') IS NULL
                  ALTER TABLE dbo.FbTurnAlert ADD OptionCode varchar(20) NULL;
              -- 告警软删除（2026-11-10）：原来信号一消失就 DELETE 整行，
              -- 于是「这注在 18:30 触发过高危、19:10 又消失了」这件事在库里完全不留痕。
              -- 撤注判据因此永远无法回测：要回答「撤对了没有」，必须先能重建
              -- 「出票时它到底有没有高危告警」，而那个信息只在被删掉的那一刻存在过。
              IF COL_LENGTH('dbo.FbTurnAlert','ClearedAt') IS NULL
                  ALTER TABLE dbo.FbTurnAlert ADD ClearedAt datetime2 NULL;",
            cancellationToken: ct));
        _columnsReady = true;
    }

    private static bool _columnsReady;

    /// <summary>
    /// 闸表补 `RunId` 列（2026-11-13 第二轮）。
    ///
    /// 写和读都要调：闸表不是我们建的（历史迁移），列不存在时
    /// <see cref="GetGateAsync"/> 里那句 `MAX(RunId)` 会直接报「列名无效」，
    /// 而页面恰好是先读后写——只挂一条 500 比没有这个功能更糟。
    /// </summary>
    private static Task EnsureGateRunColumnAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID(N'dbo.FbMilkGate', N'U') IS NOT NULL
                  AND COL_LENGTH('dbo.FbMilkGate','RunId') IS NULL
                  ALTER TABLE dbo.FbMilkGate ADD RunId nvarchar(40) NULL;
              -- ★ 唯一键扩到含 RunId（2026-11-13 第三轮）：旧的 UX_FbMilkGate (GateDate, MatchId)
              -- 让同一场一天只能留一行诊断，第二轮重算要么撞键、要么覆盖掉上一轮的判定。
              -- 保留历史就得换键；同一轮内仍然只许一场一行。
              IF OBJECT_ID(N'dbo.FbMilkGate', N'U') IS NOT NULL
                 AND EXISTS (SELECT 1 FROM sys.indexes i
                             WHERE i.object_id=OBJECT_ID(N'dbo.FbMilkGate') AND i.name='UX_FbMilkGate'
                               AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                                               JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                                               WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id
                                                 AND ic.key_ordinal>0 AND c.name='RunId'))
                  DROP INDEX UX_FbMilkGate ON dbo.FbMilkGate;
              IF OBJECT_ID(N'dbo.FbMilkGate', N'U') IS NOT NULL
                 AND NOT EXISTS (SELECT 1 FROM sys.indexes
                                 WHERE object_id=OBJECT_ID(N'dbo.FbMilkGate') AND name='UX_FbMilkGate_Run')
                  CREATE UNIQUE INDEX UX_FbMilkGate_Run ON dbo.FbMilkGate (GateDate, RunId, MatchId);",
            cancellationToken: ct));

    /// <summary>重写当日未结算的大奶推荐；已结算的行（含取消终态）不动（重算不冲掉赛果）。</summary>
    public Task<int> SaveDayAsync(DateTime pickDate, IReadOnlyList<MilkPick> picks, CancellationToken ct = default)
        => SaveDayAsync(pickDate, picks, null, ct);

    /// <param name="runId">本轮标识（2026-11-13 第二轮）。传 null 表示无轮次的历史补档，行为与旧版一致。</param>
    public async Task<int> SaveDayAsync(
        DateTime pickDate, IReadOnlyList<MilkPick> picks, string? runId, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        // 补列必须在开事务之前：连接一旦进入本地挂起事务，BeginExecuteNonQuery 就要求
        // 命令自己携带 Transaction，否则直接抛「命令的 Transaction 属性尚未初始化」。
        await EnsureColumnsAsync(conn, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // ★ 2026-11-09 修：只重写「还没开赛」的注。
        //
        // 旧写法 `WHERE Hit IS NULL AND SettledAt IS NULL` 是无差别的：只要结算还没跑到，
        // 已开赛（甚至已完赛）的注也会被删掉。而结算作业每小时才跑一次，重算却更频繁，
        // 于是「重算先跑 → 注被删 → 结算再来时已找不到行」成了常态。
        // 11-08 的后果：曼联 BQ 平平 @6.15、弗赖堡 JQ 2 @4.20 两注命中，
        // 但它们在 FbMilkPick 里被删了，页面上只剩两注未中的町田，用户看到的是「一个都没中」。
        // 空跑台账 FbPaperTrade 因为每 10 分钟冻结才留下证据——台账能验证，但用户不该靠台账才能看到战绩。
        //
        // 口径：开赛 = 票已买，不可撤回、不可重写。要改只能走告警撤注（Voided），不是 DELETE。
        // 留 5 分钟余量覆盖时钟/调度抖动，避免开赛前一刹那的重算把票冲掉。
        //
        // 2026-11-09 追加：已撤注的行（Voided=1）也不删。撤注记录要「粘住」——
        // 撤注发生后若告警消失，下一次重算会把这行删掉再重建为未撤状态，等于悄悄复活，
        // 用户刚在撤榜展示区看到「这注被撤了」，十分钟后又回到主榜。撤注是风控决定，
        // 不能被重算无声撤销；要恢复只能人工判断（撤注判错要留痕，这正是撤榜区存在的意义）。
        // ★ 2026-11-13 第三轮：删除范围收窄到**本轮**（`RunId=@run`）。
        //
        // 保留历史之后，「撤掉旧票」不再靠在榜面上 DELETE 完成，而是靠**发布一个不含它的新轮次**：
        // 榜面只读该受注日最新成功轮的行，旧轮的行留在库里供复盘。
        // 若仍按日无差别 DELETE，历史就全没了（上一轮刚为「保留历史」改完四张表，这里必须跟上）。
        //
        // 本轮内部仍要 DELETE：同一轮重跑（重试、补写）时避免重复行。
        // runId=null 是历史补档口径，保持旧行为（整日清未开赛行）。
        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.FbMilkPick
              WHERE PickDate=@d AND Hit IS NULL AND SettledAt IS NULL
                AND ISNULL(Voided,0)=0
                AND MatchTime > DATEADD(minute, 5, SYSDATETIME())
                AND (@run IS NULL OR RunId=@run)",
            new { d, run = string.IsNullOrWhiteSpace(runId) ? null : runId }, tx, cancellationToken: ct));

        var rows = 0;
        foreach (var p in picks)
        {
            if (p.MatchId is not long mid) continue;
            // INSERT ... SELECT ... WHERE NOT EXISTS：已开赛的注被上面那条 DELETE 刻意留下，
            // 这里必须避免同一注再插一条（否则页面同一场同一档会并排出两行，其中一行赔率还是旧的）。
            // 用「同日 + 场次 + 玩法 + 档位」去重，和 FbPaperTrade 的冻结键同源。
            rows += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbMilkPick
                    (PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev, CounterHigh,
                     BaselineProbability, BaselineEv, ModelLowConfidence, ModelOverconfident,
                     DivergenceTag, DivergenceNote, HalfDrawRefined, FatigueTag, ModelVsMarketRatio,
                     HomePriceRich, ModelPctPure, ModelVersion, RunId)
                  SELECT @d, @mid, @no, @lg, @home, @away, @time,
                          @play, @code, @name, @odds, @prob, @ev, @counterHigh,
                          @basePct, @baseEv, @lowConf, @overConf,
                          @divTag, @divNote, @halfDraw, @fatigue, @vsMarket, @priceRich,
                          @purePct, @modelVer, @run
                  WHERE NOT EXISTS (
                      SELECT 1 FROM dbo.FbMilkPick x
                       WHERE x.PickDate=@d AND x.MatchId=@mid
                         AND x.PlayType=@play AND x.OptionCode=@code
                         AND (@run IS NULL OR x.RunId=@run))",
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
                    fatigue = p.FatigueTag,
                    // 模型纯值 ÷ 市场隐含（2026-11-08）：唯一一个「模型自己怎么看这注」的数字。
                    // 不落库的话，「模型说这价高估」只能活在前端角标里，赛后无法复核
                    // 这条判据到底拦对了还是拦错了——而它恰恰是唯一一条没经过回测的闸。
                    vsMarket = p.ModelVsMarketRatio,
                    // 主胜赔率偏贵档（2026-11-11）：只标不撤，落库是为了能攒出「这一档到底
                    // 兑现率多少」的已结算样本——现在的证据来自市场侧回测，不是我们自己的注单，
                    // 要升级成撤注判据必须先有自己样本。
                    priceRich = p.HomePriceRich,
                    // 纯模型概率（2026-11-11）：安全闸的输入，落库后赛后才能复核
                    // 「模型当时到底怎么看这一注」。
                    // 未写时存 NULL 而不是 0——0 的含义是「模型判定概率为零」，
                    // 那会被安全闸直接拦掉，跟「这个字段没算」完全不是一回事。
                    purePct = p.ModelPctPure > 0m ? p.ModelPctPure : (decimal?)null,
                    // 模型版本快照（2026-11-12）：审计 P0。空串按 NULL 存——
                    // 「算的时候没有版本信息」和「版本是一串空」必须能区分开。
                    modelVer = string.IsNullOrWhiteSpace(p.ModelVersion) ? null : p.ModelVersion,
                    // 轮次（2026-11-13 第二轮）：把这一行绑到产生它的那一轮计算上。
                    // 空串按 NULL——与 ModelVersion 同一口径，可区分「没传」和「传了空」。
                    run = string.IsNullOrWhiteSpace(runId) ? null : runId
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
    public Task<int> SaveGateAsync(
        DateTime gateDate, IReadOnlyList<MilkDirectionDiagnosis> gates, CancellationToken ct = default)
        => SaveGateAsync(gateDate, gates, null, ct);

    /// <param name="runId">
    /// 本轮标识。给了就按轮追加（同一轮重写幂等，其它轮的历史一行不碰）；
    /// 传 null 是旧口径「整期覆盖」，只用于给闸上线之前那些天补历史。
    /// </param>
    public async Task<int> SaveGateAsync(
        DateTime gateDate, IReadOnlyList<MilkDirectionDiagnosis> gates, string? runId, CancellationToken ct = default)
    {
        var d = gateDate.Date;

        // ★ 2026-11-13 修：与存档同一个 bug 的第二种表现——空结果把已有台账删掉。
        // 11-13 实测：11:40 那次闸表 3 行（001 pass、010/011 diverge），
        // 16:40 那次受注场次全被禁推名单挡在闸之前，gates 为空 →
        // 「DELETE 当日 + 一行不插」把 3 行诊断抹平，于是「为什么没推这几场」永久丢失。
        // 闸表是审计台账，不是缓存：没有新诊断就保留上一版，宁可在页面上显示旧时点的判定。
        if (gates.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);

        // 轮次列自愈（2026-11-13 第二轮）。闸表由历史迁移建，CREATE 不在这里，
        // 所以只做「表在但列不在」时的补列；表都不在说明环境不对，交给 INSERT 直接报错。
        await EnsureGateRunColumnAsync(conn, ct);

        await using var tx = await conn.BeginTransactionAsync(ct);

        if (string.IsNullOrWhiteSpace(runId))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.FbMilkGate WHERE GateDate=@d", new { d }, tx, cancellationToken: ct));
        }
        else
        {
            // 按轮删：同一轮重试时幂等，其它轮（含更早的旧口径 NULL 行）一条都不动。
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.FbMilkGate WHERE RunId=@run", new { run = runId }, tx, cancellationToken: ct));
        }

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
                     Verdict, AlertCode, AlertNote, BlockedPicks, BlockedBest, BlockedBestProbability,
                     RunId)
                  VALUES (@d, @mid, @no, @lg, @home, @away, @time,
                          @mDir, @mh, @md2, @ma, @pDir,
                          @kDir, @src, @kh, @kd, @ka,
                          @aDir, @aOpen, @aClose, @eDir, @rh, @ra,
                          @verdict, @alert, @note, @blocked, @best, @bestProb,
                          @run)",
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
                    blocked = g.BlockedPicks, best = g.BlockedBest, bestProb = g.BlockedBestProbability,
                    run = string.IsNullOrWhiteSpace(runId) ? null : runId
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// 重写某一期的**完整分布存档**（dbo.FbMilkProbSnapshot，2026-11-12 加）。
    ///
    /// 与 <see cref="SaveDayAsync"/> 的区别，必须分清：
    /// · 那张表存的是**出票**（6 注就 6 行），这张表存的是**算过的每一个档位**（一场半全场 9 行）。
    ///   后者才是「这场为什么没出票」能回答的前提。
    /// · 这张表只增不结算、不参与收益统计，纯审计。所以它可以放心整期重写：
    ///   每次重算都覆盖成最新一版，不像 FbMilkPick 那样要留已开赛的行。
    ///
    /// 全部用一条多行 INSERT 参数化写入：单日约 1500 行，逐行 Execute 会退化成 1500 次往返。
    /// </summary>
    public Task<int> SaveProbSnapshotsAsync(
        DateTime snapshotDate, IReadOnlyList<MilkProbSnapshot> rows, CancellationToken ct = default)
        => SaveProbSnapshotsAsync(snapshotDate, rows, null, ct);

    /// <param name="runId">
    /// 本轮标识（2026-11-13 第二轮）。给了就按轮追加并**保留历史**：
    /// 同一轮重试幂等，前面几轮的存档原样留着，靠 RunId 区分「这是哪一版算的」。
    /// 传 null 是旧口径（整期覆盖），只为兼容历史补档。
    /// </param>
    public async Task<int> SaveProbSnapshotsAsync(
        DateTime snapshotDate, IReadOnlyList<MilkProbSnapshot> rows, string? runId, CancellationToken ct = default)
    {
        var d = snapshotDate.Date;
        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID(N'dbo.FbMilkProbSnapshot', N'U') IS NULL
              CREATE TABLE dbo.FbMilkProbSnapshot
              (
                  Id            bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FbMilkProbSnapshot PRIMARY KEY,
                  SnapshotDate  date           NOT NULL,
                  MatchId       bigint         NOT NULL,
                  MatchNo       nvarchar(32)   NULL,
                  PlayType      varchar(10)    NOT NULL,
                  OptionCode    varchar(20)    NOT NULL,
                  Odds          decimal(8,3)   NULL,
                  MarketPct     decimal(7,3)   NULL,
                  ModelPctPure  decimal(7,3)   NULL,
                  BlendedPct    decimal(7,3)   NULL,
                  MarketPriced  bit            NOT NULL CONSTRAINT DF_FbMilkProbSnapshot_Priced DEFAULT(0),
                  ModelVersion  nvarchar(160)  NULL,
                  CreatedAt     datetime2      NOT NULL CONSTRAINT DF_FbMilkProbSnapshot_Created DEFAULT(SYSDATETIME())
              );
              -- 轮次列（2026-11-13 第二轮）：表已存在时补列。
              IF COL_LENGTH('dbo.FbMilkProbSnapshot','RunId') IS NULL
                  ALTER TABLE dbo.FbMilkProbSnapshot ADD RunId nvarchar(40) NULL;
              -- 复盘的主查询形态是「按日期取一期」和「按场次取全档」，两条索引都建上。
              -- 表刚建时数据量为 0，在这里建索引不影响出榜。
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkProbSnapshot_Date'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkProbSnapshot'))
                  CREATE INDEX IX_MilkProbSnapshot_Date
                      ON dbo.FbMilkProbSnapshot (SnapshotDate, MatchId, PlayType);
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkProbSnapshot_Match'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkProbSnapshot'))
                  CREATE INDEX IX_MilkProbSnapshot_Match
                      ON dbo.FbMilkProbSnapshot (MatchId, PlayType);
              -- 验收按轮查：没有这条索引，后期几万行里挑一轮要走全表扫。
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkProbSnapshot_Run'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkProbSnapshot'))
                  CREATE INDEX IX_MilkProbSnapshot_Run
                      ON dbo.FbMilkProbSnapshot (RunId, SnapshotDate);",
            cancellationToken: ct));

        // ★ 2026-11-13 修：**空结果不许删旧存档**（这条 bug 当天把 11-13 已经写好的 48 行全抹了）.
        //
        // 旧顺序是「先 DELETE 当日，再 if (rows.Count == 0) return 0」。
        // 于是只要某一次重算算出 0 行——11-13 是「国际赛」并入禁推名单之后，受注 11 场全被挡、
        // 一场都没进模型——上一版存档就被删掉且没有新行补上。
        // 后果不只是少一天数据：`IDENT_CURRENT` 已经到 1560，说明此前几十次重算都成功写过，
        // 却因为最后这次空跑，整张表看起来像「从未写入」，差点被当成「写入路径坏了」去查。
        //
        // 2026-11-13 第二轮修正：光「不删」不够——那样一次失败重算会留下一版旧数据冒充本轮。
        // 现在每轮有 `RunId`，写入按轮隔离、**历史保留**：
        //   · 本轮有行 → 追加本轮的行（同轮重试先删本轮，幂等）；
        //   · 本轮 0 行 → 一行不写，本轮在 FbMilkRun 里留一条「0 行」的记录，
        //     验收按该轮核对时看得到「这一轮什么都没算」，不会拿上一轮的行充数。
        if (rows.Count == 0) return 0;

        var withRun = !string.IsNullOrWhiteSpace(runId);
        var payload = rows.Select(r => new
        {
            d,
            mid = r.MatchId,
            no = r.MatchNo,
            play = r.PlayType,
            code = r.OptionCode,
            odds = r.Odds,
            mk = r.MarketPct,
            pure = r.ModelPctPure,
            blend = r.BlendedPct,
            priced = r.MarketPriced,
            ver = r.ModelVersion,
            run = withRun ? runId : null
        }).ToList();

        // DELETE 与 INSERT 必须在同一个事务里：原来两条语句各自提交，
        // INSERT 一报错（列数/类型/长度任一处不对）就只剩「删除成功、写入失败」，
        // 而且异常被调用方 catch 吞掉，表现是存档静默清零。
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (withRun)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.FbMilkProbSnapshot WHERE RunId=@run", new { run = runId }, tx, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d }, tx, cancellationToken: ct));
        }

        var written = await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbMilkProbSnapshot
                (SnapshotDate, MatchId, MatchNo, PlayType, OptionCode,
                 Odds, MarketPct, ModelPctPure, BlendedPct, MarketPriced, ModelVersion, RunId)
              VALUES (@d, @mid, @no, @play, @code, @odds, @mk, @pure, @blend, @priced, @ver, @run)",
            payload, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return written;
    }

    /// <summary>
    /// 本次真正进了 Milk 引擎的场次（2026-11-13 加，落库 <c>dbo.FbMilkInput</c>）。
    ///
    /// 与 <see cref="SaveProbSnapshotsAsync"/> 的空值口径**故意不同**：
    /// · 快照是「算出来的分布」，本次没有就保留上一版；
    /// · 这里记的是「本次算过哪些场」，必须如实反映本次——否则分母就是在骗自己。
    ///   本次一场没进模型，就写 0 行，让验收能一眼看出「这一版什么都没算」，
    ///   而不是拿着上一版的场次集合当成本次的输入。
    /// </summary>
    public Task<int> SaveInputsAsync(
        DateTime inputDate, IReadOnlyList<MilkInputMatch> rows, CancellationToken ct = default)
        => SaveInputsAsync(inputDate, rows, null, ct);

    /// <param name="runId">
    /// 本轮标识。给了就按轮追加、保留历史；传 null 是旧口径（整期覆盖）。
    /// 无论哪种，**空集都不许删别的轮**——「本轮算过 0 场」这件事由 FbMilkRun 记，不靠删行来表达。
    /// </param>
    public async Task<int> SaveInputsAsync(
        DateTime inputDate, IReadOnlyList<MilkInputMatch> rows, string? runId, CancellationToken ct = default)
    {
        var d = inputDate.Date;
        await using var conn = await _factory.OpenAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID(N'dbo.FbMilkInput', N'U') IS NULL
              CREATE TABLE dbo.FbMilkInput
              (
                  Id          bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FbMilkInput PRIMARY KEY,
                  InputDate   date           NOT NULL,
                  MatchId     bigint         NOT NULL,
                  MatchNo     nvarchar(32)   NULL,
                  LeagueName  nvarchar(128)  NULL,
                  HomeTeam    nvarchar(128)  NULL,
                  AwayTeam    nvarchar(128)  NULL,
                  MatchTime   datetime2      NULL,
                  VersionTag  nvarchar(160)  NULL,
                  CreatedAt   datetime2      NOT NULL CONSTRAINT DF_FbMilkInput_Created DEFAULT(SYSDATETIME())
              );
              -- 轮次列（2026-11-13 第二轮）：表已存在时补列。
              IF COL_LENGTH('dbo.FbMilkInput','RunId') IS NULL
                  ALTER TABLE dbo.FbMilkInput ADD RunId nvarchar(40) NULL;
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkInput_Date'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkInput'))
                  CREATE INDEX IX_MilkInput_Date ON dbo.FbMilkInput (InputDate, MatchId);
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkInput_Run'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkInput'))
                  CREATE INDEX IX_MilkInput_Run ON dbo.FbMilkInput (RunId, InputDate);",
            cancellationToken: ct));

        var withRun = !string.IsNullOrWhiteSpace(runId);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (withRun)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.FbMilkInput WHERE RunId=@run", new { run = runId }, tx, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.FbMilkInput WHERE InputDate=@d", new { d }, tx, cancellationToken: ct));
        }

        if (rows.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var payload = rows
            .Where(r => r.MatchId > 0)
            .GroupBy(r => r.MatchId)
            .Select(g => g.First())
            .Select(r => new
            {
                d,
                mid = r.MatchId,
                no = r.MatchNo,
                lg = r.LeagueName,
                home = r.HomeTeam,
                away = r.AwayTeam,
                time = r.MatchTime,
                ver = r.VersionTag,
                run = withRun ? runId : null
            }).ToList();

        var written = await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbMilkInput
                (InputDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime, VersionTag, RunId)
              VALUES (@d, @mid, @no, @lg, @home, @away, @time, @ver, @run)",
            payload, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return written;
    }

    /// <summary>
    /// 轮次表结构（2026-11-13 第三轮：加 `Status` / `PublishedAt` / `FailReason`，
    /// 并新增按受注日的发布表 `FbMilkRunDate`）。
    ///
    /// 为什么必须有 Status：`RunId` 自带时间前缀，只靠 `MAX(RunId)` 选榜面，
    /// 等于**让失败轮也能顶掉上一轮成功的榜**——失败轮往往正是最后跑的那轮。
    /// 现在榜面只认 `Status='success'`，失败的轮次即便 RunId 更大也不会被选中。
    /// </summary>
    private static Task EnsureRunSchemaAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID(N'dbo.FbMilkRun', N'U') IS NULL
              CREATE TABLE dbo.FbMilkRun
              (
                  RunId        nvarchar(40)  NOT NULL CONSTRAINT PK_FbMilkRun PRIMARY KEY,
                  RunDate      date          NOT NULL,
                  StartedAt    datetime2     NOT NULL,
                  RawMatches   int           NOT NULL CONSTRAINT DF_FbMilkRun_Raw   DEFAULT(0),
                  SalesDates   nvarchar(200) NULL,
                  InputCount   int           NOT NULL CONSTRAINT DF_FbMilkRun_In    DEFAULT(0),
                  SnapshotRows int           NOT NULL CONSTRAINT DF_FbMilkRun_Snap  DEFAULT(0),
                  GateRows     int           NOT NULL CONSTRAINT DF_FbMilkRun_Gate  DEFAULT(0),
                  PickRows     int           NOT NULL CONSTRAINT DF_FbMilkRun_Pick  DEFAULT(0),
                  BoardMode    varchar(12)   NOT NULL CONSTRAINT DF_FbMilkRun_Mode  DEFAULT('skip'),
                  Note         nvarchar(400) NULL,
                  VersionTag   nvarchar(160) NULL,
                  Status       varchar(12)   NOT NULL CONSTRAINT DF_FbMilkRun_Status DEFAULT('running'),
                  PublishedAt  datetime2     NULL,
                  FailReason   nvarchar(300) NULL,
                  CreatedAt    datetime2     NOT NULL CONSTRAINT DF_FbMilkRun_Created DEFAULT(SYSDATETIME())
              );
              -- 老表补列：已有行按 legacy 标记，既不是 success 也不是 failed，永远不会被榜面选中。
              IF COL_LENGTH('dbo.FbMilkRun','Status') IS NULL
                  ALTER TABLE dbo.FbMilkRun ADD Status varchar(12) NOT NULL
                      CONSTRAINT DF_FbMilkRun_Status DEFAULT('legacy') WITH VALUES;
              IF COL_LENGTH('dbo.FbMilkRun','PublishedAt') IS NULL
                  ALTER TABLE dbo.FbMilkRun ADD PublishedAt datetime2 NULL;
              IF COL_LENGTH('dbo.FbMilkRun','FailReason') IS NULL
                  ALTER TABLE dbo.FbMilkRun ADD FailReason nvarchar(300) NULL;
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkRun_Date'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkRun'))
                  CREATE INDEX IX_MilkRun_Date ON dbo.FbMilkRun (RunDate, StartedAt);

              -- 发布表（2026-11-13 第三轮）：一行 = 「这一轮覆盖了这个受注日，出了 N 注」。
              -- 0 注的日子也必须有一行，否则「榜面归零」查不出来——
              -- 没有行和「这一轮没管这一天」长得一样。榜面读它挑最新一个成功轮次。
              IF OBJECT_ID(N'dbo.FbMilkRunDate', N'U') IS NULL
                  CREATE TABLE dbo.FbMilkRunDate
                  (
                      RunId     nvarchar(40) NOT NULL,
                      SalesDate date         NOT NULL,
                      PickCount int          NOT NULL CONSTRAINT DF_FbMilkRunDate_Pick DEFAULT(0),
                      CreatedAt datetime2    NOT NULL CONSTRAINT DF_FbMilkRunDate_Created DEFAULT(SYSDATETIME()),
                      CONSTRAINT PK_FbMilkRunDate PRIMARY KEY (RunId, SalesDate)
                  );
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkRunDate_Date'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkRunDate'))
                  CREATE INDEX IX_MilkRunDate_Date ON dbo.FbMilkRunDate (SalesDate, RunId) INCLUDE (PickCount);

              -- 源侧快照 + 场次级拒绝记录（2026-11-14 第七轮）。
              -- 一行 = 「本轮源侧有这一场，它是被算了还是被拒了」。
              -- 为什么不是另一张表分别存「源」和「拒绝」：这两者本来就是同一场次的两种结局，
              -- 分成两张表就必须靠 LEFT JOIN 才能回答「这场有没有凭证」，而漏掉的恰恰是 JOIN 不上的那些行。
              -- 唯一键含 RunId（与 FbMilkPick/FbMilkGate 同一教训：真库撞 2601 才暴露）。
              IF OBJECT_ID(N'dbo.FbMilkSourceMatch', N'U') IS NULL
                  CREATE TABLE dbo.FbMilkSourceMatch
                  (
                      Id           bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FbMilkSourceMatch PRIMARY KEY,
                      RunId        nvarchar(40)  NOT NULL,
                      SalesDate    date          NOT NULL,
                      MatchId      bigint        NOT NULL,
                      MatchNo      nvarchar(32)  NULL,
                      LeagueName   nvarchar(128) NULL,
                      HomeTeam     nvarchar(128) NULL,
                      AwayTeam     nvarchar(128) NULL,
                      MatchTime    datetime2     NULL,
                      LastSampleAt datetime2     NULL,
                      OptionCount  int           NOT NULL CONSTRAINT DF_FbMilkSource_Opt DEFAULT(0),
                      Plays        nvarchar(120) NULL,
                      Outcome      varchar(16)   NOT NULL,
                      Reason       nvarchar(32)  NULL,
                      Evidence     nvarchar(200) NULL,
                      Accepted     bit           NOT NULL CONSTRAINT DF_FbMilkSource_Acc DEFAULT(0),
                      CreatedAt    datetime2     NOT NULL CONSTRAINT DF_FbMilkSource_Created DEFAULT(SYSDATETIME()),
                      CONSTRAINT UX_FbMilkSource_Run UNIQUE (RunId, MatchId)
                  );
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MilkSource_Run'
                             AND object_id=OBJECT_ID(N'dbo.FbMilkSourceMatch'))
                  CREATE INDEX IX_MilkSource_Run ON dbo.FbMilkSourceMatch (RunId, SalesDate);",
            cancellationToken: ct));

    /// <summary>
    /// 开一轮：先落 `running` 头（2026-11-13 第三轮）。
    ///
    /// 顺序是刻意的——**先把「这一轮开始了」写进去，再写四类数据，最后才标成功**。
    /// 进程中途死掉时库里留的是 running，不会被榜面选中；而如果反过来先写数据、
    /// 崩在标成功之前，四张表里就有了「没有轮次头的孤儿行」，事后无从判断它们算不算数。
    /// </summary>
    public async Task<int> StartRunAsync(
        MilkRun run, DateTime runDate, string mode, string? note, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);

        var p = new
        {
            run = run.RunId,
            d = runDate.Date,
            at = run.StartedAt,
            raw = run.RawMatches,
            dates = string.Join(",", run.SalesDates.Select(x => x.ToString("MM-dd"))),
            mode,
            note = Trim300(note),
            ver = string.IsNullOrWhiteSpace(run.VersionTag) ? null : run.VersionTag
        };

        return await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.FbMilkRun
                 SET RunDate=@d, StartedAt=@at, RawMatches=@raw, SalesDates=@dates,
                     BoardMode=@mode, Note=@note, VersionTag=@ver,
                     Status='running', PublishedAt=NULL, FailReason=NULL
               WHERE RunId=@run;
              IF @@ROWCOUNT = 0
                  INSERT INTO dbo.FbMilkRun
                      (RunId, RunDate, StartedAt, RawMatches, SalesDates,
                       BoardMode, Note, VersionTag, Status)
                  VALUES (@run, @d, @at, @raw, @dates, @mode, @note, @ver, 'running');",
            p, cancellationToken: ct));
    }

    /// <summary>
    /// 收一轮但不发布（失败 / 跳过 / 采集不完整）。标了状态就再也不会被榜面选中。
    /// </summary>
    public async Task<int> CloseRunAsync(
        string runId, string status, string? note,
        int inputCount = 0, int snapshotRows = 0, int gateRows = 0, int pickRows = 0,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.FbMilkRun
                 SET Status=@s, FailReason=@note,
                     InputCount=@inc, SnapshotRows=@snap, GateRows=@gate, PickRows=@pick
               WHERE RunId=@run",
            new
            {
                run = runId, s = status, note = Trim300(note),
                inc = inputCount, snap = snapshotRows, gate = gateRows, pick = pickRows
            }, cancellationToken: ct));
    }

    /// <summary>
    /// 发布一轮（2026-11-13 第三轮）：**四类落库全部成功之后**才允许调用。
    ///
    /// 一个事务里做两件事：写该轮覆盖的受注日（含 0 注的日子），再把头标成 success。
    /// 榜面读的是 `FbMilkRunDate` ⋈ `FbMilkRun(Status='success')`，
    /// 所以「切榜面」和「标成功」必须是同一次提交——否则会出现
    /// 「指针已切、状态还没标」这种半发布状态，读到的可能是未完成的轮次。
    /// </summary>
    public async Task<int> PublishRunAsync(
        string runId, IReadOnlyList<MilkRunDateCount> dates,
        int inputCount, int snapshotRows, int gateRows, int pickRows,
        string? note, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 同一轮重发幂等：先清本轮自己的发布行，别的轮一行不碰。
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.FbMilkRunDate WHERE RunId=@run", new { run = runId }, tx, cancellationToken: ct));

        if (dates.Count > 0)
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbMilkRunDate (RunId, SalesDate, PickCount)
                  VALUES (@run, @d, @n)",
                dates.Select(x => new { run = runId, d = x.SalesDate.Date, n = x.PickCount }).ToList(),
                tx, cancellationToken: ct));

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.FbMilkRun
                 SET Status='success', PublishedAt=SYSDATETIME(), FailReason=NULL,
                     InputCount=@inc, SnapshotRows=@snap, GateRows=@gate, PickRows=@pick,
                     Note=COALESCE(@note, Note)
               WHERE RunId=@run",
            new
            {
                run = runId, inc = inputCount, snap = snapshotRows, gate = gateRows, pick = pickRows,
                note = Trim300(note)
            }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// 榜面状态（给页面告警）：按<strong>受注日</strong>取该日最新成功发布轮，
    /// 不得用全局最新成功轮冒充当前页面。last attempt 也限定同一 RunDate。
    /// </summary>
    public Task<MilkBoardStatus> GetBoardStatusAsync(CancellationToken ct = default)
        => GetBoardStatusAsync(DateTime.Today, ct);

    public async Task<MilkBoardStatus> GetBoardStatusAsync(DateTime salesDate, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);
        var d = salesDate.Date;

        // 与 PublishedRunSql 同口径：FbMilkRunDate.SalesDate + Status=success
        var published = await conn.QuerySingleOrDefaultAsync<(DateTime? PublishedAt, string? RunId)>(
            new CommandDefinition(
                @"SELECT TOP 1 r.PublishedAt, r.RunId
                  FROM dbo.FbMilkRunDate rd
                  JOIN dbo.FbMilkRun r ON r.RunId = rd.RunId
                  WHERE rd.SalesDate = @d AND r.Status = 'success' AND r.PublishedAt IS NOT NULL
                  ORDER BY r.StartedAt DESC, rd.RunId DESC",
                new { d }, cancellationToken: ct));

        var last = await conn.QuerySingleOrDefaultAsync<(
            DateTime? StartedAt, string? Status, string? FailReason, string? Note, string? RunId)>(
            new CommandDefinition(
                @"SELECT TOP 1 StartedAt, Status, FailReason, Note, RunId
                  FROM dbo.FbMilkRun
                  WHERE RunDate = @d
                  ORDER BY StartedAt DESC",
                new { d }, cancellationToken: ct));

        return new MilkBoardStatus
        {
            SalesDate = d,
            PublishedAt = published.PublishedAt,
            PublishedRunId = published.RunId,
            LastAttemptAt = last.StartedAt,
            LastStatus = last.Status,
            LastFailReason = last.FailReason,
            LastNote = last.Note,
            LastRunId = last.RunId
        };
    }

    /// <summary>
    /// 读回本轮四类落库的**实际账本**（2026-11-13 第四轮）：按业务键分组计数。
    ///
    /// 键的格式与 <c>MilkRunLedger.Keys</c> 逐字对齐（`yyyy-MM-dd|MatchId|Play|Option`），
    /// 两边各写一份格式是本项目反复踩的坑，所以这里把 SQL 的拼法写在注释里对照：
    ///   pick     CONVERT(varchar(10), PickDate, 23) + '|' + MatchId + '|' + PlayType + '|' + OptionCode
    ///   gate     MatchId
    ///   snapshot MatchId + '|' + PlayType + '|' + OptionCode
    ///   input    MatchId
    ///
    /// 返回的是**实际行数**，不是「该有多少」——这正是一次「只写进去一半」能被发现的前提：
    /// 旧口径只比总数（input&gt;0 且 snapshot&gt;0），少几档、多几行都看不出来。
    /// </summary>
    public async Task<List<(string Table, string Key, int Count)>> ReadRunLedgerAsync(
        string runId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId)) return new List<(string, string, int)>();
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);
        var rows = (await conn.QueryAsync<(string Table, string Key, int Count)>(new CommandDefinition(
            @"SELECT 'pick' AS [Table],
                     CONVERT(varchar(10), p.PickDate, 23) + '|' + CAST(p.MatchId AS varchar(20))
                     + '|' + p.PlayType + '|' + p.OptionCode AS [Key],
                     COUNT(*) AS [Count]
              FROM dbo.FbMilkPick p
              WHERE p.RunId=@run
              GROUP BY p.PickDate, p.MatchId, p.PlayType, p.OptionCode
              UNION ALL
              SELECT 'gate' AS [Table], CAST(g.MatchId AS varchar(20)) AS [Key], COUNT(*) AS [Count]
              FROM dbo.FbMilkGate g
              WHERE g.RunId=@run
              GROUP BY g.MatchId
              UNION ALL
              SELECT 'snapshot' AS [Table],
                     CAST(s.MatchId AS varchar(20)) + '|' + s.PlayType + '|' + s.OptionCode AS [Key],
                     COUNT(*) AS [Count]
              FROM dbo.FbMilkProbSnapshot s
              WHERE s.RunId=@run
              GROUP BY s.MatchId, s.PlayType, s.OptionCode
              UNION ALL
              SELECT 'input' AS [Table], CAST(i.MatchId AS varchar(20)) AS [Key], COUNT(*) AS [Count]
              FROM dbo.FbMilkInput i
              WHERE i.RunId=@run
              GROUP BY i.MatchId
              UNION ALL
              -- 源侧场次账本（2026-11-14）：一行 = 源里的一场，算过或拒过。
              -- 它不参与「存档覆盖」判定，但必须与 FbMilkInput ∪ 拒绝记录逐键相等，
              -- 否则「这一场到底有没有被处理」就没有独立凭证。
              SELECT 'source' AS [Table], CAST(s.MatchId AS varchar(20)) AS [Key], COUNT(*) AS [Count]
              FROM dbo.FbMilkSourceMatch s
              WHERE s.RunId=@run
              GROUP BY s.MatchId",
            new { run = runId }, cancellationToken: ct))).ToList();
        return rows;
    }

    /// <summary>
    /// 落库源侧快照与场次级拒绝记录（2026-11-14 第七轮）。
    ///
    /// 每一行都必须属于本轮（按 RunId 先删后写），且**源侧每一场都要有一行**：
    /// 算过的写 `computed`（带档数与场景采样时刻），没算的写 `rejected`（带原因与证据）。
    /// 只有被算过的那些场才同时有 `FbMilkInput` 凭证——两张表一起才能回答
    /// 「该算的算了没有」和「没算的凭什么不算」。
    ///
    /// 空集也要写 0 行并返回 0：本轮源侧没有场次时，核对侧会拿「源 0 场」直接判，
    /// 不靠「一行没有」去猜。
    /// </summary>
    public async Task<int> SaveSourceAsync(
        string runId, DateTime salesDate,
        IReadOnlyList<MilkSourceMatch> source,
        IReadOnlyList<MilkInputRejection> rejections,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);

        var rejById = (rejections ?? Array.Empty<MilkInputRejection>())
            .Where(r => r.MatchId > 0)
            .GroupBy(r => r.MatchId)
            .ToDictionary(g => g.Key, g => g.Last());

        var payload = (source ?? Array.Empty<MilkSourceMatch>())
            .Where(m => m.MatchId > 0)
            .GroupBy(m => m.MatchId)
            .Select(g => g.First())
            .Select(m =>
            {
                var has = rejById.TryGetValue(m.MatchId, out var r);
                return new
                {
                    run = runId,
                    d = (m.SalesDate == default ? salesDate : m.SalesDate).Date,
                    mid = m.MatchId,
                    no = m.MatchNo,
                    lg = m.LeagueName,
                    home = m.HomeTeam,
                    away = m.AwayTeam,
                    time = m.MatchTime,
                    at = m.LastSampleAt,
                    opts = m.OptionCount,
                    plays = Trim120(m.Plays),
                    outcome = has ? "rejected" : "computed",
                    reason = has ? r!.Reason : null,
                    evidence = has ? Trim200(r!.Evidence) : null,
                    accepted = has && r!.Accepted
                };
            }).ToList();

        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.FbMilkSourceMatch WHERE RunId=@run", new { run = runId }, tx, cancellationToken: ct));
        if (payload.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var written = await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbMilkSourceMatch
                (RunId, SalesDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                 LastSampleAt, OptionCount, Plays, Outcome, Reason, Evidence, Accepted)
              VALUES (@run, @d, @mid, @no, @lg, @home, @away, @time,
                      @at, @opts, @plays, @outcome, @reason, @evidence, @accepted)",
            payload, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return written;
    }

    private static string? Trim120(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : (s.Length > 118 ? s[..118] : s);

    private static string? Trim200(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : (s.Length > 198 ? s[..198] : s);

    private static string? Trim300(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : (s.Length > 280 ? s[..280] : s);

    /// <summary>
    /// 榜面选轮的公共片段（2026-11-13 第三轮）。
    ///
    /// 语义：给定受注日，取**最近一个成功发布**的轮次；没有新口径的轮次时退回 null，
    /// 由调用方按「旧口径（RunId IS NULL）」处理历史数据。
    /// 排序用 `StartedAt` 而不是 `RunId` 字符串——RunId 只是标识，不是状态。
    /// </summary>
    private const string PublishedRunSql =
        @"DECLARE @run nvarchar(40) =
              (SELECT TOP 1 rd.RunId
                 FROM dbo.FbMilkRunDate rd
                 JOIN dbo.FbMilkRun r ON r.RunId = rd.RunId
                WHERE rd.SalesDate = @d AND r.Status = 'success'
                ORDER BY r.StartedAt DESC, rd.RunId DESC);";

    /// <summary>行级判据：这一行是否属于「该受注日的最新成功轮」，或属于旧口径（RunId IS NULL）。</summary>
    private const string PublishedRowSql =
        @"(p.RunId IS NULL OR EXISTS (
              SELECT 1 FROM dbo.FbMilkRunDate rd
              JOIN dbo.FbMilkRun r ON r.RunId = rd.RunId
              WHERE rd.RunId = p.RunId AND rd.SalesDate = p.PickDate AND r.Status = 'success'))";

    /// <summary>
    /// 「上过榜的一注」= 按业务键去重后的一行（2026-11-13 第四轮）。
    ///
    /// 为什么战绩不能照抄榜面的「最新成功轮」口径（用户 11-13 明确要求）：
    ///   · 榜面回答「现在买什么」，只能有最新一版；
    ///   · 战绩回答「我们推荐过什么、结果如何」，**推荐过就是推荐过**——
    ///     后面那轮把它换掉，不该让这一注从中奖记录里消失。
    /// 为什么也不能直接把所有轮的行都算进去：保留历史之后，同一注在 3~5 轮里各有一份副本，
    /// 直接统计等于把同一注算 3~5 次，命中率会被复制放大。
    ///
    /// 所以口径是「按 (受注日, 场次, 玩法, 档位) 去重，取最早那一份」：
    ///   · 赔率 / 概率 = **首次上榜**那份（事后重算不算数，与空跑台账同口径）；
    ///   · 命中 / 结算 = 取所有副本里「任一份判过」的结果（`AnyHit`），
    ///     避免结算只落在某一份副本上时把命中丢掉；
    ///   · **撤注 = 末份**，但**已冻结的注以台账为准**（2026-11-13 第五轮）：
    ///     台账是「照榜单买会怎样」的凭证，结算即终态——结算之后再来一轮把同一注标成撤注，
    ///     不该让这一注从战绩里消失（隔离库已复现：报表说撤注、台账说已下注，两边对不上）。
    ///   · 只认「属于某个成功发布轮」的行 + 升级前 `RunId IS NULL` 的历史行，
    ///     失败轮/采集不完整轮写进去的孤儿行不参与统计。
    ///
    /// 升级前已经在库里的已开赛、已结算、已撤注、已冻结记录全部满足 `RunId IS NULL`，
    /// 所以它们一定被包含在内——这是「切换轮次不许让历史消失」的地方。
    /// </summary>
    private static string BoardedOneCte(bool withPaper)
    {
        const string P = "PARTITION BY p.PickDate, p.MatchId, p.PlayType, p.OptionCode";

        // 台账联表只在表存在时拼进去：空库（还没跑过 paper-freeze）不能因此把战绩页打挂。
        var join = withPaper
            ? @"LEFT JOIN dbo.FbPaperTrade t
                       ON t.TradeDate = p.PickDate AND t.MatchId = p.MatchId
                      AND t.PlayType = p.PlayType AND t.OptionCode = p.OptionCode"
            : string.Empty;

        var anyHit = withPaper
            ? $"COALESCE(MAX(CAST(t.Hit AS int)) OVER ({P}), MAX(CAST(p.Hit AS int)) OVER ({P}))"
            : $"MAX(CAST(p.Hit AS int)) OVER ({P})";

        var anySettled = withPaper
            ? $"MAX(CASE WHEN p.SettledAt IS NOT NULL OR t.SettledAt IS NOT NULL THEN 1 ELSE 0 END) OVER ({P})"
            : $"MAX(CASE WHEN p.SettledAt IS NOT NULL THEN 1 ELSE 0 END) OVER ({P})";

        var finalVoided = withPaper
            ? $"COALESCE(MAX(CAST(t.Voided AS int)) OVER ({P}), FIRST_VALUE(CAST(p.Voided AS int)) OVER ({P} ORDER BY p.PickId DESC))"
            : $"FIRST_VALUE(CAST(p.Voided AS int)) OVER ({P} ORDER BY p.PickId DESC)";

        return
            $@"WITH base AS (
              SELECT p.PlayType, p.OptionCode, p.Odds, p.ModelProbability, p.Ev, p.PickDate, p.MatchId, p.PickId,
                     CASE WHEN p.RunId IS NULL THEN 1 ELSE 0 END AS IsLegacy,
                     {anyHit} AS AnyHit,
                     {anySettled} AS AnySettled,
                     {finalVoided} AS FinalVoided,
                     ROW_NUMBER() OVER ({P} ORDER BY p.PickId) AS rn
              FROM dbo.FbMilkPick p
              {join}
              WHERE p.PickDate >= @from AND " + PublishedRowSql + @"
          ),
          one AS (SELECT * FROM base WHERE rn = 1)
          ";
    }

    /// <summary>战绩口径的 CTE；台账表还没建时自动降级成不联表版本（空库不能把战绩页打挂）。</summary>
    private static async Task<string> CteAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        var hasPaper = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.FbPaperTrade', N'U') IS NULL THEN 0 ELSE 1 END",
            cancellationToken: ct)) == 1;
        return BoardedOneCte(hasPaper);
    }

    /// <summary>
    /// 写一条作业级日志到 <c>dbo.SyncLog</c>（2026-11-13 加）。
    ///
    /// 只为一件事：存档这类「审计材料」写失败时，库里要留下**可定位**的一行，
    /// 而不是被 `catch { }` 吞掉之后只剩「表里没数据」。行里带输入行数、写入行数、异常类型与消息，
    /// 一眼能分清是「本次没有输入」还是「有输入但写坏了」。
    ///
    /// 写日志本身失败绝不影响出票——它不是出票依赖。
    /// </summary>
    public async Task LogAsync(string task, string status, int rows, string message, CancellationToken ct = default)
    {
        try
        {
            if (message.Length > 1800) message = message[..1800];
            await using var conn = await _factory.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.SyncLog (TaskName, Source, StartedAt, FinishedAt, Status, RowsAffected, Message)
                  VALUES (@t, N'milk', SYSDATETIME(), SYSDATETIME(), @s, @r, @m)",
                new { t = task, s = status, r = rows, m = message }, cancellationToken: ct));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 日志写不进去不能影响主流程，也不该掩盖真正的异常 */ }
    }

    /// <summary>
    /// 当日方向闸台账。背离的排前面，其余按开赛时间——页面上先看被挡掉的。
    ///
    /// 2026-11-13 第二轮：闸表改成按轮追加、保留历史（一行不再互相覆盖），
    /// 所以读的时候必须钉住最新一轮，否则同一场会出现三四行、Verdict 各不相同。
    /// 当日一行 RunId 都没有（旧口径的历史补档）时退回「全部」，保持老页面行为。
    /// </summary>
    public async Task<List<MilkDirectionDiagnosis>> GetGateAsync(DateTime gateDate, CancellationToken ct = default)
    {
        var d = gateDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureGateRunColumnAsync(conn, ct);
        await EnsureRunSchemaAsync(conn, ct);
        return (await conn.QueryAsync<MilkDirectionDiagnosis>(new CommandDefinition(
            // ★ 2026-11-13 第三轮：选轮改用**状态**，不再用 MAX(RunId)。
            // 闸门与榜面必须指向同一轮，否则会出现「榜上是这一轮、诊断是上一轮」。
            @"DECLARE @run nvarchar(40) =
                  (SELECT TOP 1 r.RunId FROM dbo.FbMilkRun r
                    WHERE r.RunDate=@d AND r.Status='success'
                    ORDER BY r.StartedAt DESC, r.RunId DESC);
              SELECT MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     ModelDirection, ModelHomePct, ModelDrawPct, ModelAwayPct, PickDirection,
                     MarketDirection, MarketSource, MarketHomePct, MarketDrawPct, MarketAwayPct,
                     AsianDirection, AsianOpen, AsianClose, ExchangeDirection, RatioHome, RatioAway,
                     Verdict, AlertCode, AlertNote, BlockedPicks, BlockedBest, BlockedBestProbability
              FROM dbo.FbMilkGate
              WHERE GateDate=@d AND (@run IS NULL OR RunId=@run)
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
        // 榜面要读 AlertSeverity/Voided 两列，老库第一次跑时它们还不存在——
        // 补列放在查询前，读榜不能因为「还没人跑过出榜作业」而整页 500。
        await EnsureColumnsAsync(conn, ct);
        await EnsureRunSchemaAsync(conn, ct);
        var rows = (await conn.QueryAsync<MilkPickRow>(new CommandDefinition(
            // ★ 2026-11-13 第三轮：**只读该受注日最新一个成功发布轮**。
            // 四张表保留历史之后，同一天库里会躺着好几轮的注单，不带这个条件就会
            // 把失败轮、被替换轮的行一起显示出来（同一场出现三四行、赔率各不相同）。
            // 没有成功轮次（历史日期、旧口径）时 @run 为 NULL，退回「整日全读」。
            PublishedRunSql +
            @"SELECT PickId, PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, Ev, CounterHigh,
                     BaselineProbability, BaselineEv, ModelLowConfidence, ModelOverconfident,
                     DivergenceTag, DivergenceNote,
                     HalfDrawRefined, FatigueTag, ModelVsMarketRatio, HomePriceRich, ModelPctPure,
                     ModelVersion,
                     AlertSeverity, AlertReason, Voided, VoidedAt,
                     ActualScore, ActualHalfScore, ActualGoals, Hit, SettledAt
              FROM dbo.FbMilkPick
              WHERE PickDate=@d AND (@run IS NULL OR RunId=@run)
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

    /// <summary>
    /// 撤榜明细（2026-11-09 加，配合前端「撤榜展示区」）。两类来源，语义完全不同，前端必须分开写：
    ///
    ///   ① <c>voided</c> —— 临场告警主动撤注：有原因、有撤下时间，是风控动作。
    ///   ② <c>replaced</c> —— 曾经上过榜、当前榜面已经没有它：后续重算把这一档换掉或被筛掉了。
    ///      这类注在 FbMilkPick 里是被 **整行 DELETE** 的（重写当日榜），要不是空跑台账
    ///      每 10 分钟冻一次，它们完全无痕。11-08 曼联「胜平 @13.00」被换成「平平 @6.15」
    ///      就是这么消失的——用户第二天问「这注你到底推没推过」，系统当时答不出来。
    ///
    /// 为什么用台账做差集、而不另建一张「撤榜表」：台账是只增不改的原始记录，
    /// 差集天然自愈——重算把某注换回来，它就自动从撤榜区消失，不需要额外的状态机去同步。
    /// </summary>
    public async Task<List<MilkVoidedRow>> GetVoidedAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureColumnsAsync(conn, ct);
        await EnsureRunSchemaAsync(conn, ct);

        // 台账表由 paper-freeze 建，可能还不存在（新库/首次部署）。缺表就只给①类，不能整页 500。
        var hasPaper = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.FbPaperTrade', N'U') IS NULL THEN 0 ELSE 1 END",
            cancellationToken: ct)) == 1;

        // 2026-11-13 第三轮：撤榜区也钉住轮次。
        //
        // 2026-11-13 第四轮修正（用户要求「撤注记录不能因切换轮次而消失」）：
        // **撤注分支不再只认最新轮**。它原来写的是 `@run`，于是一旦下一次重算产出了新一轮，
        // 昨天那注被撤的记录就从撤榜区消失了——而撤注是风控决定，必须在切轮之后仍然可见。
        // 现在的判据是「属于任何一个成功发布的轮次 + 按业务键去重」：
        // 撤了就是撤了，不会因为榜面换代而被抹掉。`replaced` 分支仍按最新轮判，
        // 因为它的语义是「当前榜面没有了」，本来就该跟着榜面走。
        var sql = PublishedRunSql + @"
WITH v AS (
    SELECT p.MatchId, p.MatchNo, p.LeagueName, p.HomeTeam, p.AwayTeam, p.MatchTime,
           p.PlayType, p.OptionCode, p.OptionName, p.Odds, p.AlertSeverity, p.AlertReason,
           p.VoidedAt, p.ActualScore, p.Hit, p.SettledAt,
           ROW_NUMBER() OVER (PARTITION BY p.PickDate, p.MatchId, p.PlayType, p.OptionCode
                              ORDER BY p.PickId) AS rn
    FROM dbo.FbMilkPick p
    WHERE p.PickDate=@d AND p.Voided=1 AND " + PublishedRowSql + @"
)
SELECT N'voided' AS Kind, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
       PlayType, OptionCode, OptionName, Odds, AlertSeverity, AlertReason,
       VoidedAt, CAST(NULL AS datetime2) AS FrozenAt, ActualScore, Hit, SettledAt
FROM v WHERE rn=1";

        if (hasPaper)
            sql += @"
UNION ALL
SELECT N'replaced', t.MatchId, t.MatchNo, t.LeagueName, t.HomeTeam, t.AwayTeam, t.MatchTime,
       t.PlayType, t.OptionCode, t.OptionName, t.Odds,
       CAST(NULL AS nvarchar(8)) AS AlertSeverity, CAST(NULL AS nvarchar(400)) AS AlertReason,
       CAST(NULL AS datetime2) AS VoidedAt, t.FrozenAt, t.ActualScore, t.Hit, t.SettledAt
FROM dbo.FbPaperTrade t
WHERE t.TradeDate=@d
  -- 「当前榜面没有它」= 不在最新成功轮里。不能只判「库里有这行」——
  -- 保留历史之后，被替换的注一直在库里，那样它永远不会出现在撤榜区。
  AND NOT EXISTS (SELECT 1 FROM dbo.FbMilkPick p
                   WHERE p.PickDate=@d AND p.MatchId=t.MatchId
                     AND p.PlayType=t.PlayType AND p.OptionCode=t.OptionCode
                     AND (@run IS NULL OR p.RunId=@run))
  -- 同一档历史上有过重复冻结（早期幂等保护缺失）时只取最早那一行，避免撤榜区出现重复行。
  AND t.TradeId = (SELECT MIN(x.TradeId) FROM dbo.FbPaperTrade x
                    WHERE x.TradeDate=@d AND x.MatchId=t.MatchId
                      AND x.PlayType=t.PlayType AND x.OptionCode=t.OptionCode)";

        sql += " ORDER BY MatchNo, PlayType";

        return (await conn.QueryAsync<MilkVoidedRow>(
            new CommandDefinition(sql, new { d }, cancellationToken: ct))).ToList();
    }

    /// <summary>指定日期之前最近一个有大奶注的日期（跨午夜回退用），没有则 null。</summary>
    public async Task<DateTime?> GetLatestPickDateBeforeAsync(DateTime pickDate, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            // 只认上过榜的行：失败轮的日期不该被当成「上一个有榜的日子」翻出来。
            @"SELECT MAX(p.PickDate) FROM dbo.FbMilkPick p
              WHERE p.PickDate < @d AND " + PublishedRowSql,
            new { d = pickDate.Date }, cancellationToken: ct));
    }

    /// <summary>
    /// 历史口径核对（2026-11-13 第四轮，**只读**）。
    ///
    /// 回答用户点名的那件事：「升级前已经开赛、结算、撤注或冻结的记录，
    /// 会不会因为榜面改成按最新轮读取而消失」。
    /// 做法是把两种口径并排算出来，让差异可见，而不是靠推理保证：
    ///   old    逐行统计（升级前的老口径：不去重、不看轮次）
    ///   new    上过榜的一注（本次口径：按业务键去重 + 只认成功发布轮 / 升级前 NULL 行）
    ///   legacy 升级前 `RunId IS NULL` 且已结算的行数，以及它在新口径里的存活数
    /// `old - new` 应当恰好等于「多副本重复数」——多出来的不是历史丢失，而是同一注被记了几次。
    /// 若 `legacy` 与 `legacyInNew` 不等，说明历史真的被吞了，核对要报错。
    ///
    /// 不写库，可在生产库直接跑。
    /// </summary>
    public async Task<MilkLedgerAudit> GetLedgerAuditAsync(int days, CancellationToken ct = default)
    {
        var span = Math.Max(1, days);
        var from = DateTime.Today.AddDays(-(span - 1));
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureRunSchemaAsync(conn, ct);
        var cte = await CteAsync(conn, ct);

        var head = await conn.QuerySingleAsync<MilkLedgerAuditHead>(new CommandDefinition(
            @"SELECT COUNT(*) AS AllRows,
                     COUNT(DISTINCT CONVERT(varchar(10), PickDate, 23) + '|' + CAST(MatchId AS varchar(20))
                                    + '|' + PlayType + '|' + OptionCode) AS DistinctBets,
                     SUM(CASE WHEN RunId IS NULL THEN 1 ELSE 0 END) AS LegacyRows,
                     SUM(CASE WHEN RunId IS NOT NULL THEN 1 ELSE 0 END) AS RunRows,
                     SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END) AS SettledRows,
                     SUM(CASE WHEN ISNULL(Voided,0)=1 THEN 1 ELSE 0 END) AS VoidedRows,
                     SUM(CASE WHEN RunId IS NULL AND Hit IS NOT NULL THEN 1 ELSE 0 END) AS LegacySettled
              FROM dbo.FbMilkPick
              WHERE PickDate >= @from",
            new { from }, cancellationToken: ct));

        // 旧口径：逐行、不看轮次（= 升级前的战绩算法）
        var oldRows = (await conn.QueryAsync<MilkLedgerAuditRow>(new CommandDefinition(
            @"SELECT PlayType, COUNT(*) AS OldTotal,
                     SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) AS OldHit
              FROM dbo.FbMilkPick
              WHERE PickDate >= @from AND Hit IS NOT NULL
              GROUP BY PlayType",
            new { from }, cancellationToken: ct))).ToList();

        // 新口径：上过榜的一注（与 GetReportAsync 同一段 CTE）
        var newRows = (await conn.QueryAsync<MilkLedgerAuditRow>(new CommandDefinition(
            cte +
            @"SELECT PlayType, COUNT(*) AS NewTotal,
                     SUM(CASE WHEN AnyHit = 1 THEN 1 ELSE 0 END) AS NewHit,
                     SUM(CASE WHEN IsLegacy = 1 THEN 1 ELSE 0 END) AS NewLegacyTotal,
                     SUM(CASE WHEN IsLegacy = 1 AND AnyHit IS NOT NULL THEN 1 ELSE 0 END) AS NewLegacySettled
              FROM one
              WHERE AnyHit IS NOT NULL
              GROUP BY PlayType",
            new { from }, cancellationToken: ct))).ToList();

        var rows = oldRows
            .Select(o =>
            {
                var n = newRows.FirstOrDefault(x => x.PlayType == o.PlayType);
                return new MilkLedgerAuditRow
                {
                    PlayType = o.PlayType,
                    OldTotal = o.OldTotal, OldHit = o.OldHit,
                    NewTotal = n?.NewTotal ?? 0, NewHit = n?.NewHit ?? 0,
                    NewLegacyTotal = n?.NewLegacyTotal ?? 0,
                    NewLegacySettled = n?.NewLegacySettled ?? 0
                };
            })
            .OrderBy(r => r.PlayType, StringComparer.Ordinal)
            .ToList();

        return new MilkLedgerAudit
        {
            Days = span,
            From = from,
            AllRows = head.AllRows,
            DistinctBets = head.DistinctBets,
            LegacyRows = head.LegacyRows,
            RunRows = head.RunRows,
            SettledRows = head.SettledRows,
            VoidedRows = head.VoidedRows,
            LegacySettled = head.LegacySettled,
            LegacySettledInNew = rows.Sum(r => r.NewLegacySettled),
            OldSettled = rows.Sum(r => r.OldTotal),
            OldHit = rows.Sum(r => r.OldHit),
            NewSettled = rows.Sum(r => r.NewTotal),
            NewHit = rows.Sum(r => r.NewHit),
            Plays = rows
        };
    }

    private sealed class MilkLedgerAuditHead
    {
        public int AllRows { get; set; }
        public int DistinctBets { get; set; }
        public int LegacyRows { get; set; }
        public int RunRows { get; set; }
        public int SettledRows { get; set; }
        public int VoidedRows { get; set; }
        public int LegacySettled { get; set; }
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
        await EnsureRunSchemaAsync(conn, ct);
        var cte = await CteAsync(conn, ct);

        // 2026-11-13 第四轮：战绩口径从「最新成功轮」改成「上过榜的一注」。
        // 上一版照抄了榜面的 `@run` 过滤，后果是**历史战绩会随今天重算而消失**：
        // 昨天那注（RunId 属于昨天那个轮次）在今天重算后仍在库里，却因为不在「今天的最新轮」而被排除。
        // 现在的判据见 BoardedOneCte：按业务键去重 + 只要那一轮真的发布过就算数。
        //
        // ★ 2026-11-13 第五轮：**撤注不进分母**（`FinalVoided = 0`），与空跑台账同一口径。
        // 隔离库验证复现过：撤注注被算进命中率分母，回报却是 0（本来就没下注），
        // 分子分母都对不上。撤注注单独计数，用来评价撤注判据（`VoidedSettled`）。
        var groupSql =
            @"SELECT PlayType,
                     COUNT(*) AS Total,
                     SUM(CASE WHEN AnyHit = 1 THEN 1 ELSE 0 END) AS Hit,
                     ISNULL(SUM(Odds), 0) AS TotalOdds,
                     ISNULL(SUM(CASE WHEN AnyHit = 1 THEN Odds ELSE 0 END), 0) AS HitOdds,
                     CAST(ISNULL(AVG(Odds), 0) AS DECIMAL(8,2)) AS AvgOdds,
                     CAST(ISNULL(AVG(ModelProbability), 0) AS DECIMAL(6,2)) AS AvgProbability,
                     CAST(ISNULL(AVG(Ev), 0) AS DECIMAL(8,3)) AS AvgEv
              FROM one
              WHERE AnyHit IS NOT NULL AND FinalVoided = 0";

        var plays = (await conn.QueryAsync<MilkPlayStat>(new CommandDefinition(
            cte + groupSql + " GROUP BY PlayType ORDER BY PlayType",
            new { from }, cancellationToken: ct))).ToList();

        var overall = await conn.QuerySingleOrDefaultAsync<MilkPlayStat>(new CommandDefinition(
            cte + groupSql.Replace("SELECT PlayType,", "SELECT 'ALL' AS PlayType,"),
            new { from }, cancellationToken: ct));

        // 撤注对照组：已撤注 = 没下这笔，不进分子分母，但赛果照记，用来评价撤注判据。
        const string voidSql =
            @"SELECT COUNT(*) AS Total,
                     SUM(CASE WHEN AnyHit = 1 THEN 1 ELSE 0 END) AS Hit
              FROM one
              WHERE AnyHit IS NOT NULL AND FinalVoided = 1";

        var voidedByPlay = (await conn.QueryAsync<(string PlayType, int Total, int Hit)>(new CommandDefinition(
            cte + @"SELECT PlayType, COUNT(*) AS Total,
                     SUM(CASE WHEN AnyHit = 1 THEN 1 ELSE 0 END) AS Hit
              FROM one
              WHERE AnyHit IS NOT NULL AND FinalVoided = 1
              GROUP BY PlayType",
            new { from }, cancellationToken: ct))).ToList();

        var voidedTotal = await conn.QuerySingleAsync<(int Total, int Hit)>(new CommandDefinition(
            cte + voidSql, new { from }, cancellationToken: ct));

        // 取消场次写了 SettledAt 终态（Hit 仍 NULL、ActualScore='取消'），不算 pending。
        // 判据用 AnySettled 而不是首行的 SettledAt：取消标记可能只落在某一副本上。
        var pending = await conn.QuerySingleAsync<int>(new CommandDefinition(
            cte + "SELECT COUNT(*) FROM one WHERE AnyHit IS NULL AND AnySettled = 0",
            new { from }, cancellationToken: ct));

        var top = (await conn.QueryAsync<MilkOptionStat>(new CommandDefinition(
            cte +
            @"SELECT TOP 12 PlayType, OptionCode,
                     COUNT(*) AS Total,
                     SUM(CASE WHEN AnyHit = 1 THEN 1 ELSE 0 END) AS Hit,
                     CAST(ISNULL(AVG(Odds), 0) AS DECIMAL(8,2)) AS AvgOdds
              FROM one
              WHERE AnyHit IS NOT NULL AND FinalVoided = 0
              GROUP BY PlayType, OptionCode
              HAVING COUNT(*) >= 3
              ORDER BY COUNT(*) DESC, SUM(CASE WHEN AnyHit = 1 THEN 1 ELSE 0 END) DESC",
            new { from }, cancellationToken: ct))).ToList();

        // 撤注对照：总体与分玩法各填一次。撤注注不进上面的分子分母，只报「撤掉的注后来中没中」。
        var voidedPlays = voidedByPlay.ToDictionary(x => x.PlayType, x => (x.Total, x.Hit), StringComparer.Ordinal);
        foreach (var p in plays)
            if (voidedPlays.TryGetValue(p.PlayType, out var v))
            {
                p.Voided = v.Total;
                p.VoidedHit = v.Hit;
            }

        var overallRow = overall ?? new MilkPlayStat { PlayType = "ALL" };
        overallRow.Voided = voidedTotal.Total;
        overallRow.VoidedHit = voidedTotal.Hit;

        return new MilkStatsReport
        {
            Days = span,
            From = from,
            Overall = overallRow,
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
        // ★ 补列必须在这里也调一次（2026-11-10）：下面 SELECT 引用 p.Handicap，
        //   而这一列是本次新加的。SettleAsync 原本只有 SaveDayAsync 那条路径会补列，
        //   一旦结算先跑（或者进程刚重启 → _columnsReady 被重置后结算比保存先到），
        //   就会报「列名 Handicap 无效」。更糟的是调用方 RecommendationService 用
        //   try/catch 包着它（结算失败不能拖垮整轮采集），异常被吞 → 结算静默停摆，
        //   症状是「注一直挂在那里没有结果」，和 11-09 那次取消误判的表现一模一样。
        await EnsureColumnsAsync(conn, ct);

        var pending = (await conn.QueryAsync<MilkPendingRow>(new CommandDefinition(
            @"SELECT p.PickId, p.PlayType, p.OptionCode,
                     COALESCE(p.Handicap, m.Handicap) AS Handicap,
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
            else if (row.PlayType == JcPlayTypes.Nspf && row.Handicap is not decimal)
            {
                // 让球胜平负拿不到让球数就**不许判**。判 false 等于把这注记成未中，
                // 直接污染命中率；跳过只损失一轮及时性，下一轮读到了自然补上。
                // 与半全场缺半场比分同一个处理方式。
                continue;
            }
            else
            {
                hit = MilkPickMath.IsHit(row.PlayType, row.OptionCode,
                    row.HomeScore!.Value, row.AwayScore!.Value, null, null, row.Handicap);
            }

            var score = $"{row.HomeScore}:{row.AwayScore}";
            var half = row.HalfHomeScore is int h1 && row.HalfAwayScore is int h2 ? $"{h1}:{h2}" : null;
            var goals = row.HomeScore + row.AwayScore;

            settled += await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbMilkPick
                  SET Hit=@hit, ActualScore=@score, ActualHalfScore=@half, ActualGoals=@goals,
                      Handicap=COALESCE(Handicap, @hcp),
                      SettledAt=SYSDATETIME()
                  WHERE PickId=@id",
                new { hit, score, half, goals, hcp = row.Handicap, id = row.PickId }, cancellationToken: ct));
        }

        // 取消场次（Status=2）永远等不到赛果：写死「取消」终态退出 pending。
        // Hit 保持 NULL 不进战绩分母，ActualScore='取消' 当标记。
        //
        // 2026-11-06 修 bug：数据源在比赛「未开赛」时会把 Status 短暂标成 2 又改回 0/1，
        // 旧逻辑只要 Status=2 就写死「取消」，导致还没开赛的注被永久误杀、退出结算队列。
        // 加两个前置条件：① 开赛时间已过（m.MatchTime < 当前时刻）；② 比分仍为 NULL。
        // 只有「时间过了 + 比分仍空 + Status=2」才是真取消；未开赛的短暂波动绝不判取消。
        //
        // ★ 2026-11-09 再修：上面两条还不够——**比赛正在进行中**也满足它们（开赛时间已过、比分还没采到），
        // 而数据源在比赛进行中会把 Status 短暂刷成 2。11-09 04:19 paper-settle 就这样把正在踢的
        // 朗斯-马赛（实际 1:2）写死成「取消」终态。改为开赛后 150 分钟仍无比分才算取消，
        // 与结算判据用同一条时间线，避免两张表各自跑出一套「取消」。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE p SET p.ActualScore = N'取消', p.SettledAt = SYSDATETIME()
              FROM dbo.FbMilkPick p
              JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
              WHERE p.Hit IS NULL AND p.SettledAt IS NULL AND m.Status = 2
                AND m.MatchTime < DATEADD(minute, -150, SYSDATETIME())
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
    public async Task<TurnAlertSaveResult> SaveTurnAlertsAsync(
        DateTime alertDate, IReadOnlyList<TurnAlertRow> alerts, CancellationToken ct = default)
    {
        var d = alertDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        // 补列必须在开事务之前（否则命令要自带 Transaction，见 SaveDayAsync 注释）。
        await EnsureColumnsAsync(conn, ct);

        // ★ 读取窗口必须是 ±1 天，不能只读 @d（2026-11-10 修）。
        //
        // 病因：通宵场次（03:00 开赛）的注是**前一天**落的，PickDate 停在前一天；
        // 而 turn-guard 用的是 DateTime.Today。23:50 那次扫描写下的告警 AlertDate=昨天，
        // 00:10 那次扫描 AlertDate=今天——只按 @d 读的话两次互相看不见，
        // 同一注会被 INSERT 两行，而旧的那行永远进不了 stale 清理（清理只看 existing）。
        // 结果是一场比赛在告警表里堆好几行，去重键 (MatchId,PlayType,OptionCode) 形同虚设。
        var existing = (await conn.QueryAsync<TurnAlertRow>(new CommandDefinition(
            @"SELECT AlertId, AlertDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PickOption, Reason, Severity, ScoreDiagTag, ExpGdOpen, ExpGdNow, ExpGdMove, CreatedAt,
                     ISNULL(PickId,0) AS PickId, ISNULL(PlayType,'') AS PlayType,
                     ISNULL(OptionCode,'') AS OptionCode, SignalCode, LastSeenAt
              FROM dbo.FbTurnAlert
              WHERE AlertDate >= DATEADD(day, -1, @d) AND AlertDate <= DATEADD(day, 1, @d)",
            new { d }, cancellationToken: ct))).ToList();

        // 键 = (MatchId, PlayType, OptionCode)：只认「哪一注」，不认 Reason 文案。
        static string KeyOf(long mid, string? pt, string? oc) => $"{mid}|{pt}|{oc}";
        var byKey = existing
            .GroupBy(x => KeyOf(x.MatchId, x.PlayType, x.OptionCode))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AlertId).First());

        await using var tx = await conn.BeginTransactionAsync(ct);

        var inserted = 0;
        var updated = 0;
        var newHigh = 0;
        var keep = new HashSet<string>(StringComparer.Ordinal);

        foreach (var a in alerts)
        {
            if (a.MatchId <= 0) continue;
            var key = KeyOf(a.MatchId, a.PlayType, a.OptionCode);
            keep.Add(key);

            if (byKey.TryGetValue(key, out var old))
            {
                updated += await conn.ExecuteAsync(new CommandDefinition(
                    @"UPDATE dbo.FbTurnAlert
                      SET AlertDate=@ad, Reason=@reason, Severity=@sev, SignalCode=@sig, ScoreDiagTag=@tag,
                          ExpGdOpen=@open, ExpGdNow=@now, ExpGdMove=@move, PickId=@pid,
                          PickOption=@pick, MatchTime=@time, LastSeenAt=SYSDATETIME(),
                          ClearedAt=NULL
                      WHERE AlertId=@id",
                    new
                    {
                        // AlertDate 一起前移（2026-11-10）：告警是「当前还在触发」的状态，
                        // 跟着滚动到今天，监控页（按 AlertDate=今天查）才不会在零点后把它漏掉。
                        ad = d,
                        reason = a.Reason, sev = a.Severity, sig = a.SignalCode, tag = a.ScoreDiagTag,
                        open = a.ExpGdOpen, now = a.ExpGdNow, move = a.ExpGdMove, pid = a.PickId,
                        pick = a.PickOption, time = a.MatchTime, id = old.AlertId
                    }, tx, cancellationToken: ct));

                // 只有「从无到有」或「mid→high」才算新增高危，避免每 10 分钟重复触发重算。
                if (a.Severity == "high" && old.Severity != "high") newHigh++;
                continue;
            }

            inserted += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbTurnAlert
                    (AlertDate, MatchId, PickId, PlayType, OptionCode, MatchNo, LeagueName,
                     HomeTeam, AwayTeam, MatchTime, PickOption, Reason, Severity, SignalCode,
                     ScoreDiagTag, ExpGdOpen, ExpGdNow, ExpGdMove, CreatedAt, LastSeenAt)
                  VALUES (@d, @mid, @pid, @play, @code, @no, @lg,
                          @home, @away, @time, @pick, @reason, @sev, @sig,
                          @tag, @open, @now, @move, SYSDATETIME(), SYSDATETIME())",
                new
                {
                    d, mid = a.MatchId, pid = a.PickId, play = a.PlayType, code = a.OptionCode,
                    no = a.MatchNo, lg = a.LeagueName,
                    home = a.HomeTeam, away = a.AwayTeam, time = a.MatchTime,
                    pick = a.PickOption, reason = a.Reason, sev = a.Severity, sig = a.SignalCode,
                    tag = a.ScoreDiagTag, open = a.ExpGdOpen, now = a.ExpGdNow, move = a.ExpGdMove
                }, tx, cancellationToken: ct));
            if (a.Severity == "high") newHigh++;
        }

        // 信号消失就撤掉告警行——否则「早上的高危」会一直挂在榜上，用户以为现在还是高危。
        // 只清「还没开赛」的场次：已开赛的场次不再重算，保留告警作为事后凭证。
        //
        // 2026-11-10 起改为软删除（打 ClearedAt，不再 DELETE 整行）。理由见 DDL 里那段：
        // 物理删除让「这注曾经高危过、后来又消失」彻底不可考，撤注判据因此永远攒不出回测样本。
        // 所有读告警的地方都必须加 ClearedAt IS NULL，否则消失的告警会复活。
        var stale = existing
            .Where(x => !keep.Contains(KeyOf(x.MatchId, x.PlayType, x.OptionCode)))
            .Where(x => x.MatchTime is null || x.MatchTime > DateTime.Now)
            .Select(x => x.AlertId)
            .ToList();
        foreach (var id in stale)
            await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbTurnAlert
                  SET ClearedAt = SYSDATETIME()
                  WHERE AlertId = @id AND ClearedAt IS NULL", new { id }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new TurnAlertSaveResult
        {
            Inserted = inserted, Updated = updated, Cleared = stale.Count, NewHigh = newHigh
        };
    }

    /// <summary>
    /// 把当日告警**回写到注单本身**（2026-11-08 加）。这是「监控 → 榜单」闭环的最后一环。
    ///
    /// 之前断在这一环：turn-guard 每 10 分钟把告警写进 FbTurnAlert，推荐页那张卡片一个字都不变。
    /// 用户看到的是「这注还在榜上、还是那个 EV」，于是照买——而系统在他背后已经喊了十几次。
    /// 2026-11-08 诺丁汉森林 3:1 曼城 就是这么丢的：16:45 起十几条 high 告警，选注照旧押曼城。
    ///
    /// 回写规则：
    ///   high → 上榜红标 + **撤注**（Voided=1，不可逆，重算也不会把它放回主榜）
    ///   mid  → 只上榜红标，不撤注（单信号不足以下撤注决心，见 TurnGuard 高危判据）
    /// 已无告警的注清空红标，但**不解除已发生的撤注**——撤了就是撤了，不因为市场暂时回摆又放回榜上。
    /// </summary>
    public async Task<(int Marked, int Voided)> ApplyTurnAlertsToPicksAsync(
        DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureColumnsAsync(conn, ct);
        await EnsureRunSchemaAsync(conn, ct);

        // 同一注可能有多行历史告警（早期无去重的数据），取最新一行。
        //
        // ★★ 这里**绝对不能**再按 p.PickDate=@d 过滤（2026-11-10 修，本条链路此前从未真正生效）。
        //
        // 病因是两个日期走的不是同一个钟：
        //   FbMilkPick.PickDate  = SaveDayAsync 落库那一刻的日期（出注日）
        //   GetOpenDayAsync      = 不按日期取，捞出所有「未结算 + 未开赛」的注（11-07 为跨午夜改的）
        //   本方法的 @d          = DateTime.Today（扫描那一刻的日期）
        // 于是每天 00:00 一过，通宵场次（03:00 开赛那批）就全部错位：
        // 注的 PickDate 还是昨天，@d 已经是今天 → 告警写进 FbTurnAlert、监控页看得见，
        // 但这一条 UPDATE 一行都匹配不上，推荐页那一注永远不会被红标、更不会被撤。
        // 「监控到了却不动」的标准形态：告警页在喊，推荐页纹丝不动。
        //
        // 实测（事务内注入一条 AlertDate=次日 的 high 告警，同一注同一条告警）：
        //   带 p.PickDate=@d  → 影响 0 行
        //   去掉该条件        → 影响 1 行
        //
        // 匹配只靠 (MatchId, PlayType, OptionCode) 这个稳定三角：MatchId 全局唯一，
        // 不可能误伤别的受注日；再加上 SettledAt IS NULL，已结算的历史行也不会被翻出来。
        // 告警侧放宽到 ±1 天，是为了让「23:50 写下的告警」在 00:10 这一轮仍然生效。
        var marked = await conn.ExecuteAsync(new CommandDefinition(
            @"WITH latest AS (
                  SELECT MatchId, PlayType, OptionCode, Severity, Reason,
                         ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode
                                            ORDER BY AlertId DESC) AS rn
                  FROM dbo.FbTurnAlert
                  WHERE AlertDate >= DATEADD(day, -1, @d) AND AlertDate <= DATEADD(day, 1, @d)
                    AND ClearedAt IS NULL
              )
              UPDATE p
                 SET p.AlertSeverity = a.Severity,
                     p.AlertReason   = a.Reason,
                     p.Voided        = CASE WHEN a.Severity='high' THEN 1 ELSE p.Voided END,
                     p.VoidedAt      = CASE WHEN a.Severity='high' AND p.Voided=0 THEN SYSDATETIME()
                                            ELSE p.VoidedAt END
                FROM dbo.FbMilkPick p
                JOIN latest a
                  ON a.MatchId = p.MatchId AND a.PlayType = p.PlayType
                 AND a.OptionCode = p.OptionCode AND a.rn = 1
               WHERE p.SettledAt IS NULL AND " + PublishedRowSql,
            new { d }, cancellationToken: ct));

        // 告警消失 → 清红标（撤注不解除）。
        // 2026-11-09：已撤注（Voided=1）的行**不清原因**。撤注理由要留档，
        // 因为撤榜展示区就是靠 AlertReason 回答「为什么撤的」；清掉之后就只剩
        // 「未记录原因」，等于把撤注的唯一凭证也一起删了。未撤注的注照旧清。
        //
        // 2026-11-10：同样去掉 p.PickDate=@d —— 否则跨午夜那一批注永远不会被清标，
        // 信号消失后红标会一直挂在榜上（用户以为还在危险中）。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE p SET p.AlertSeverity = CASE WHEN ISNULL(p.Voided,0)=1 THEN p.AlertSeverity ELSE NULL END,
                          p.AlertReason   = CASE WHEN ISNULL(p.Voided,0)=1 THEN p.AlertReason   ELSE NULL END
                FROM dbo.FbMilkPick p
               WHERE p.SettledAt IS NULL AND p.AlertSeverity IS NOT NULL
                 AND " + PublishedRowSql + @"
                 AND NOT EXISTS (SELECT 1 FROM dbo.FbTurnAlert a
                                  WHERE a.MatchId=p.MatchId
                                    AND a.PlayType=p.PlayType AND a.OptionCode=p.OptionCode
                                    AND a.AlertDate >= DATEADD(day, -1, @d)
                                    AND a.AlertDate <= DATEADD(day, 1, @d)
                                    -- 2026-11-10 软删除配套：必须排除已清除的告警行。
                                    -- 漏了这一句，NOT EXISTS 永远为假，红标将永远清不掉，
                                    -- 而且看起来完全像「信号一直还在」——不报错、不告警。
                                    AND a.ClearedAt IS NULL)",
            new { d }, cancellationToken: ct));

        var voided = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.FbMilkPick p WHERE p.Voided=1 AND p.SettledAt IS NULL AND " + PublishedRowSql,
            new { d }, cancellationToken: ct));

        return (marked, voided);
    }

    /// <summary>
    /// 当日仍在生效的临场转向告警，按严重度降序、开赛时间正序。
    /// 2026-11-08 改：只返回**还没开赛**的场次——已开赛的场次榜单已不再展示，
    /// 继续挂在监控页只会让人以为「现在还有风险」。
    /// </summary>
    public async Task<List<TurnAlertRow>> GetTurnAlertsAsync(DateTime alertDate, CancellationToken ct = default)
    {
        var d = alertDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TurnAlertRow>(new CommandDefinition(
            @"SELECT AlertId, AlertDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PickOption, Reason, Severity, ScoreDiagTag, ExpGdOpen, ExpGdNow, ExpGdMove, CreatedAt,
                     ISNULL(PickId,0) AS PickId, ISNULL(PlayType,'') AS PlayType,
                     ISNULL(OptionCode,'') AS OptionCode, SignalCode, LastSeenAt
              FROM dbo.FbTurnAlert
              WHERE AlertDate=@d AND ClearedAt IS NULL
                AND (MatchTime IS NULL OR MatchTime > SYSDATETIME())
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
        await EnsureRunSchemaAsync(conn, ct);
        return (await conn.QueryAsync<MilkPickRow>(new CommandDefinition(
            // ★ 必须带上 BaselineProbability / BaselineEv（2026-11-08 修）。
            // 少了这两列，TurnGuard 的「实证 EV 跌破返奖率」判据拿到的永远是 null，
            // 于是 EV 破线的注一路被判成「无告警」——判据写了、字段没读，等于没写。
            //
            // ★ 2026-11-13 第三轮：只取**榜面上真出现过**的注（属于该受注日的最新成功轮）。
            // 保留历史之后，失败轮与已被替换的注也躺在库里；不筛就会
            // ①对根本没上过榜的注发告警、②对已被替换的旧票撤注（撤了也看不见）。
            // 跨午夜仍不按日期过滤（11-07 的口径），所以判据是「该行所属轮次对它自己那天是成功的」。
            //
            // ★ 2026-11-13 第四轮：在此基础上**按业务键去重**。
            // 保留历史之后同一注会有多份副本（每轮一份），不去重就会：
            //   ①同一注在告警表里被反复写（去重键虽然挡住了重复行，但 PickId 关联的是某一份副本）；
            //   ②撤注 UPDATE 命中多份副本，其中一份早已被替换、页面上根本看不到。
            // 取最早那份（首次上榜），与战绩/台账口径一致。
            @"WITH b AS (
                  SELECT p.PickId, p.PickDate, p.MatchId, p.MatchNo, p.LeagueName, p.HomeTeam, p.AwayTeam,
                         p.MatchTime, p.PlayType, p.OptionCode, p.OptionName, p.Odds, p.ModelProbability, p.Ev,
                         p.BaselineProbability, p.BaselineEv, p.ModelVsMarketRatio, p.ModelPctPure,
                         p.AlertSeverity, p.Voided,
                         p.ActualScore, p.ActualHalfScore, p.ActualGoals, p.Hit, p.SettledAt,
                         ROW_NUMBER() OVER (
                             PARTITION BY p.PickDate, p.MatchId, p.PlayType, p.OptionCode
                             ORDER BY p.PickId) AS rn
                  FROM dbo.FbMilkPick p
                  JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
                  WHERE p.Hit IS NULL AND p.SettledAt IS NULL
                    AND m.Status <> 3 AND m.MatchTime > SYSDATETIME()
                    AND " + PublishedRowSql + @"
              )
              SELECT * FROM b WHERE rn = 1
              ORDER BY MatchTime, MatchNo",
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
        /// <summary>出注时的让球数（缺失回退 FbMatch.Handicap），只给让球胜平负判定用。</summary>
        public decimal? Handicap { get; set; }
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

    /// <summary>出告警时那一注的 PickId（仅供追溯，不用于关联——重算会换 Id）。</summary>
    public long PickId { get; set; }

    /// <summary>玩法（BQ/BF/JQ），与 MatchId+OptionCode 一起构成告警回写到榜面的稳定键（2026-11-08）。</summary>
    public string PlayType { get; set; } = "";

    /// <summary>选项代码（如 0-0 / 1:0 / 2），告警回写键的一部分。</summary>
    public string OptionCode { get; set; } = "";

    /// <summary>
    /// 命中的信号码（逗号分隔，2026-11-08 加）。这是去重键，也是「哪几条信号对注不利」的凭证：
    /// 没有它就只能拿带实时数值的 Reason 去重，数值一动就是新记录（11-08 一天刷了 57 条告警）。
    /// </summary>
    public string? SignalCode { get; set; }

    /// <summary>最后一次仍在触发的时刻（告警会随信号消失被删除，本列用于判断新鲜度）。</summary>
    public DateTime? LastSeenAt { get; set; }

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

/// <summary>当前榜面发布时间与最近一次出榜尝试（闸拒绝时页面据此提示，不得假装已刷新）。</summary>
public sealed class MilkBoardStatus
{
    /// <summary>查询所用的受注日（与页面一致）。</summary>
    public DateTime SalesDate { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? PublishedRunId { get; init; }
    public DateTime? LastAttemptAt { get; init; }
    public string? LastStatus { get; init; }
    public string? LastFailReason { get; init; }
    public string? LastNote { get; init; }
    public string? LastRunId { get; init; }

    /// <summary>跨日跳过：不发布成功，也不算失败告警。</summary>
    public bool LastAttemptSkipped =>
        string.Equals(LastStatus, "skipped", StringComparison.OrdinalIgnoreCase);

    /// <summary>最近一次尝试是失败/partial（不含 skipped / success）。</summary>
    public bool LastAttemptFailed =>
        LastStatus is "failed" or "partial"
        || (!string.IsNullOrWhiteSpace(LastFailReason)
            && !LastAttemptSkipped
            && !string.Equals(LastStatus, "success", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(LastStatus, "running", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 一轮告警落库的结果（2026-11-08 加）。<see cref="NewHigh"/> 是「新出现的高危」条数，
/// 调用方据此决定要不要立刻重算当天的榜——不是每次都重算，否则每 10 分钟跑一遍全量。
/// </summary>
public sealed class TurnAlertSaveResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    /// <summary>信号已消失、被删掉的告警行数。</summary>
    public int Cleared { get; set; }
    /// <summary>本轮新出现（或由 mid 升级为 high）的高危告警条数。</summary>
    public int NewHigh { get; set; }
    public int Total => Inserted + Updated;
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
    /// <summary>
    /// 模型自证口径的 EV，API 层把 <see cref="Ev"/> 覆盖成 BaselineEv 时存进来（2026-11-08）。
    /// 只用于「模型有多乐观」的对比展示，不参与排序。
    /// </summary>
    public decimal? ModelEv { get; set; }
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
    /// 模型纯值 ÷ 市场去水隐含（2026-11-08 加）。&lt; 0.70 = 模型认为市场高估 30% 以上。
    /// 与 <see cref="MilkPick.ModelVsMarketRatio"/> 同口径，落库是为了赛后能复核这条闸。
    /// </summary>
    public decimal? ModelVsMarketRatio { get; set; }
    /// <summary>
    /// 主胜赔率偏贵档（2026-11-11 加）：收盘欧赔主胜 [2.90,3.60) 且该注押全场主胜。
    /// 只标不撤，证据强度见 <c>MilkPickEngine.HomePriceRichMin</c>。
    /// </summary>
    public bool HomePriceRich { get; set; }
    /// <summary>
    /// 纯模型概率（2026-11-11 加）：掺盘口之前的值。NULL = 历史行没存，不代表模型概率为零。
    /// </summary>
    public decimal? ModelPctPure { get; set; }
    /// <summary>
    /// 模型版本戳（2026-11-12 加）：拟合窗口/样本数/ρ/构建 MVID/五个玩法 w。
    /// NULL = 该行在加这一列之前出的，或者当时模型未就绪。
    /// </summary>
    public string? ModelVersion { get; set; }
    /// <summary>
    /// 当前挂单价（2026-11-08 加）。不是数据库列——由 <c>GetDayAsync</c> 关联当天最新快照填入。
    ///
    /// 用途：出榜价是出榜那一刻的快照，临场会动。单列注还有额外一层风险——它是区间票
    /// （赔率必须落在 30~&lt;40），价格滑出区间后引擎下一次重算就会换注，但页面上还挂着旧价。
    /// 页面据此显示「现价」并在滑出区间时打标，避免用户按一个已经不存在的赔率去下单。
    /// </summary>
    public decimal? CurrentOdds { get; set; }
    /// <summary>
    /// 临场告警严重度（2026-11-08 加）：high / mid / null（无告警）。
    /// 由 turn-guard 扫描后**回写到注单本身**——这才是闭环的关键一步：
    /// 以前告警只落在 FbTurnAlert，推荐页那张卡片上什么都不显示，
    /// 用户照着榜下单，而系统在他背后喊了十几次「这注危险」。
    /// </summary>
    public string? AlertSeverity { get; set; }
    /// <summary>告警原因（一句话，前端直接显示在注单行上）。</summary>
    public string? AlertReason { get; set; }
    /// <summary>
    /// 是否已被临场撤注（2026-11-08 加）。high 告警自动置位。
    /// 撤注**不删行**，理由有两条：① 用户必须看得见「这注被撤了」，静默消失等于假装没推过；
    /// ② 撤注对错必须能回测——留着行才能在结算后回答「撤掉的注最后中没中」。
    /// </summary>
    public bool Voided { get; set; }
    public DateTime? VoidedAt { get; set; }
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

/// <summary>
/// 撤榜明细行（2026-11-09 加）。给前端「撤榜展示区」用，不参与任何出票与统计。
/// <see cref="Kind"/> 决定页面怎么写这一行：
///   voided   = 临场高危撤注（有 AlertReason / VoidedAt）
///   replaced = 曾上榜、后来被重算换掉（只有 FrozenAt：它最后一次出现在榜上的时间）
/// </summary>
public sealed class MilkVoidedRow
{
    public string Kind { get; set; } = "";
    public long MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public string PlayType { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public string? OptionName { get; set; }
    public decimal Odds { get; set; }
    public string? AlertSeverity { get; set; }
    public string? AlertReason { get; set; }
    public DateTime? VoidedAt { get; set; }
    /// <summary>该注最后一次被空跑台账记下的时间（只有 replaced 类有）。</summary>
    public DateTime? FrozenAt { get; set; }
    public string? ActualScore { get; set; }
    public bool? Hit { get; set; }
    public DateTime? SettledAt { get; set; }
}

/// <summary>按玩法聚合的大奶命中统计。</summary>
public sealed class MilkPlayStat
{
    public string PlayType { get; set; } = "";
    /// <summary>已结算**且未撤注**注数（Hit 非空）。撤注 = 没下这笔，不进分母。</summary>
    public int Total { get; set; }
    public int Hit { get; set; }

    /// <summary>已撤注的注数（已结算、有赛果，但没下注）。单独计数，用于评价撤注判据。</summary>
    public int Voided { get; set; }

    /// <summary>撤注注里命中的条数。与 <see cref="Voided"/> 配对看，别混进命中率。</summary>
    public int VoidedHit { get; set; }
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
