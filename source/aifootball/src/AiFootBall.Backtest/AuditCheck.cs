using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 审计表端到端自检（2026-11-12 加）。
///
/// 为什么需要它：`SaveDayAsync` / `SaveProbSnapshotsAsync` 里都是**手写的多列 INSERT**，
/// 列数和值数一旦不齐、或者新加的那一列没进 SELECT，SQL Server 会在运行时抛错。
/// 而调用方（`RecommendationService.SaveMilkAsync`）用 try/catch 包着存档写入——
/// 于是「存档一直没写进去」的表现是**完全静默**的：榜照出、日志无异常、表里就是空的。
/// 这个项目已经因为「判据写了、字段没读」栽过一次（`GetOpenDayAsync` 漏 SELECT BaselineEv）。
///
/// 做法：用**远离真实数据的日期**（2090-01-01）插一条假注单和三条假分布，读回来核对，再删干净。
/// 不碰任何真实场次、不触发结算、不改榜面。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- audit-check [批量行数]
/// 会写库（仅 2090-01-01 这个日期），跑完自动清理。
///
/// 给了批量行数就额外跑一次**真实规模的写入计时**（2026-11-12 加）。
/// 起因：这三个自检行只验证「列对不对」，一行一行的写法在 3 行下永远看不出问题；
/// 而线上一次出榜要写上千行，`save-recommendations` 又有 10 分钟硬上限，
/// 一旦存档写成逐行往返把作业拖超时，表现同样是「榜照出、存档空着」——和列数不齐同一种病症。
/// </summary>
public static class AuditCheck
{
    public static async Task<int> RunAsync(string connStr, int bulkRows = 0)
    {
        var probeDate = new DateTime(2090, 1, 1);
        const long probeMatchId = 999_999_991L;
        // 两轮探针（2026-11-13 第二轮）：A 是主轮，B 用来验证「写新一轮不会抹掉前一轮」。
        const string probeRunA = "20900101T120000-aaaaaa";
        const string probeRunB = "20900101T130000-bbbbbb";
        var factory = new SqlConnectionFactory(connStr);
        var repo = new MilkPickRepository(factory);
        var failures = new List<string>();

        await using var conn = await factory.OpenAsync();

        // ---------- 1) FbMilkPick 多列 INSERT（含新加的 ModelVersion） ----------
        var pick = new MilkPick
        {
            MatchNo = "自检000",
            MatchId = probeMatchId,
            LeagueName = "自检联赛",
            HomeTeam = "自检主队",
            AwayTeam = "自检客队",
            MatchTime = probeDate.AddHours(12),
            PlayType = JcPlayTypes.Bq,
            OptionCode = "3-3",
            OptionName = "胜胜",
            Odds = 2.5m,
            ModelProbability = 31.11m,
            ModelPctPure = 30.00m,
            Ev = 0.778m,
            ModelVersion = "SELFCHECK|dc1|2090-01-01..2090-01-01|n=1|rho=0.000|test|w=70/70/50/80/30|bf=off"
        };

        var written = await repo.SaveDayAsync(probeDate, new[] { pick }, probeRunA);
        var readBack = await repo.GetDayAsync(probeDate);
        var stored = readBack.FirstOrDefault(x => x.MatchId == probeMatchId);
        if (written == 0) failures.Add("FbMilkPick 写入 0 行（多列 INSERT 可能列数不匹配）");
        if (stored is null) failures.Add("FbMilkPick 读回不到自检行（SELECT 列不全？）");
        else if (stored.ModelVersion != pick.ModelVersion) failures.Add($"ModelVersion 未落库：读到 {stored.ModelVersion ?? "NULL"}");
        var pickRunStored = await conn.ExecuteScalarAsync<string?>(
            "SELECT TOP 1 RunId FROM dbo.FbMilkPick WHERE PickDate=@d AND MatchId=@m",
            new { d = probeDate, m = probeMatchId });
        if (pickRunStored != probeRunA) failures.Add($"注单 RunId 未落库：读到 {pickRunStored ?? "NULL"}");

        // ---------- 2) 分布存档表 ----------
        var snapshotRows = new List<MilkProbSnapshot>
        {
            new()
            {
                MatchId = probeMatchId, MatchNo = "自检000", PlayType = JcPlayTypes.Bq, OptionCode = "3-3",
                Odds = 2.5m, MarketPct = 31.89m, ModelPctPure = 30.00m, BlendedPct = 31.60m,
                MarketPriced = true, ModelVersion = pick.ModelVersion
            },
            new()
            {
                MatchId = probeMatchId, MatchNo = "自检000", PlayType = JcPlayTypes.Bq, OptionCode = "1-1",
                Odds = 5.0m, MarketPct = 15.95m, ModelPctPure = 12.00m, BlendedPct = 14.58m,
                MarketPriced = true, ModelVersion = pick.ModelVersion
            },
            new()
            {
                MatchId = probeMatchId, MatchNo = "自检000", PlayType = JcPlayTypes.Jq, OptionCode = "3",
                Odds = 4.0m, MarketPct = 19.93m, ModelPctPure = 22.00m, BlendedPct = 20.34m,
                MarketPriced = true, ModelVersion = pick.ModelVersion
            }
        };

        var snapWritten = await repo.SaveProbSnapshotsAsync(probeDate, snapshotRows, probeRunA);
        var snapStored = (await conn.QueryAsync<(string PlayType, string OptionCode, decimal? Pure, decimal? Blend, string? Ver)>(
            @"SELECT PlayType, OptionCode, ModelPctPure AS Pure, BlendedPct AS Blend, ModelVersion AS Ver
              FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d AND MatchId=@m AND RunId=@run",
            new { d = probeDate, m = probeMatchId, run = probeRunA })).ToList();

        if (snapWritten != snapshotRows.Count) failures.Add($"分布存档写入 {snapWritten} 行，期望 {snapshotRows.Count}");
        if (snapStored.Count != snapshotRows.Count) failures.Add($"分布存档读回 {snapStored.Count} 行，期望 {snapshotRows.Count}");
        var bq33 = snapStored.FirstOrDefault(x => x.PlayType == JcPlayTypes.Bq && x.OptionCode == "3-3");
        if (bq33.PlayType is null) failures.Add("分布存档缺 3-3 行");
        else if (bq33.Pure != 30.00m || bq33.Blend != 31.60m) failures.Add($"分布存档数值不对：pure={bq33.Pure} blend={bq33.Blend}");

        // 同轮重写幂等（按 RunId 先删后插，不会累加）
        await repo.SaveProbSnapshotsAsync(probeDate, snapshotRows, probeRunA);
        var afterRewrite = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
        if (afterRewrite != snapshotRows.Count) failures.Add($"同轮重写不幂等：两次写入后共 {afterRewrite} 行");

        // ★ 历史保留（2026-11-13 第二轮）：写新一轮不许抹掉上一轮。
        // 这正是「空结果不删存档」那条修法留下的新缺口——只留一版，就分不清是哪一轮的。
        await repo.SaveProbSnapshotsAsync(probeDate, snapshotRows.Take(1).ToList(), probeRunB);
        var afterSecondRun = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
        var runARows = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d AND RunId=@run",
            new { d = probeDate, run = probeRunA });
        if (afterSecondRun != snapshotRows.Count + 1)
            failures.Add($"写新一轮抹掉了旧轮：期望 {snapshotRows.Count + 1} 行，实存 {afterSecondRun}");
        if (runARows != snapshotRows.Count)
            failures.Add($"旧轮存档被改动：A 轮应留 {snapshotRows.Count} 行，实存 {runARows}");
        // 清掉 B 轮，后面的批量计时仍按单轮口径断言
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkProbSnapshot WHERE RunId=@run", new { run = probeRunB });

        // ---------- 3) 真实规模写入计时（可选，参数 bulkRows） ----------
        var bulkMs = 0L;
        var bulkWritten = 0;
        var bulkExpected = 0;
        if (bulkRows > 0)
        {
            // 按线上形态造：每场 35 档，顺序 SPF 3 / NSPF 3 / BQ 9 / JQ 8 / BF 12。
            // 一行一行写在这里看不出差别，要的就是「一次写上千行要多久」。
            var bulk = new List<MilkProbSnapshot>(bulkRows);
            for (var i = 0; i < bulkRows; i++)
            {
                var k = i % 35;
                var (play, code) = k switch
                {
                    < 3 => (JcPlayTypes.Spf, new[] { "3", "1", "0" }[k]),
                    < 6 => (JcPlayTypes.Nspf, new[] { "3", "1", "0" }[k - 3]),
                    < 15 => (JcPlayTypes.Bq, new[] { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" }[k - 6]),
                    < 23 => (JcPlayTypes.Jq, (k - 15).ToString()),
                    _ => (JcPlayTypes.Bf, new[] { "1:0", "2:0", "2:1", "3:0", "3:1", "3:2", "0:0", "1:1", "2:2", "0:1", "0:2", "1:2" }[k - 23])
                };
                bulk.Add(new MilkProbSnapshot
                {
                    MatchId = probeMatchId + 1 + (i / 35),
                    MatchNo = "自检" + (i / 35).ToString("000"),
                    PlayType = play,
                    OptionCode = code,
                    Odds = 2.5m,
                    MarketPct = 31.89m,
                    ModelPctPure = 30.00m,
                    BlendedPct = 31.60m,
                    MarketPriced = true,
                    ModelVersion = "SELFCHECK-BULK"
                });
            }
            bulkExpected = bulk.Count;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bulkWritten = await repo.SaveProbSnapshotsAsync(probeDate, bulk, probeRunA);
            sw.Stop();
            bulkMs = sw.ElapsedMilliseconds;

            var bulkStored = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
            if (bulkStored != bulkExpected) failures.Add($"批量存档实存 {bulkStored} 行，期望 {bulkExpected}");
        }

        // ---------- 4) 输入凭证表（2026-11-13 加） ----------
        // 这张表回答的是「本次到底算了哪些场」，是存档覆盖率的独立分母。
        // 同样是手写多列 INSERT，同样被 try/catch 包着，所以必须进自检——
        // 否则它写坏了也只会表现成「分母是 0」，反过来又会被误读成「今天没有输入」。
        var inputRows = new List<MilkInputMatch>
        {
            new() { MatchId = probeMatchId, MatchNo = "自检000", LeagueName = "自检联赛", HomeTeam = "自检主队", AwayTeam = "自检客队", MatchTime = probeDate.AddHours(12), VersionTag = "SELFCHECK" },
            new() { MatchId = probeMatchId + 1, MatchNo = "自检001", LeagueName = "自检联赛", HomeTeam = "自检主队", AwayTeam = "自检客队2", MatchTime = probeDate.AddHours(14), VersionTag = "SELFCHECK" }
        };
        var inputWritten = await repo.SaveInputsAsync(probeDate, inputRows, probeRunA);
        var inputStored = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkInput WHERE InputDate=@d AND RunId=@run",
            new { d = probeDate, run = probeRunA });
        if (inputWritten != inputRows.Count) failures.Add($"输入凭证写入 {inputWritten} 行，期望 {inputRows.Count}");
        if (inputStored != inputRows.Count) failures.Add($"输入凭证读回 {inputStored} 行，期望 {inputRows.Count}");

        // 同轮重写幂等
        await repo.SaveInputsAsync(probeDate, inputRows, probeRunA);
        var inputAfterRewrite = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkInput WHERE InputDate=@d AND RunId=@run",
            new { d = probeDate, run = probeRunA });
        if (inputAfterRewrite != inputRows.Count) failures.Add($"输入凭证同轮重写不幂等：{inputAfterRewrite} 行");

        // ★ 空集只清本轮、不许清别的轮（2026-11-13 第二轮改口径）
        //
        // 旧口径是「空集就把该日全删」。按轮写之后，那样做会连带删掉同一天别的轮次，
        // 于是「本轮 0 场」这个事实又要靠「表里没行」来表达——与「写坏了」分不开。
        await repo.SaveInputsAsync(probeDate, inputRows.Take(1).ToList(), probeRunB);
        await repo.SaveInputsAsync(probeDate, Array.Empty<MilkInputMatch>(), probeRunA);
        var inputAfterEmpty = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkInput WHERE InputDate=@d AND RunId=@run",
            new { d = probeDate, run = probeRunA });
        var otherRunLeft = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkInput WHERE InputDate=@d AND RunId=@run",
            new { d = probeDate, run = probeRunB });
        if (inputAfterEmpty != 0) failures.Add($"输入凭证空集重写后本轮仍有 {inputAfterEmpty} 行（应清空）");
        if (otherRunLeft != 1) failures.Add($"空集重写误删了别的轮：B 轮应留 1 行，实存 {otherRunLeft}");

        // ---------- 5) 轮次头与状态（2026-11-13 第二轮加，第三轮补状态） ----------
        // 存在的理由：「全被过滤」的那一轮在四张表里都不留行，与「作业没跑」无法区分。
        // 第三轮补 Status：没有它，只靠 MAX(RunId) 选榜面 = 失败轮也能顶掉上一轮成功的榜。
        var run = new MilkRun
        {
            RunId = probeRunA,
            StartedAt = probeDate.AddHours(12),
            RawMatches = 11,
            SalesDates = new[] { probeDate },
            VersionTag = pick.ModelVersion
        };
        await repo.StartRunAsync(run, probeDate, "rewrite", "自检轮次", default);
        await repo.StartRunAsync(run, probeDate, "rewrite", "自检轮次重复写", default);
        var runStored = (await conn.QueryAsync<(string RunId, int Raw, string Status, DateTime? Published)>(
            @"SELECT RunId, RawMatches AS Raw, Status, PublishedAt AS Published
              FROM dbo.FbMilkRun WHERE RunDate=@d",
            new { d = probeDate })).ToList();
        if (runStored.Count != 1) failures.Add($"轮次头应为 1 行（同一 RunId 幂等），实存 {runStored.Count}");
        else
        {
            var r = runStored[0];
            if (r.Raw != 11) failures.Add($"轮次头 RawMatches 未落库：{r.Raw}");
            // 开一轮只写 running：此时还没发布，任何读取都不该把它当成榜面。
            if (r.Status != MilkRunStatus.Running) failures.Add($"开轮状态应为 running，实为 {r.Status}");
            if (r.Published != null) failures.Add($"未发布的轮次 PublishedAt 应为 NULL，实为 {r.Published:O}");
        }

        // ---------- 6) 闸门表按轮写 + 读路径钉最新一轮（2026-11-13 第二轮） ----------
        //
        // 这一段是被真机打醒之后补的：闸表不是我们建的，`RunId` 靠 SaveGateAsync 里的
        // 「表在但列不在就补」自愈。但页面是**先读后写**——`GetGateAsync` 里那句 MAX(RunId)
        // 会在列还没补上的那一刻直接报「列名无效」，整页 500。
        // 所以读路径也得跑一次自愈，并且必须由自检钉住，不能靠下次有人打开页面才发现。
        var gateA = new[]
        {
            new MilkDirectionDiagnosis
            {
                MatchId = probeMatchId, MatchNo = "自检000", LeagueName = "自检联赛",
                HomeTeam = "自检主队", AwayTeam = "自检客队", MatchTime = probeDate.AddHours(12),
                ModelDirection = '3', ModelHomePct = 50m, ModelDrawPct = 30m, ModelAwayPct = 20m,
                PickDirection = '3', MarketDirection = '3', MarketSource = "spf",
                Verdict = MilkGateVerdicts.Pass
            },
            new MilkDirectionDiagnosis
            {
                MatchId = probeMatchId + 1, MatchNo = "自检001", LeagueName = "自检联赛",
                HomeTeam = "自检主队", AwayTeam = "自检客队2", MatchTime = probeDate.AddHours(14),
                ModelDirection = '3', ModelHomePct = 45m, ModelDrawPct = 30m, ModelAwayPct = 25m,
                PickDirection = '3', MarketDirection = '0', MarketSource = "spf",
                Verdict = MilkGateVerdicts.Diverge
            }
        };
        var gateWritten = await repo.SaveGateAsync(probeDate, gateA, probeRunA, default);
        if (gateWritten != 2) failures.Add($"闸门写入 {gateWritten} 行，期望 2");

        await repo.SaveGateAsync(
            probeDate,
            new[] { new MilkDirectionDiagnosis { MatchId = probeMatchId + 2, MatchNo = "自检002", Verdict = MilkGateVerdicts.Pass } },
            probeRunB, default);
        var gateRunAStored = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkGate WHERE GateDate=@d AND RunId=@run",
            new { d = probeDate, run = probeRunA });
        if (gateRunAStored != 2) failures.Add($"闸门 A 轮被改写：应留 2 行，实存 {gateRunAStored}");

        // ---------- 7) 榜面发布：只切到最近一个「成功」轮（2026-11-13 第三轮） ----------
        //
        // 这一段是用户点名的端到端口径，必须真读库、真经过 SQL：
        //   A 轮成功、2 注     → 榜面 2 行
        //   B 轮成功、0 注     → 榜面归零（关键形态：不删任何行，只切发布指针）
        //   C 轮失败、1 注、RunId 故意更大 → 榜面仍是 0（失败轮不许被 MAX(RunId) 选中）
        //   D 轮采集不完整（partial）、1 注 → 榜面仍是 0
        // 同时断言「历史保留」：库里行数 > 榜面行数。
        const string probeRunC = "20900101T140000-cccccc";
        const string probeRunD = "20900101T150000-dddddd";

        MilkPick ProbePick(long id, string no, DateTime at) => new()
        {
            MatchNo = no,
            MatchId = id,
            LeagueName = "自检联赛",
            HomeTeam = "自检主队",
            AwayTeam = "自检客队" + (id % 100),
            MatchTime = at,
            PlayType = JcPlayTypes.Jq,
            OptionCode = "3",
            OptionName = "3球",
            Odds = 4.10m,
            ModelProbability = 20m,
            Ev = 0.82m,
            ModelVersion = "SELFCHECK"
        };

        var pickB = ProbePick(probeMatchId + 1, "自检001", probeDate.AddHours(14));
        var pickC = ProbePick(probeMatchId + 2, "自检002", probeDate.AddHours(16));
        var pickD = ProbePick(probeMatchId + 3, "自检003", probeDate.AddHours(18));

        // A：2 注 + 发布
        await repo.SaveDayAsync(probeDate, new[] { pick, pickB }, probeRunA);
        var publishedA = await repo.PublishRunAsync(
            probeRunA,
            new[] { new MilkRunDateCount { SalesDate = probeDate, PickCount = 2 } },
            inputRows.Count, snapshotRows.Count, gateA.Length, 2, "自检 A 轮", default);
        var boardA = await repo.GetDayAsync(probeDate);
        var gateReadA = await repo.GetGateAsync(probeDate, default);
        if (publishedA != 1) failures.Add($"A 轮发布影响 {publishedA} 行，期望 1");
        if (boardA.Count != 2) failures.Add($"A 轮发布后榜面应 2 行，实读 {boardA.Count}");
        if (gateReadA.Count != 2) failures.Add($"A 轮发布后闸门应 2 行，实读 {gateReadA.Count}");

        // B：成功但 0 注 → 榜面必须归零，且 A 的行仍在库里（保留历史）
        await repo.StartRunAsync(
            new MilkRun { RunId = probeRunB, StartedAt = probeDate.AddHours(13), RawMatches = 11, SalesDates = new[] { probeDate } },
            probeDate, "rewrite", "自检 B 轮", default);
        await repo.PublishRunAsync(
            probeRunB,
            new[] { new MilkRunDateCount { SalesDate = probeDate, PickCount = 0 } },
            0, 0, 1, 0, "自检 B 轮 0 注", default);
        var boardB = await repo.GetDayAsync(probeDate);
        var gateB = await repo.GetGateAsync(probeDate, default);
        var rowsInDbAfterB = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkPick WHERE PickDate=@d", new { d = probeDate });
        if (boardB.Count != 0) failures.Add($"B 轮发布 0 注后榜面应归零，实读 {boardB.Count} 行");
        // B 轮只写过 1 条诊断（A 轮是 2 条）：读回 1 条且是 B 那条，才说明「跟的是发布轮」。
        if (gateB.Count != 1 || gateB[0].MatchId != probeMatchId + 2)
            failures.Add($"B 轮发布后闸门应只剩 B 的 1 条（{probeMatchId + 2}），实读 {gateB.Count} 条");
        if (rowsInDbAfterB != 2) failures.Add($"发布 0 注不应删掉历史行：应留 2 行，实存 {rowsInDbAfterB}");

        // C：失败轮（RunId 比 B 大）+ 1 注 + 1 条诊断 → 榜面与闸门都不许切
        await repo.SaveDayAsync(probeDate, new[] { pickC }, probeRunC);
        await repo.StartRunAsync(
            new MilkRun { RunId = probeRunC, StartedAt = probeDate.AddHours(14), RawMatches = 11, SalesDates = new[] { probeDate } },
            probeDate, "rewrite", "自检 C 轮", default);
        await repo.SaveGateAsync(probeDate, new[]
        {
            new MilkDirectionDiagnosis { MatchId = probeMatchId + 3, MatchNo = "自检003", Verdict = MilkGateVerdicts.Pass }
        }, probeRunC, default);
        await repo.CloseRunAsync(probeRunC, MilkRunStatus.Failed, "自检失败轮", 1, 0, 1, 1, default);
        var boardC = await repo.GetDayAsync(probeDate);
        var gateC = await repo.GetGateAsync(probeDate, default);
        if (boardC.Count != 0) failures.Add($"失败轮被选中：榜面应 0 行，实读 {boardC.Count}");
        if (gateC.Count != 1 || gateC[0].MatchId != probeMatchId + 2)
            failures.Add($"失败轮的诊断被选中：闸门应仍是 B 的 1 条（{probeMatchId + 2}），实读 {gateC.Count} 条");

        // D：采集不完整（partial）+ 1 注 → 同样不许切
        await repo.SaveDayAsync(probeDate, new[] { pickD }, probeRunD);
        await repo.StartRunAsync(
            new MilkRun { RunId = probeRunD, StartedAt = probeDate.AddHours(15), RawMatches = 8, SalesDates = new[] { probeDate } },
            probeDate, "partial", "自检 D 轮", default);
        await repo.CloseRunAsync(probeRunD, MilkRunStatus.Partial, "只采到 8/11 场", 1, 0, 0, 1, default);
        var boardD = await repo.GetDayAsync(probeDate);
        if (boardD.Count != 0) failures.Add($"partial 轮被选中：榜面应 0 行，实读 {boardD.Count}");

        // 历史保留：A/C/D 三轮共 4 行都还在，只是没发布
        var rowsInDbFinal = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkPick WHERE PickDate=@d", new { d = probeDate });
        var runsFinal = (await conn.QueryAsync<(string RunId, string Status)>(
            "SELECT RunId, Status FROM dbo.FbMilkRun WHERE RunDate=@d ORDER BY RunId",
            new { d = probeDate })).ToList();
        if (rowsInDbFinal != 4) failures.Add($"历史未保留：应 4 行（A2+C1+D1），实存 {rowsInDbFinal}");
        if (runsFinal.Count != 4) failures.Add($"轮次头应 4 行，实存 {runsFinal.Count}");
        else if (runsFinal.Count(r => r.Status == MilkRunStatus.Success) != 2)
            failures.Add($"成功轮应 2 个（A/B），实有 {runsFinal.Count(r => r.Status == MilkRunStatus.Success)}");

        // ---------- 清理 ----------
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkPick WHERE PickDate=@d", new { d = probeDate });
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkInput WHERE InputDate=@d", new { d = probeDate });
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkRunDate WHERE SalesDate=@d", new { d = probeDate });
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkRun WHERE RunDate=@d", new { d = probeDate });
        await conn.ExecuteAsync("DELETE FROM dbo.FbMilkGate WHERE GateDate=@d", new { d = probeDate });
        var leftPick = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkPick WHERE PickDate=@d", new { d = probeDate });
        var leftSnap = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkProbSnapshot WHERE SnapshotDate=@d", new { d = probeDate });
        var leftInput = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkInput WHERE InputDate=@d", new { d = probeDate });
        var leftRun = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkRun WHERE RunDate=@d", new { d = probeDate });
        var leftGate = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkGate WHERE GateDate=@d", new { d = probeDate });
        var leftRunDate = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.FbMilkRunDate WHERE SalesDate=@d", new { d = probeDate });
        if (leftPick != 0 || leftSnap != 0 || leftInput != 0 || leftRun != 0 || leftGate != 0 || leftRunDate != 0)
            failures.Add($"清理不干净：pick={leftPick} snapshot={leftSnap} input={leftInput} run={leftRun} gate={leftGate} runDate={leftRunDate}");

        Console.WriteLine($"# 审计表自检  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"| 检查项 | 结果 |");
        Console.WriteLine($"|---|---|");
        Console.WriteLine($"| FbMilkPick 写入行数 | {written} |");
        Console.WriteLine($"| FbMilkPick 读回 | {(stored is null ? "失败" : "成功")} |");
        Console.WriteLine($"| ModelVersion 落库 | {(stored?.ModelVersion is null ? "**NULL**" : "正常")} |");
        Console.WriteLine($"| 分布存档写入 / 读回 | {snapWritten} / {snapStored.Count}（期望 {snapshotRows.Count}） |");
        Console.WriteLine($"| 同轮重写后行数（幂等） | {afterRewrite} |");
        Console.WriteLine($"| 写新一轮后总行数 / 旧轮保留 | {afterSecondRun} / {runARows}（期望 {snapshotRows.Count + 1} / {snapshotRows.Count}） |");
        Console.WriteLine($"| 输入凭证 写入 / 读回 / 空集后 | {inputWritten} / {inputStored} / {inputAfterEmpty}（别的轮剩 {otherRunLeft}） |");
        Console.WriteLine($"| 轮次头幂等 / 开轮状态 | {(runStored.Count == 1 ? "1 行" : runStored.Count + " 行")} / {(runStored.Count == 1 ? runStored[0].Status : "?")} |");
        Console.WriteLine($"| 闸门 写入 / A 轮保留 | {gateWritten} / {gateRunAStored} |");
        Console.WriteLine($"| 榜面 A 轮发布 2 注 | {boardA.Count} 行（期望 2） |");
        Console.WriteLine($"| 榜面 B 轮发布 0 注 | {boardB.Count} 行（期望 0，历史仍 {rowsInDbAfterB} 行） |");
        Console.WriteLine($"| 榜面 失败轮 C（RunId 更大） | {boardC.Count} 行（期望 0） |");
        Console.WriteLine($"| 榜面 采集不完整轮 D | {boardD.Count} 行（期望 0） |");
        Console.WriteLine($"| 历史保留 / 轮次头 | {rowsInDbFinal} 行 / {runsFinal.Count} 轮（成功 {runsFinal.Count(r => r.Status == MilkRunStatus.Success)}） |");
        if (bulkExpected > 0)
        {
            var perSec = bulkMs > 0 ? bulkExpected * 1000 / bulkMs : bulkExpected;
            Console.WriteLine($"| 批量写入 {bulkExpected} 行 | {bulkWritten} 行 / {bulkMs} ms（≈{perSec} 行/秒） |");
            Console.WriteLine($"| 出榜作业预算 | 10 分钟硬上限，存档占 {bulkMs / 600.0:0.0}% |");
        }
        Console.WriteLine($"| 清理后残留 | pick={leftPick} snapshot={leftSnap} input={leftInput} run={leftRun} gate={leftGate} runDate={leftRunDate} |");
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("**全部通过。** 两套 INSERT 的列与值数量一致，新列能落库也能读回；"
                              + "榜面只切到最近一个状态为 success 的轮次，失败轮与不完整轮都不会顶掉它。");
            return 0;
        }

        Console.WriteLine("**失败项：**");
        foreach (var f in failures) Console.WriteLine($"- {f}");
        return 1;
    }
}
