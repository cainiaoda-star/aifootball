using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Text;

namespace AiFootBall.Backtest;

/// <summary>
/// 隔离库集成验证（2026-11-13 第五轮，用户明确同意「创建一个专用隔离测试库，只用于这次集成验证」）。
///
/// 验什么：**历史战绩的新统计口径（MIN(PickId) 取首份 + AnyHit 取任一副本）到底会不会算错账。**
/// 上一版是照着「榜面只认最新轮」改的，改完直接在生产上宣称「历史保留 28→28」——
/// 但那个样本里一条多副本都没有（28 行全是升级前的 RunId IS NULL），所以什么也没证明。
/// 这里就造出多副本，逐条验用户点名的三种形态：
///   ① 同票跨轮赔率变化     → 只算一注，价格取**首次上榜**那份，不是最新那份
///   ② 撤注后再出现         → 不重复计票，已冻结的收益（Pnl = NULL）不被改写
///   ③ 结算只落一份副本     → 命中不能因为「首份没结算」而丢
/// 判定标准不是「我觉得合理」，而是**报表必须与空跑台账（冻结 + 结算规则）逐项相等**：
/// 台账是「照榜单买会怎样」的唯一凭证，两套口径对不上就说明战绩页在说另一件事。
///
/// 安全：只连 <see cref="AuditDbName"/>，连生产库名直接拒绝；建表用生产库 `SELECT * INTO ... WHERE 1=0`
/// 克隆结构，不碰生产数据。每次运行前 DROP 重建（这个库就是一次性的）。
/// </summary>
public static class MilkIsolatedCheck
{
    /// <summary>专用隔离库名。硬编码 + 二次校验，防止连接串写错把生产库清了。</summary>
    public const string AuditDbName = "AiFootBall_AuditTest";

    private const long M1 = 900000001, M2 = 900000002, M3 = 900000003, M4 = 900000004, M5 = 900000005;
    /// <summary>源快照时间边界用例专用场次（不与上面几场共用，避免影响榜面/报表断言）。</summary>
    private const long M6 = 900000006, M7 = 900000007, M8 = 900000008;

    private sealed record Seed(long MatchId, string Play, string Code, decimal Odds)
    {
        public bool Voided { get; init; }
        public bool? Hit { get; init; }
        public DateTime? SettledAt { get; init; }
    }

    public static async Task<int> RunAsync(string prodConnStr, CancellationToken ct = default)
    {
        var prod = new SqlConnectionStringBuilder(prodConnStr);
        if (string.IsNullOrWhiteSpace(prod.InitialCatalog))
        {
            Console.WriteLine("生产连接串里没有库名，拒绝执行。");
            return 2;
        }
        if (prod.InitialCatalog.Equals(AuditDbName, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("连接串指向隔离库本身，拒绝执行。");
            return 2;
        }

        var prodDb = prod.InitialCatalog;
        Console.WriteLine($"# 隔离库验证  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"- 生产库（只读克隆结构）：{prodDb}");
        Console.WriteLine($"- 隔离库（会重建）：{AuditDbName}");
        Console.WriteLine();

        await InitAsync(prod, prodDb, ct);
        var auditStr = new SqlConnectionStringBuilder(prodConnStr) { InitialCatalog = AuditDbName }.ConnectionString;

        var checks = new List<(string Name, bool Ok, string Detail)>();
        var d1 = DateTime.Today.AddDays(-3);   // 主场景日
        var d2 = DateTime.Today.AddDays(-4);   // 「结算只落一份副本」单独一天，免得互相干扰

        await using var c = new SqlConnection(auditStr);
        await c.OpenAsync(ct);

        await SeedMatchesAsync(c, d1, d2, ct);

        // ── 场景 ①＋②：同一天跑四轮 ──
        // RA 12:00 三注；RB 13:00 M1 赔率 5.75→6.20；RC 14:00 M2 被撤注；RD 15:00 M2 又出现。
        await SeedRunAsync(c, "20261101T120000-aaaaaa", d1, 12, d1,
            new Seed(M1, JcPlayTypes.Bq, "3-3", 5.75m),
            new Seed(M2, JcPlayTypes.Jq, "3", 4.10m),
            new Seed(M3, JcPlayTypes.Bf, "2:1", 8.00m), ct);

        var milk = new MilkPickRepository(new SqlConnectionFactory(auditStr));
        var paper = new PaperTradeRepository(new SqlConnectionFactory(auditStr));

        await paper.EnsureTableAsync(ct);
        var frozen1 = await paper.FreezeDayAsync(d1, ct);

        await SeedRunAsync(c, "20261101T130000-bbbbbb", d1, 13, d1,
            new Seed(M1, JcPlayTypes.Bq, "3-3", 6.20m),
            new Seed(M2, JcPlayTypes.Jq, "3", 4.10m),
            new Seed(M3, JcPlayTypes.Bf, "2:1", 8.00m), ct);
        var frozen2 = await paper.FreezeDayAsync(d1, ct);

        var m1Row = await OneAsync(c, d1, M1, JcPlayTypes.Bq, "3-3", ct);
        checks.Add(("① 同票跨轮赔率变化：只算一注且价格冻结",
            frozen1.Inserted == 3 && frozen2.Inserted == 0 && m1Row is not null && m1Row.Odds == 5.75m,
            frozen1.Inserted == 3 && frozen2.Inserted == 0 && m1Row is not null && m1Row.Odds == 5.75m
                ? $"首次冻结 3 注；第二轮 0 新增；M1 价格仍 {m1Row!.Odds:0.00}（第二轮报价 6.20 未覆盖）"
                : $"首冻 {frozen1.Inserted} 新增、二轮 {frozen2.Inserted} 新增、M1 价格 {m1Row?.Odds:0.00}（应 5.75）"));

        // RC：M2 撤注、M5 撤注（行还在，Voided=1 —— 引擎的撤注形态是标记，不是删行）
        await SeedRunAsync(c, "20261101T140000-cccccc", d1, 14, d1, new[]
        {
            new Seed(M1, JcPlayTypes.Bq, "3-3", 6.20m),
            new Seed(M2, JcPlayTypes.Jq, "3", 4.10m) { Voided = true },
            new Seed(M3, JcPlayTypes.Bf, "2:1", 8.00m),
            new Seed(M5, JcPlayTypes.Jq, "2", 5.00m) { Voided = true }
        }, ct);
        await paper.FreezeDayAsync(d1, ct);

        var m2AfterVoid = await OneAsync(c, d1, M2, JcPlayTypes.Jq, "3", ct);
        checks.Add(("② 撤注同步到冻结台账",
            m2AfterVoid is { Voided: true },
            m2AfterVoid is null ? "台账里找不到 M2" : $"Voided={m2AfterVoid.Voided}"));

        // RD：M2 又出现在榜上（Voided 回 0），M5 仍撤注。
        // 这才是生产里的真实顺序：**停售前**反复重算，撤了又回心转意。
        await SeedRunAsync(c, "20261101T150000-dddddd", d1, 15, d1, new[]
        {
            new Seed(M1, JcPlayTypes.Bq, "3-3", 6.20m),
            new Seed(M2, JcPlayTypes.Jq, "3", 4.10m),
            new Seed(M3, JcPlayTypes.Bf, "2:1", 8.00m),
            new Seed(M5, JcPlayTypes.Jq, "2", 5.00m) { Voided = true }
        }, ct);
        var frozenBack = await paper.FreezeDayAsync(d1, ct);

        var m2Back = await OneAsync(c, d1, M2, JcPlayTypes.Jq, "3", ct);
        var m2Rows = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.FbPaperTrade WHERE TradeDate=@d AND MatchId=@m AND PlayType=@p",
            new { d = d1, m = M2, p = JcPlayTypes.Jq }, cancellationToken: ct));
        checks.Add(("② 撤注后再出现：不重复计票、价格仍是首份",
            frozenBack.Inserted == 0 && m2Rows == 1 && m2Back is { Voided: false } && m2Back.Odds == 4.10m,
            $"新增 {frozenBack.Inserted} 行、台账 {m2Rows} 行、Voided={m2Back?.Voided}、价格 {m2Back?.Odds:0.00}（应 4.10）"));

        // 结算：M2 最终在榜（下注了）→ Pnl = 4.10-1；M5 最终撤注 → Pnl 必须 NULL（没下这笔）
        await milk.SettleAsync(ct);
        await paper.SettleAsync(ct);

        var m2Settled = await OneAsync(c, d1, M2, JcPlayTypes.Jq, "3", ct);
        var m5Settled = await OneAsync(c, d1, M5, JcPlayTypes.Jq, "2", ct);

        checks.Add(("② 撤注注结算后收益为 NULL（没下注）",
            m5Settled is { Voided: true, Pnl: null, Hit: false },
            m5Settled is null ? "台账里找不到 M5"
                : $"Voided={m5Settled.Voided} Hit={m5Settled.Hit} Pnl={(m5Settled.Pnl is null ? "NULL" : m5Settled.Pnl.ToString())}"));

        checks.Add(("② 回到榜的注正常结算一次",
            m2Settled is { Voided: false, Hit: true, Pnl: 3.10m },
            m2Settled is null ? "台账里找不到 M2"
                : $"Voided={m2Settled.Voided} Hit={m2Settled.Hit} Pnl={m2Settled.Pnl:0.00}（应 3.10）"));

        // 迟到撤注：结算之后又来一轮把 M2 标成撤注。结算即终态，已冻结的收益不许被改写。
        await SeedRunAsync(c, "20261101T160000-777777", d1, 16, d1, new[]
        {
            new Seed(M1, JcPlayTypes.Bq, "3-3", 6.20m),
            new Seed(M2, JcPlayTypes.Jq, "3", 4.10m) { Voided = true },
            new Seed(M3, JcPlayTypes.Bf, "2:1", 8.00m),
            new Seed(M5, JcPlayTypes.Jq, "2", 5.00m) { Voided = true }
        }, ct);
        await paper.FreezeDayAsync(d1, ct);
        var m2Late = await OneAsync(c, d1, M2, JcPlayTypes.Jq, "3", ct);
        checks.Add(("② 迟到的撤注不改写已结算行",
            m2Late is { Voided: false } && m2Late.Pnl == 3.10m,
            m2Late is null ? "台账里找不到 M2"
                : $"Voided={m2Late.Voided} Pnl={m2Late.Pnl:0.00}（应保持 False / 3.10）"));

        // ── 场景 ③：三份副本，结算只落在最后一份 ──
        await SeedRunAsync(c, "20261102T120000-eeeeee", d2, 12, d2, new Seed(M4, JcPlayTypes.Spf, "3", 2.10m), ct);
        await SeedRunAsync(c, "20261102T130000-ffffff", d2, 13, d2, new Seed(M4, JcPlayTypes.Spf, "3", 2.10m), ct);
        await SeedRunAsync(c, "20261102T140000-999999", d2, 14, d2,
            new Seed(M4, JcPlayTypes.Spf, "3", 2.10m) { Hit = true, SettledAt = DateTime.Now }, ct);

        await paper.FreezeDayAsync(d2, ct);
        await paper.SettleAsync(ct);

        var copies = await c.QueryAsync<(string RunId, bool? Hit, bool Voided, decimal Odds)>(
            new CommandDefinition(
                @"SELECT RunId, Hit, Voided, Odds FROM dbo.FbMilkPick
                  WHERE PickDate=@d AND MatchId=@m AND PlayType=@p AND OptionCode=@o ORDER BY PickId",
                new { d = d2, m = M4, p = JcPlayTypes.Spf, o = "3" }, cancellationToken: ct));
        copies = copies.ToList();

        checks.Add(("③ 三份副本里只有最后一份结算",
            copies.Count() == 3 && copies.Count(x => x.Hit == true) == 1 && copies.First().Hit is null,
            $"副本 {copies.Count()} 行、已判 {(copies.Count(x => x.Hit == true))} 行、首份 Hit={(copies.FirstOrDefault().Hit is null ? "NULL" : copies.First().Hit.ToString())}"));

        // ── 场景 ④：发布状态机与发布闸（2026-11-14 第七轮） ──
        //
        // 这一节验的不是统计口径，而是「什么情况下**不许**发布」：
        //   缺一场输入 / 缺一档存档 / 概率单位异常 → 发布闸不通过；
        //   failed / partial 轮次 → 榜面仍指上一成功轮；
        //   零注但有输入 → 照样发布，且输入与存档必须完整。
        //
        // 日期刻意放在 400 天前：`GetReportAsync(30)` 是近 30 天口径，
        // 放到窗口内会把上面几节的对账数一起改掉，届时分不清是哪一段错了。
        var d3 = DateTime.Today.AddDays(-400);
        var runAt = d3.AddHours(12);

        // 完整档位域（五玩法 54 档）。源侧账本要求「已启售玩法必须是完整域」，
        // 所以这里不能再拿三档 SPF 当源侧样本——那不是简化，而是另一种形态的缺档。
        static List<string> FullKeys() => JcPlayDomain.Plays
            .SelectMany(p => JcPlayDomain.Of(p)!.Select(c => $"{p}|{c}")).ToList();
        static MilkSourceMatch SrcOf(long mid, string no, DateTime day, DateTime at, IEnumerable<string> keys) => new()
        {
            MatchId = mid, MatchNo = no, LeagueName = "审计联赛", HomeTeam = "主队", AwayTeam = "客队",
            MatchTime = day.AddHours(20), SalesDate = day, LastSampleAt = at,
            OptionKeys = keys.ToList()
        };

        MilkInputMatch Input(long mid, string no, params string[] codes) => new()
        {
            MatchId = mid, MatchNo = no, MatchTime = d3.AddHours(20), VersionTag = "dc1|audit",
            Options = codes.Select(c => new MilkInputOption { PlayType = JcPlayTypes.Spf, OptionCode = c }).ToList()
        };
        // 与源侧同域的全量凭证：只用于「场次/档位对齐」的判据，不落库（落库的那份仍按 SPF 三档构造）。
        MilkInputMatch InputFull(long mid, string no, IEnumerable<string> keys) => new()
        {
            MatchId = mid, MatchNo = no, MatchTime = d3.AddHours(20), VersionTag = "dc1|audit",
            Options = keys.Select(k => new MilkInputOption
            {
                PlayType = k[..k.IndexOf('|')], OptionCode = k[(k.IndexOf('|') + 1)..]
            }).ToList()
        };
        static MilkProbSnapshot Snap(DateTime d, long mid, string code, decimal pure) => new()
        {
            SnapshotDate = d, MatchId = mid, PlayType = JcPlayTypes.Spf, OptionCode = code,
            Odds = 100m / pure, MarketPct = pure, ModelPctPure = pure, BlendedPct = pure,
            MarketPriced = true, ModelVersion = "dc1|audit"
        };
        MilkSourceMatch Src(long mid, string no) => SrcOf(mid, no, d3, runAt.AddMinutes(-2), FullKeys());

        var inputsD = new List<MilkInputMatch> { Input(M1, "周日001", "3", "1", "0"), Input(M2, "周日002", "3", "1", "0") };
        var inputsFullD = new List<MilkInputMatch>
        {
            InputFull(M1, "周日001", FullKeys()), InputFull(M2, "周日002", FullKeys())
        };
        var sourcesD = new List<MilkSourceMatch> { Src(M1, "周日001"), Src(M2, "周日002") };
        var snapsD = new List<MilkProbSnapshot>
        {
            Snap(d3, M1, "3", 45m), Snap(d3, M1, "1", 27m), Snap(d3, M1, "0", 28m),
            Snap(d3, M2, "3", 40m), Snap(d3, M2, "1", 30m), Snap(d3, M2, "0", 30m)
        };

        // RA：完整一轮 → 发布。
        await milk.StartRunAsync(new MilkRun
        {
            RunId = "AUDIT-GATE-A", StartedAt = runAt, RawMatches = 2, SalesDates = new[] { d3 }
        }, d3, "rewrite", "审计：完整轮", ct);
        await milk.SaveDayAsync(d3, new List<MilkPick>
        {
            new() { MatchId = M1, MatchNo = "周日001", PlayType = JcPlayTypes.Spf, OptionCode = "3", Odds = 2.22m }
        }, "AUDIT-GATE-A", ct);
        await milk.SaveInputsAsync(d3, inputsD, "AUDIT-GATE-A", ct);
        await milk.SaveProbSnapshotsAsync(d3, snapsD, "AUDIT-GATE-A", ct);
        await milk.SaveSourceAsync("AUDIT-GATE-A", d3, sourcesD, Array.Empty<MilkInputRejection>(), ct);
        await milk.PublishRunAsync("AUDIT-GATE-A",
            new[] { new MilkRunDateCount { SalesDate = d3, PickCount = 1 } }, 2, 6, 0, 1, "审计", ct);

        var boardA = await milk.GetDayAsync(d3, ct);
        checks.Add(("④-1 完整轮发布后榜面指本轮",
            boardA.Count == 1 && boardA.All(x => x.MatchId == M1),
            $"榜面 {boardA.Count} 注，MatchId={string.Join(",", boardA.Select(x => x.MatchId))}"));

        // RB：注单写进去了，但标成 failed（模拟「只写成功一部分」）→ 不许顶掉 RA。
        await milk.StartRunAsync(new MilkRun
        {
            RunId = "AUDIT-GATE-B", StartedAt = runAt.AddMinutes(5), RawMatches = 2, SalesDates = new[] { d3 }
        }, d3, "rewrite", "审计：失败轮", ct);
        await milk.SaveDayAsync(d3, new List<MilkPick>
        {
            new() { MatchId = M2, MatchNo = "周日002", PlayType = JcPlayTypes.Spf, OptionCode = "0", Odds = 3.30m }
        }, "AUDIT-GATE-B", ct);
        await milk.CloseRunAsync("AUDIT-GATE-B", MilkRunStatus.Failed, "审计：存档缺一档", 2, 5, 0, 1, ct);

        var boardB = await milk.GetDayAsync(d3, ct);
        checks.Add(("④-2 failed 轮不覆盖上一成功榜面",
            boardB.Count == 1 && boardB.All(x => x.MatchId == M1),
            $"榜面 {boardB.Count} 注，MatchId={string.Join(",", boardB.Select(x => x.MatchId))}"));

        // RC：期号确认但采集不完整 → partial，一行不动。
        await milk.StartRunAsync(new MilkRun
        {
            RunId = "AUDIT-GATE-C", StartedAt = runAt.AddMinutes(10), RawMatches = 2, SalesDates = new[] { d3 }
        }, d3, "partial", "审计：采集不完整", ct);
        await milk.CloseRunAsync("AUDIT-GATE-C", MilkRunStatus.Partial, "审计：采集不完整", 2, 6, 0, 0, ct);
        var boardC = await milk.GetDayAsync(d3, ct);
        checks.Add(("④-3 partial 轮不覆盖上一成功榜面",
            boardC.Count == 1 && boardC.All(x => x.MatchId == M1),
            $"榜面 {boardC.Count} 注，MatchId={string.Join(",", boardC.Select(x => x.MatchId))}"));

        // ── 发布闸：三类缺陷必须判不通过（判据用真实读回的库内行） ──
        var vSource = MilkSourceSlate.Verify(sourcesD, inputsFullD, Array.Empty<MilkInputRejection>(), runAt);
        checks.Add(("④-4 源 2 场输入 2 场：源侧账本通过",
            vSource.Ok && vSource.ComputedCount == 2,
            $"{vSource.Summary}；{vSource.Detail}"));

        var vMissMatch = MilkSourceSlate.Verify(
            sourcesD, inputsFullD.Take(1).ToList(), Array.Empty<MilkInputRejection>(), runAt);
        checks.Add(("④-5 缺一场输入（无拒绝记录）→ 不通过",
            !vMissMatch.Ok && vMissMatch.Issues.Any(i => i.Kind == "missing-input"),
            vMissMatch.Detail));

        var expectedSlate = MilkExpectedSlate.Build(inputsD);
        var readD = await milk.ReadRunLedgerAsync("AUDIT-GATE-A", ct);
        List<MilkLedgerRow> ActD(string table) => readD.Where(x => x.Table == table)
            .Select(x => new MilkLedgerRow { Key = x.Key, Count = x.Count }).ToList();
        var okLedger = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = expectedSlate.Rows, Actual = ActD("snapshot") },
            new MilkLedgerPair { Table = "input", Expected = MilkRunLedger.FromInputs(inputsD), Actual = ActD("input") }
        });
        checks.Add(("④-6 6 档存档逐键对齐：通过",
            okLedger.Ok, okLedger.Ok ? okLedger.Summary : okLedger.Detail));

        // 删掉一档（M1 的让球? 这里删 SPF 1）→ 同一个核对必须报 missing。
        await c.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.FbMilkProbSnapshot
              WHERE RunId='AUDIT-GATE-A' AND MatchId=@m AND PlayType=N'SPF' AND OptionCode=N'1'",
            new { m = M1 }, cancellationToken: ct));
        var readD2 = await milk.ReadRunLedgerAsync("AUDIT-GATE-A", ct);
        List<MilkLedgerRow> ActD2(string table) => readD2.Where(x => x.Table == table)
            .Select(x => new MilkLedgerRow { Key = x.Key, Count = x.Count }).ToList();
        var missBand = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = expectedSlate.Rows, Actual = ActD2("snapshot") }
        });
        checks.Add(("④-7 缺一档存档 → 不通过",
            !missBand.Ok && missBand.Issues.Any(i => i.Kind == "missing"),
            missBand.Detail));

        // 概率单位异常：BF 写成 0~1（11-14 生产上的真实形态）。
        var unitRows = snapsD.Select(x => new MilkProbSnapshot
        {
            MatchId = x.MatchId, PlayType = x.PlayType, OptionCode = x.OptionCode,
            ModelPctPure = x.ModelPctPure, MarketPct = x.MarketPct, BlendedPct = x.BlendedPct
        }).ToList();
        unitRows.Add(new MilkProbSnapshot
        {
            MatchId = M1, PlayType = JcPlayTypes.Bf, OptionCode = "1:0",
            Odds = 7m, MarketPct = 14.2m, ModelPctPure = 0.079m, BlendedPct = 13.0m
        });
        var unitIssues = MilkProbScale.Verify(unitRows);
        checks.Add(("④-8 BF 小数当百分数 → 不通过",
            unitIssues.Any(i => i.Kind == "prob-not-normalized") && unitIssues.Any(i => i.Kind == "unit-mismatch"),
            unitIssues.Count == 0 ? "未拦截" : string.Join("；", unitIssues.Take(2))));

        // RD：零注但有输入 → 仍然发布，输入/存档/源侧账本都要完整。
        await milk.StartRunAsync(new MilkRun
        {
            RunId = "AUDIT-GATE-D", StartedAt = runAt.AddMinutes(15), RawMatches = 2, SalesDates = new[] { d3 }
        }, d3, "rewrite", "审计：零注有输入", ct);
        await milk.SaveDayAsync(d3, new List<MilkPick>(), "AUDIT-GATE-D", ct);
        await milk.SaveInputsAsync(d3, inputsD, "AUDIT-GATE-D", ct);
        await milk.SaveProbSnapshotsAsync(d3, snapsD, "AUDIT-GATE-D", ct);
        var srcRowsD = await milk.SaveSourceAsync("AUDIT-GATE-D", d3, sourcesD, Array.Empty<MilkInputRejection>(), ct);
        await milk.PublishRunAsync("AUDIT-GATE-D",
            new[] { new MilkRunDateCount { SalesDate = d3, PickCount = 0 } }, 2, 6, 0, 0, "审计：零注", ct);

        var boardD = await milk.GetDayAsync(d3, ct);
        var dateRow = await c.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT PickCount FROM dbo.FbMilkRunDate WHERE RunId='AUDIT-GATE-D' AND SalesDate=@d",
            new { d = d3 }, cancellationToken: ct));
        var readD3 = await milk.ReadRunLedgerAsync("AUDIT-GATE-D", ct);
        var inputRowsD = readD3.Count(x => x.Table == "input");
        var snapRowsD = readD3.Count(x => x.Table == "snapshot");
        var sourceRowsD = readD3.Count(x => x.Table == "source");
        checks.Add(("④-9 零注但有输入：照样发布且存档完整",
            boardD.Count == 0 && dateRow == 0 && inputRowsD == 2 && snapRowsD == 6 && srcRowsD == 2 && sourceRowsD == 2,
            $"榜面 {boardD.Count} 注／RunDate.PickCount={dateRow}；输入 {inputRowsD} 行、存档 {snapRowsD} 档、"
            + $"源侧账本 {sourceRowsD} 行（写入 {srcRowsD} 行）"));

        // ── 源侧档位域（2026-11-14 第八轮）：缺档必须阻断，且不能一律硬套 54 ──
        var gapKeys = FullKeys();
        gapKeys.Remove("BF|1:0");
        var vGap = MilkSourceSlate.Verify(
            new[] { SrcOf(M1, "周日001", d3, runAt.AddMinutes(-2), gapKeys) },
            new[] { InputFull(M1, "周日001", gapKeys) },
            Array.Empty<MilkInputRejection>(), runAt);
        checks.Add(("④-10 源侧少一档（BF|1:0）→ 不通过",
            !vGap.Ok
            && vGap.Issues.Any(i => i.Kind == "source-option-gap" && i.Key.Contains("1:0"))
            && !vGap.Issues.Any(i => i.Kind == "missing-option"),
            vGap.Detail));

        // 整块缺一个玩法 + 引擎据此报 no-odds：缺档与被推翻的豁免两条同时拦。
        var noSpf = FullKeys().Where(k => !k.StartsWith("SPF|", StringComparison.Ordinal)).ToList();
        var vWhole = MilkSourceSlate.Verify(
            new[] { SrcOf(M1, "周日001", d3, runAt.AddMinutes(-2), noSpf) },
            Array.Empty<MilkInputMatch>(),
            new[]
            {
                new MilkInputRejection
                {
                    MatchId = M1, MatchNo = "周日001", HomeTeam = "主队", AwayTeam = "客队",
                    Reason = MilkInputRejection.ReasonNoOdds, Evidence = "options=0;source=0"
                }
            },
            runAt);
        checks.Add(("④-11 整块缺 SPF 且报 no-odds → 缺档 + 豁免被推翻",
            !vWhole.Ok
            && vWhole.Issues.Any(i => i.Kind == "source-option-gap" && i.Key.Contains("SPF"))
            && vWhole.Issues.Any(i => i.Kind == "contradicted-rejection")
            && !vWhole.Rejections.Any(x => x.Accepted),
            vWhole.Detail));

        // 总数仍是 54（BF 少一档、BQ 多一条脏码）：只看档数会判「齐」，逐档位才看得出来。
        var swapped = FullKeys();
        swapped.Remove("BF|2:1");
        swapped.Add("BQ|平/胜");
        var vSwap = MilkSourceSlate.Verify(
            new[] { SrcOf(M1, "周日001", d3, runAt.AddMinutes(-2), swapped) },
            new[] { InputFull(M1, "周日001", swapped) },
            Array.Empty<MilkInputRejection>(), runAt);
        checks.Add(("④-12 总数 54 但 BF 少一档、BQ 多脏码 → 只报 BF",
            !vSwap.Ok
            && vSwap.Issues.Count(i => i.Kind == "source-option-gap") == 1
            && vSwap.Issues.Any(i => i.Kind == "source-option-gap" && i.Key.Contains("2:1")),
            vSwap.Detail));

        // ── ④-13 源快照按 StartedAt 截止（2026-11-14 第八轮） ──
        //
        // 造两批受注卡：asOf-6min 两场、asOf+20min 三场（模拟下一轮/事后补采）；
        // M6 有两条采样：asOf-3min 的 SPF 三档、asOf+15min 的 BF 三十一档。
        // 用 asOf 查必须只看到两场、M6 只有三档、采样停在 asOf-3min——
        // 这就是「事后回查看到 10:58 的 11 场」不能用来证明 08:40 那一轮的原因。
        // `SELECT INTO` 会把 IDENTITY 一起带过来，所以 CardId 不显式给（给了会报 544）。
        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.BettingCard
                (Board, Expect, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam, MatchTime, IsSingle, CapturedAt)
              VALUES ('jc', N'审计', N'周日101', @m6, N'审计联赛', N'甲', N'乙', @mt, 0, @a1),
                     ('jc', N'审计', N'周日102', @m7, N'审计联赛', N'丙', N'丁', @mt, 0, @a1),
                     ('jc', N'审计', N'周日101', @m6, N'审计联赛', N'甲', N'乙', @mt, 0, @a2),
                     ('jc', N'审计', N'周日102', @m7, N'审计联赛', N'丙', N'丁', @mt, 0, @a2),
                     ('jc', N'审计', N'周日103', @m8, N'审计联赛', N'戊', N'己', @mt, 0, @a2)",
            new
            {
                m6 = M6, m7 = M7, m8 = M8, mt = d3.AddHours(20),
                a1 = runAt.AddMinutes(-6), a2 = runAt.AddMinutes(20)
            }, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbJcOddsHistory (MatchId, MatchNo, PlayType, OptionCode, OptionName, Odds, CapturedAt)
              VALUES (@m6, N'周日101', 'SPF', '3', N'胜', 2.10, @early),
                     (@m6, N'周日101', 'SPF', '1', N'平', 3.30, @early),
                     (@m6, N'周日101', 'SPF', '0', N'负', 3.40, @early)",
            new { m6 = M6, early = runAt.AddMinutes(-3) }, cancellationToken: ct));

        var lateRows = JcPlayDomain.Bf
            .Select((code, i) => new { m = M6, code, odds = 7m + i, at = runAt.AddMinutes(15) })
            .ToList();
        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbJcOddsHistory (MatchId, MatchNo, PlayType, OptionCode, OptionName, Odds, CapturedAt)
              VALUES (@m, N'周日101', 'BF', @code, @code, @odds, @at)",
            lateRows, cancellationToken: ct));

        var bets = new BettingRepository(new SqlConnectionFactory(auditStr));
        var srcSnap = await bets.GetJcSourceSnapshotAsync(runAt, ct);
        var snapM6 = srcSnap.FirstOrDefault(x => x.MatchId == M6);
        checks.Add(("④-13 源快照按 StartedAt 截止：晚于 asOf 的采样不进本轮",
            srcSnap.Count == 2 && snapM6 is not null && snapM6.OptionKeys.Count == 3
            && snapM6.LastSampleAt is DateTime at6 && at6 <= runAt,
            $"源 {srcSnap.Count} 场（应 2）；M6 档位 {(snapM6?.OptionKeys.Count ?? -1)}（应 3）；"
            + $"最新采样 {snapM6?.LastSampleAt:HH:mm}（应 ≤ {runAt:HH:mm}）"));

        // ④-13b：**引擎取价也要停在同一时刻**（2026-11-14 第九轮）。
        // 之前只有源快照带边界，引擎仍取「此刻最新」：账本核的是 asOf 的清单，
        // 票却是用 asOf 之后落库的价算的——缺档、超龄这些判据全都查不到真正被用的那份数据。
        var jcOdds = new JcOddsRepository(new SqlConnectionFactory(auditStr));
        var latestAt = await jcOdds.GetLatestAsync(new[] { M6 }, ct, runAt);
        var latestAny = await jcOdds.GetLatestAsync(new[] { M6 }, ct);
        checks.Add(("④-13b 引擎取价按同一时刻截止：asOf 之后落库的档位不进本轮",
            latestAt.Count == 3 && latestAt.All(p => p.PlayType == "SPF")
            && latestAny.Count == 3 + JcPlayDomain.Bf.Count,
            $"asOf 内 {latestAt.Count} 档（应 3，全 SPF）；不加边界 {latestAny.Count} 档（应 {3 + JcPlayDomain.Bf.Count}）"));

        // ── ④-14 截止后新增采样不改变本轮计算（2026-11-14 第十轮） ──
        //
        // ④-13/13b 只证明「边界写对了」，这一条证明**边界之后的写入不会改本轮的结果**：
        // 同一个 asOf 先取一次价、把引擎真正要用的输入逐档记下来，再插一条 asOf+8min 的同档新价，
        // 用同一个 asOf 再取一次。两份输入必须逐档相同，派生量（是否完整、归一因子）也必须相同。
        // 同时不加边界再查一次，确认那条新价**确实落库了**——否则「没变化」可能只是因为压根没写进去。
        static string Digest(IEnumerable<JcOddsHistoryPoint> pts)
            => string.Join(";", pts
                .OrderBy(p => p.PlayType, StringComparer.Ordinal)
                .ThenBy(p => p.OptionCode, StringComparer.Ordinal)
                .Select(p => $"{p.PlayType}|{p.OptionCode}|{p.Odds:0.###}"));

        var beforePts = await jcOdds.GetLatestAsync(new[] { M6 }, ct, runAt);
        var beforeDigest = Digest(beforePts);
        var spfOver = MilkPickEngine.StandardOverRound(JcPlayTypes.Spf);
        var beforeNorm = MilkPickEngine.BlendNormalizer(beforePts, JcPlayTypes.Spf, spfOver);
        var beforeComplete = MilkPickEngine.SnapshotComplete(beforePts, JcPlayTypes.Spf);

        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbJcOddsHistory (MatchId, MatchNo, PlayType, OptionCode, OptionName, Odds, CapturedAt)
              VALUES (@m6, N'周日101', 'SPF', '3', N'胜', 1.55, @late2)",
            new { m6 = M6, late2 = runAt.AddMinutes(8) }, cancellationToken: ct));

        var afterPts = await jcOdds.GetLatestAsync(new[] { M6 }, ct, runAt);
        var afterAny = await jcOdds.GetLatestAsync(new[] { M6 }, ct);
        var afterDigest = Digest(afterPts);
        var afterNorm = MilkPickEngine.BlendNormalizer(afterPts, JcPlayTypes.Spf, spfOver);
        var afterComplete = MilkPickEngine.SnapshotComplete(afterPts, JcPlayTypes.Spf);
        var spf3Any = afterAny.FirstOrDefault(p => p.PlayType == JcPlayTypes.Spf && p.OptionCode == "3");

        checks.Add(("④-14 截止后的新采样不改变本轮计算（同 asOf 两次取价逐档相同）",
            beforeDigest == afterDigest && beforeNorm == afterNorm && beforeComplete == afterComplete
            && spf3Any is not null && spf3Any.Odds == 1.55m,
            $"输入 {beforePts.Count}→{afterPts.Count} 档；逐档相同={beforeDigest == afterDigest}；"
            + $"归一 {beforeNorm:0.000000}→{afterNorm:0.000000}；完整 {beforeComplete}→{afterComplete}；"
            + $"不加边界看到 SPF|3 = {spf3Any?.Odds}（应 1.55）"));

        // ── ④-15 别名补齐后：库检命中一线队、不混梯队（2026-11-14 第十一轮） ──
        //
        // 用户点名「零计数不能证明是真新队」。这条判据的前半段（场次）走的是**真实 SQL 链路**：
        // 竞彩简称 → 别名表 → 核心名 → `LIKE` 检索 → 身份闸。任何一环把梯队并进来，
        // 「0 场」或「31 场」就会被当成判新队的依据，而这正是「谢周三 0 场 / 谢菲尔德星期三 74 场」
        // 在同一个零下被混为一谈的原因。
        //
        // 造量刻意让梯队更多：谢菲尔德星期三 4 场、谢菲尔德星期三U21 6 场（核心名相同，只能靠身份闸挡）；
        // 云达不莱梅 3 场、云达不莱梅U19 4 场；不莱梅港 6 场（另一家俱乐部，连名字都不该命中）。
        var aliasDay = DateTime.Today.AddDays(-5).AddHours(20);
        async Task SeedAliasAsync(string team, int count, long firstId)
            => await c.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbMatch (MatchId, MatchTime, HomeScore, AwayScore, Status, HomeTeamName, AwayTeamName)
                  VALUES (@id, @mt, 1, 0, 3, @h, N'审计对手')",
                Enumerable.Range(0, count).Select(i => new
                {
                    id = firstId + i, mt = aliasDay.AddHours(i), h = team
                }), cancellationToken: ct));

        await SeedAliasAsync("谢菲尔德星期三", 4, 900000301);
        await SeedAliasAsync("谢菲尔德星期三U21", 6, 900000311);
        await SeedAliasAsync("谢周三U21", 5, 900000321);
        await SeedAliasAsync("云达不莱梅", 3, 900000331);
        await SeedAliasAsync("云达不莱梅U19", 4, 900000341);
        await SeedAliasAsync("不莱梅港", 6, 900000351);

        var gamesByAlias = await bets.GetRecentTeamGamesAsync(new[] { "谢周三", "不来梅", "谢周三U21" }, ct: ct);
        var variantsByAlias = await bets.GetTeamNameVariantsAsync(
            new[] { "谢周三", "不来梅", "全新测试FC" }, ct: ct);

        checks.Add(("④-15 别名库检：简称命中一线队场次，梯队与邻居俱乐部不并入",
            gamesByAlias.GetValueOrDefault("谢周三") == 4
            && gamesByAlias.GetValueOrDefault("不来梅") == 3
            && gamesByAlias.GetValueOrDefault("谢周三U21") == 5,
            $"谢周三={gamesByAlias.GetValueOrDefault("谢周三")}（应 4，梯队 6 场不许并）"
            + $"；不来梅={gamesByAlias.GetValueOrDefault("不来梅")}（应 3）"
            + $"；谢周三U21={gamesByAlias.GetValueOrDefault("谢周三U21")}（应 5，反向不误伤）"));

        checks.Add(("④-15b 变体探查：查到相近写法，梯队不算；没查到的返回空而非 null",
            variantsByAlias.GetValueOrDefault("谢周三") is { Count: 1 } v1 && v1[0] == "谢菲尔德星期三"
            && variantsByAlias.GetValueOrDefault("不来梅") is { Count: 1 } v2 && v2[0] == "云达不莱梅"
            && variantsByAlias.GetValueOrDefault("全新测试FC") is { Count: 0 },
            $"谢周三→[{string.Join(",", variantsByAlias.GetValueOrDefault("谢周三") ?? Array.Empty<string>())}]"
            + $"；不来梅→[{string.Join(",", variantsByAlias.GetValueOrDefault("不来梅") ?? Array.Empty<string>())}]"
            + $"；全新测试FC→{variantsByAlias.GetValueOrDefault("全新测试FC")?.Count ?? -1} 条（应 0，不是 null）"));

        // ── 总裁定：报表必须与空跑台账逐项相等 ──
        var milkRep = await milk.GetReportAsync(30, ct);
        var paperRep = await paper.GetReportAsync(30, ct);
        var slice = paperRep.Overall;

        checks.Add(("报表注数 = 台账已结算未撤注注数",
            milkRep.Overall.Total == slice.Settled,
            $"报表 {milkRep.Overall.Total} / 台账 {slice.Settled}"));

        checks.Add(("报表命中数 = 台账命中数",
            milkRep.Overall.Hit == slice.Hits,
            $"报表 {milkRep.Overall.Hit} / 台账 {slice.Hits}"));

        checks.Add(("报表命中赔率合计 = 台账回报",
            Math.Abs(milkRep.Overall.HitOdds - slice.Return) < 0.01m,
            $"报表 {milkRep.Overall.HitOdds:0.00} / 台账 {slice.Return:0.00}"));

        checks.Add(("撤注单独计数，不进命中率分母",
            slice.Voided == 1 && milkRep.Overall.Voided == slice.Voided && milkRep.Overall.Total == slice.Settled,
            $"台账撤注 {slice.Voided} 注；报表撤注列 {milkRep.Overall.Voided}；报表分母 {milkRep.Overall.Total}（台账已结算未撤注 {slice.Settled}）"));

        // 报表 vs 台账的逐注对账：**价格**必须一致（这是唯一不能被 COALESCE 抹平的一项）。
        // 命中/撤注两边按构造相同（报表已改成「已冻结的注以台账为准」），由上面三项总数对账覆盖；
        // 这里只盯价格，是因为它多轮重算后最容易走样，而走样后任何回报数都失去意义。
        var pRows = await paper.GetRowsAsync(30, ct);
        var final = PaperTradeMath.FinalVersion(pRows);
        var mismatches = new List<string>();
        foreach (var r in final.Where(x => x.Hit is not null))
        {
            var first = await c.QueryFirstOrDefaultAsync<decimal?>(new CommandDefinition(
                @"SELECT TOP 1 Odds FROM dbo.FbMilkPick
                  WHERE PickDate=@d AND MatchId=@m AND PlayType=@p AND OptionCode=@o
                  ORDER BY PickId",
                new { d = r.TradeDate, m = r.MatchId, p = r.PlayType, o = r.OptionCode }, cancellationToken: ct));

            if (first is null || first.Value != r.Odds)
                mismatches.Add($"{r.MatchNo} {r.PlayType}{r.OptionCode}: 注单首份 {first:0.00} vs 台账 {r.Odds:0.00}");
        }
        checks.Add(("逐注对账：台账冻结价 = 注单首份价",
            mismatches.Count == 0,
            mismatches.Count == 0 ? $"{final.Count(x => x.Hit is not null)} 注全部一致" : string.Join("；", mismatches)));

        // ── 输出 ──
        var sb = new StringBuilder();
        sb.AppendLine("| 场景 | 结论 | 实测 |");
        sb.AppendLine("|---|---|---|");
        foreach (var (name, ok, detail) in checks)
            sb.AppendLine($"| {name} | {(ok ? "通过" : "**不通过**")} | {detail} |");
        Console.WriteLine(sb.ToString());

        var failed = checks.Count(x => !x.Ok);
        Console.WriteLine(failed == 0
            ? "**全部通过**：多副本不会重复计票，价格以首次上榜为准，撤注不进分母，结算落在一份副本上也不丢命中。"
            : $"**{failed} 项不通过**：报表口径与冻结/结算规则不一致，不可用于战绩展示。");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>台账里的一行（只取对账要用的四列）。</summary>
    private sealed class FrozenRow
    {
        public decimal Odds { get; set; }
        public bool? Hit { get; set; }
        public bool Voided { get; set; }
        public decimal? Pnl { get; set; }
    }

    /// <summary>注单侧的「有效判定」：首份价格 + 任一副本命中 + 末份撤注。</summary>
    private sealed class EffectiveRow
    {
        public decimal Odds { get; set; }
        public int? AnyHit { get; set; }
        public int Voided { get; set; }
    }

    private static async Task<FrozenRow?> OneAsync(
        SqlConnection c, DateTime d, long matchId, string play, string code, CancellationToken ct)
        => await c.QueryFirstOrDefaultAsync<FrozenRow>(
            new CommandDefinition(
                @"SELECT Odds, Hit, Voided, Pnl FROM dbo.FbPaperTrade
                  WHERE TradeDate=@d AND MatchId=@m AND PlayType=@p AND OptionCode=@o",
                new { d, m = matchId, p = play, o = code }, cancellationToken: ct));

    /// <summary>建库 + 从生产库克隆结构（只克隆表结构，不搬数据）。每次重建，隔离库是一次性的。</summary>
    private static async Task InitAsync(SqlConnectionStringBuilder prod, string prodDb, CancellationToken ct)
    {
        if (!prodDb.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            throw new InvalidOperationException($"库名含意外字符，拒绝拼接：{prodDb}");

        var masterStr = new SqlConnectionStringBuilder(prod.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var master = new SqlConnection(masterStr))
        {
            await master.OpenAsync(ct);
            await master.ExecuteAsync(new CommandDefinition(
                $"IF DB_ID(N'{AuditDbName}') IS NULL CREATE DATABASE [{AuditDbName}];",
                commandTimeout: 120, cancellationToken: ct));
        }

        var tables = new[]
        {
            "FbMilkPick", "FbMilkGate", "FbMilkProbSnapshot", "FbMilkInput",
            "FbMilkRun", "FbMilkRunDate", "FbPaperTrade", "FbMatch",
            // 榜面读取会联它取「当前固定奖金」（GetDayAsync），不克隆就整页报「对象名无效」。
            "FbJcOddsHistory",
            // 源侧快照的时间边界用例要造受注卡批次（④-13）。
            "BettingCard",
            // 补列自愈逻辑会读写它（撤注/告警回写）。不克隆，第一次 Settle 就报「对象不存在」。
            "FbTurnAlert"
        };

        var sql = new StringBuilder();
        foreach (var t in tables)
        {
            sql.AppendLine($"IF OBJECT_ID(N'dbo.{t}', N'U') IS NOT NULL DROP TABLE dbo.{t};");
            sql.AppendLine($"SELECT * INTO dbo.{t} FROM [{prodDb}].dbo.{t} WHERE 1=0;");
        }

        // 索引/唯一键必须补回来：生产上「同轮内不许重复」是靠唯一键兜的，
        // 少了它，用例里的重复写入会静默成功，验证就失去意义。
        sql.AppendLine("ALTER TABLE dbo.FbMilkPick ADD CONSTRAINT PK_AT_MilkPick PRIMARY KEY CLUSTERED (PickId);");
        sql.AppendLine("ALTER TABLE dbo.FbMilkPick ADD CONSTRAINT UX_AT_MilkPick UNIQUE (PickDate, RunId, MatchId, PlayType, OptionCode);");
        sql.AppendLine("ALTER TABLE dbo.FbMilkGate ADD CONSTRAINT PK_AT_MilkGate PRIMARY KEY CLUSTERED (GateId);");
        sql.AppendLine("ALTER TABLE dbo.FbMilkGate ADD CONSTRAINT UX_AT_MilkGate UNIQUE (GateDate, RunId, MatchId);");
        sql.AppendLine("ALTER TABLE dbo.FbMilkRun ADD CONSTRAINT PK_AT_MilkRun PRIMARY KEY CLUSTERED (RunId);");
        sql.AppendLine("ALTER TABLE dbo.FbMilkRunDate ADD CONSTRAINT PK_AT_MilkRunDate PRIMARY KEY CLUSTERED (RunId, SalesDate);");
        sql.AppendLine("ALTER TABLE dbo.FbPaperTrade ADD CONSTRAINT PK_AT_PaperTrade PRIMARY KEY CLUSTERED (TradeId);");
        sql.AppendLine("ALTER TABLE dbo.FbPaperTrade ADD CONSTRAINT UQ_AT_PaperTrade UNIQUE (TradeDate, MatchId, PlayType, OptionCode);");

        // SELECT * INTO 只带列定义，**不带走默认值约束**（FbPaperTrade.FrozenAt、FbMatch.Source 这类）。
        // 不补回来，一插入就报「不能将值 NULL 插入列」，而那个错看起来像用例写错了。
        // 直接从生产库的元数据把默认值照搬过来，避免手抄漏项。
        sql.AppendLine($@"
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'ALTER TABLE dbo.' + QUOTENAME(t.name)
            + N' ADD CONSTRAINT ' + QUOTENAME(N'DF_AT_' + t.name + N'_' + c.name)
            + N' DEFAULT ' + dc.definition + N' FOR ' + QUOTENAME(c.name) + N';' + CHAR(10)
FROM [{prodDb}].sys.default_constraints dc
JOIN [{prodDb}].sys.tables t ON t.object_id = dc.parent_object_id
JOIN [{prodDb}].sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
WHERE t.name IN (N'FbMilkPick',N'FbMilkGate',N'FbMilkProbSnapshot',N'FbMilkInput',N'FbMilkRun',N'FbMilkRunDate',N'FbPaperTrade',N'FbMatch',N'FbTurnAlert',N'BettingCard');
IF @sql <> N'' EXEC sp_executesql @sql;");

        var auditStr = new SqlConnectionStringBuilder(prod.ConnectionString) { InitialCatalog = AuditDbName }.ConnectionString;
        await using var conn = new SqlConnection(auditStr);
        await conn.OpenAsync(ct);
        // 源侧账本表由仓储自建（生产上也是第一次落库时才建），所以不在克隆清单里；
        // 但每次运行要清空，否则上一次的 RunId 会留在里面。
        await conn.ExecuteAsync(new CommandDefinition(
            "IF OBJECT_ID(N'dbo.FbMilkSourceMatch', N'U') IS NOT NULL DELETE FROM dbo.FbMilkSourceMatch;",
            cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), commandTimeout: 180, cancellationToken: ct));
    }

    private static async Task SeedMatchesAsync(SqlConnection c, DateTime d1, DateTime d2, CancellationToken ct)
    {
        // 赛果刻意让四注全中、一注故意不中，算账才好对：
        //   M1 半 1:0 / 全 2:1 → 半全场 3-3 中
        //   M2 全 2:1          → 总进球 3 中
        //   M3 全 2:1          → 比分 2:1 中
        //   M4 全 2:0          → 胜平负 3 中
        //   M5 全 2:1          → 总进球 2 **不中**（3 球），且它全程撤注 → 只验「不进分母」
        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbMatch (MatchId, MatchTime, HomeScore, AwayScore, HalfHomeScore, HalfAwayScore, Status)
              VALUES (@m1, @t1, 2, 1, 1, 0, 3),
                     (@m2, @t2, 2, 1, 1, 0, 3),
                     (@m3, @t3, 2, 1, 1, 0, 3),
                     (@m4, @t4, 2, 0, 1, 0, 3),
                     (@m5, @t5, 2, 1, 1, 0, 3);",
            new
            {
                m1 = M1, t1 = d1.AddHours(20),
                m2 = M2, t2 = d1.AddHours(20),
                m3 = M3, t3 = d1.AddHours(20),
                m4 = M4, t4 = d2.AddHours(20),
                m5 = M5, t5 = d1.AddHours(20)
            }, cancellationToken: ct));
    }

    private static async Task SeedRunAsync(
        SqlConnection c, string runId, DateTime runDate, int hour, DateTime salesDate,
        Seed p1, CancellationToken ct)
        => await SeedRunAsync(c, runId, runDate, hour, salesDate, new[] { p1 }, ct);

    private static async Task SeedRunAsync(
        SqlConnection c, string runId, DateTime runDate, int hour, DateTime salesDate,
        Seed p1, Seed p2, Seed p3, CancellationToken ct)
        => await SeedRunAsync(c, runId, runDate, hour, salesDate, new[] { p1, p2, p3 }, ct);

    private static async Task SeedRunAsync(
        SqlConnection c, string runId, DateTime runDate, int hour, DateTime salesDate,
        IReadOnlyList<Seed> picks, CancellationToken ct)
    {
        var started = runDate.AddHours(hour);
        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbMilkRun (RunId, RunDate, StartedAt, Status, PublishedAt, RawMatches, InputCount, SnapshotRows, GateRows, PickRows, BoardMode)
              VALUES (@id, @d, @started, 'success', @started, 3, @n, @n, @n, @n, 'rewrite');",
            new { id = runId, d = runDate, started, n = picks.Count }, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.FbMilkRunDate (RunId, SalesDate, PickCount, CreatedAt)
              VALUES (@id, @d, @n, @started);",
            new { id = runId, d = salesDate, n = picks.Count, started }, cancellationToken: ct));

        foreach (var p in picks)
        {
            var matchTime = salesDate.AddHours(20);
            await c.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbMilkPick
                    (PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds,
                     ModelProbability, Ev, CounterHigh, BaselineProbability, BaselineEv,
                     ModelLowConfidence, ModelOverconfident, HalfDrawRefined, HomePriceRich,
                     Voided, Hit, SettledAt, CreatedAt, RunId, ModelPctPure)
                  VALUES (@d, @mid, @no, N'审计联赛', N'主队', N'客队', @mt,
                          @play, @code, N'档位', @odds,
                          20.0, 1.10, 0, 20.0, 1.10,
                          0, 0, 0, 0,
                          @voided, @hit, @settled, SYSDATETIME(), @runId, 20.0);",
                new
                {
                    d = salesDate, mid = p.MatchId, no = "周日001", mt = matchTime,
                    play = p.Play, code = p.Code, odds = p.Odds,
                    voided = p.Voided, hit = p.Hit, settled = p.SettledAt, runId
                }, cancellationToken: ct));
        }
    }
}
