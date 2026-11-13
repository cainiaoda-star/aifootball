using System;
using System.Collections.Generic;
using System.Linq;
using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 榜面重写计划（2026-11-13 第二轮；第三轮加采集完整性闸）。
///
/// 要钉住的是**三种形态不能混**：
///   · 期号确认、采集完整、全部被过滤 → 必须按确认的受注日撤掉失效旧票；
///   · 期号确认、但只采到部分场次     → 不许动（否则没采到的那几场旧票会被误撤）；
///   · 采集失败 / 连期号都确认不了     → 一行都不许动。
///
/// 11-13 的现场是第一种被当成了第三种：竞彩把国家队热身赛叫「国际赛」，
/// 并入禁推名单后受注 11 场全被挡在引擎之前，`build.Picks` / `Inputs` / 闸表三个全空，
/// 结果 11:40 那注 新加坡 vs 巴拉圭 从上午一直挂到晚上。
///
/// 用户当天又补了第二种：`RawMatches > 0` 只说明「拿到了一些场次」，
/// 部分采集会让工作集缩小，按它重写就是把没采到的那几场的旧票删掉。
/// </summary>
public class MilkBoardPlanTests
{
    private static readonly DateTime Today = new(2026, 11, 13);   // 周五

    private static MilkPick Pick(string? no, DateTime time, long id = 1) => new()
    {
        MatchId = id,
        MatchNo = no ?? string.Empty,
        MatchTime = time,
        PlayType = JcPlayTypes.Jq,
        OptionCode = "3",
        Odds = 4.10m
    };

    /// <summary>构造一个「当天 16:00 采集、16:40 计算」的采集状态。</summary>
    private static MilkCaptureState Capture(int batch, int known, string issue = "周五")
        => MilkCaptureState.Build(
            Today.AddHours(16), Today.AddHours(16).AddMinutes(40), MilkCaptureState.MaxBatchAge,
            new[] { new MilkIssueState { Issue = issue, BatchMatches = batch, KnownMatches = known } });

    /// <summary>
    /// 11-13 原样复现：受注 11 场全在禁推名单内 → 一注都没出，
    /// 但受注日仍然确认、采集完整，所以必须重写这一天的榜面。
    /// </summary>
    [Fact]
    public void AllFiltered_StillRewrites_Confirmed_SalesDate()
    {
        var run = new MilkRun
        {
            RunId = "20261113T164000-abcdef",
            StartedAt = Today.AddHours(16).AddMinutes(40),
            RawMatches = 11,
            SalesDates = new[] { Today },
            Capture = Capture(11, 11)
        };

        var plan = MilkBoardPlan.Create(run, Array.Empty<MilkPick>(), Today);

        Assert.Equal(MilkBoardMode.Rewrite, plan.Mode);
        Assert.True(plan.AllFiltered);
        Assert.Equal(0, plan.PickTotal);
        var w = Assert.Single(plan.Writes);
        Assert.Equal(Today, w.SalesDate);
        Assert.Empty(w.Picks);
    }

    /// <summary>采集失败 / 期号还没放出来：过滤前就是 0 场，此时清榜等于拿中断当撤榜理由。</summary>
    [Fact]
    public void NoInput_DoesNotTouchBoard()
    {
        var run = new MilkRun
        {
            RunId = "20261113T080000-000000",
            StartedAt = Today.AddHours(8),
            RawMatches = 0,
            SalesDates = Array.Empty<DateTime>(),
            Capture = Capture(0, 0)
        };

        var plan = MilkBoardPlan.Create(run, Array.Empty<MilkPick>(), Today);

        Assert.Equal(MilkBoardMode.Skip, plan.Mode);
        Assert.Empty(plan.Writes);
        Assert.False(plan.AllFiltered);
        Assert.Contains("不清榜", plan.Reason);
    }

    /// <summary>有行但算不出受注日（场次号与开赛时间都缺）：同样无法确认期号，按保守处理。</summary>
    [Fact]
    public void RawMatchesPositive_ButNoSalesDates_Skips()
    {
        var run = new MilkRun
        {
            RunId = "r1", RawMatches = 3, SalesDates = Array.Empty<DateTime>(), Capture = Capture(3, 3)
        };

        var plan = MilkBoardPlan.Create(run, Array.Empty<MilkPick>(), Today);

        Assert.Equal(MilkBoardMode.Skip, plan.Mode);
        Assert.Empty(plan.Writes);
    }

    /// <summary>
    /// ★ 用户 11-13 第三轮点名的一条：**只采到部分场次，不得误撤其余旧票**。
    ///
    /// 现场形态：`BettingCard` 是一天一批的 MERGE，某次只抓到 11 场里的 8 场，
    /// 另外 3 行的 `CapturedAt` 停在上一批，`GetCardsAsync` 只读最新那批 → 工作集变 8 场。
    /// 此时如果按「有输入就重写」，那 3 场的旧票会被当成「本轮没出」撤掉——而它们根本没被算过。
    /// 正确处置：一轮都不写、不删，榜面继续显示上一轮。
    /// </summary>
    [Fact]
    public void PartialCapture_DoesNotRewrite()
    {
        var run = new MilkRun
        {
            RunId = "20261113T164000-part01",
            StartedAt = Today.AddHours(16).AddMinutes(40),
            RawMatches = 8,                     // 只拿到 8 场，但 RawMatches>0
            SalesDates = new[] { Today },
            Capture = Capture(8, 11)            // 该期已知 11 场
        };

        var plan = MilkBoardPlan.Create(run, Array.Empty<MilkPick>(), Today);

        Assert.Equal(MilkBoardMode.Partial, plan.Mode);
        Assert.Empty(plan.Writes);
        Assert.Equal(3, plan.MissingMatches);
        Assert.Contains("8/11", plan.Reason);
        Assert.Contains("旧票保留", plan.Reason);
        Assert.Equal(MilkRunStatus.Partial, plan.RunStatus);
        // 关键：不能因为有 partial 就被判成「全部被过滤」而去重写
        Assert.False(plan.AllFiltered);
    }

    /// <summary>部分采集时即便出了注也不写：写一半的榜比不写更糟。</summary>
    [Fact]
    public void PartialCapture_Ignores_NewPicks_Too()
    {
        var run = new MilkRun
        {
            RunId = "20261113T164000-part02",
            StartedAt = Today.AddHours(16).AddMinutes(40),
            RawMatches = 8,
            SalesDates = new[] { Today },
            Capture = Capture(8, 11)
        };

        var plan = MilkBoardPlan.Create(run, new[] { Pick("周五001", Today.AddHours(20)) }, Today);

        Assert.Equal(MilkBoardMode.Partial, plan.Mode);
        Assert.Empty(plan.Writes);
        Assert.Equal(0, plan.PickTotal);
    }

    /// <summary>多个期里只要有一期不完整，整轮都不重写（不能拿完整那期去覆盖）。</summary>
    [Fact]
    public void MultiIssue_OnePartial_IsPartial()
    {
        var capture = MilkCaptureState.Build(
            Today.AddHours(16), Today.AddHours(16).AddMinutes(40), MilkCaptureState.MaxBatchAge,
            new[]
            {
                new MilkIssueState { Issue = "周五", BatchMatches = 8, KnownMatches = 11 },
                new MilkIssueState { Issue = "周六", BatchMatches = 11, KnownMatches = 11 }
            });

        var run = new MilkRun
        {
            RunId = "r3", RawMatches = 19, SalesDates = new[] { Today }, Capture = capture
        };

        var plan = MilkBoardPlan.Create(run, Array.Empty<MilkPick>(), Today);

        Assert.Equal(MilkBoardMode.Partial, plan.Mode);
        Assert.Equal(3, plan.MissingMatches);
        Assert.Contains("周六 11/11", plan.Reason);
    }

    /// <summary>没查采集状态（null）一律按「不可用」处理，不许猜「大概是完整的」。</summary>
    [Fact]
    public void MissingCapture_Skips()
    {
        var run = new MilkRun
        {
            RunId = "r4", RawMatches = 11, SalesDates = new[] { Today }, Capture = null
        };

        var plan = MilkBoardPlan.Create(run, Array.Empty<MilkPick>(), Today);

        Assert.Equal(MilkBoardMode.Skip, plan.Mode);
        Assert.Contains("采集批次不可用", plan.Reason);
    }

    /// <summary>批次太旧（可能是几天前的快照）不算「采集成功」。</summary>
    [Fact]
    public void StaleBatch_Skips()
    {
        var capture = MilkCaptureState.Build(
            Today.AddHours(16), Today.AddHours(16).Add(MilkCaptureState.MaxBatchAge).AddMinutes(1),
            MilkCaptureState.MaxBatchAge,
            new[] { new MilkIssueState { Issue = "周五", BatchMatches = 11, KnownMatches = 11 } });

        Assert.False(capture.Succeeded);
        Assert.Contains("过期", capture.Reason);

        var run = new MilkRun
        {
            RunId = "r5", RawMatches = 11, SalesDates = new[] { Today }, Capture = capture
        };
        Assert.Equal(MilkBoardMode.Skip, MilkBoardPlan.Create(run, null, Today).Mode);
    }

    /// <summary>
    /// 范围只能来自过滤前的场次，不能来自出票结果。
    /// 这里两期都算过（11-13 与 11-14），只有 11-14 出了注：
    /// 11-13 必须仍然被写一次（空列表 = 撤掉该日失效旧票），否则旧票会一直挂着。
    /// </summary>
    [Fact]
    public void Scope_Comes_From_Prefilter_Matches_Not_From_Picks()
    {
        var tomorrow = Today.AddDays(1);
        var run = new MilkRun
        {
            RunId = "20261113T224000-abcdef",
            StartedAt = Today.AddHours(22).AddMinutes(40),
            RawMatches = 11,
            SalesDates = new[] { Today, tomorrow },
            Capture = Capture(11, 11)
        };

        var picks = new[] { Pick("周六001", tomorrow.AddHours(13), 7) };

        var plan = MilkBoardPlan.Create(run, picks, Today);

        Assert.Equal(MilkBoardMode.Rewrite, plan.Mode);
        Assert.Equal(2, plan.Writes.Count);
        var todayWrite = plan.Writes.Single(w => w.SalesDate == Today);
        Assert.Empty(todayWrite.Picks);
        var tomorrowWrite = plan.Writes.Single(w => w.SalesDate == tomorrow);
        Assert.Single(tomorrowWrite.Picks);
        Assert.False(plan.AllFiltered);
        Assert.Equal(1, plan.PickTotal);
    }

    /// <summary>注单落在范围外的受注日（理论上不该有）：补进范围，别让这些注写不进去。</summary>
    [Fact]
    public void Picks_Outside_Scope_Are_Still_Written()
    {
        var tomorrow = Today.AddDays(1);
        var run = new MilkRun
        {
            RunId = "r2", RawMatches = 5, SalesDates = new[] { Today }, Capture = Capture(5, 5)
        };

        var plan = MilkBoardPlan.Create(run, new[] { Pick("周六001", tomorrow.AddHours(13)) }, Today);

        Assert.Equal(2, plan.Writes.Count);
        Assert.Contains(plan.Writes, w => w.SalesDate == tomorrow && w.Picks.Count == 1);
    }

    /// <summary>
    /// 引擎实测的「全部被过滤」形态（禁推名单挡在分布之前），
    /// 接到计划上必须走 Rewrite —— 这一条把引擎行为和榜面行为钉在一起。
    /// </summary>
    [Fact]
    public void Banned_League_All_Filtered_Still_Rewrites()
    {
        var model = BuildModel();
        var rec = new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = "周五001",
            MatchId = 1,
            LeagueName = "国际赛",
            HomeTeam = "新加坡",
            AwayTeam = "巴拉圭",
            MatchTime = Today.AddHours(20),
            TotalLine = 2.5m
        };

        var build = MilkPickEngine.BuildDetailed(new[] { rec }, new Dictionary<long, List<JcOddsHistoryPoint>>(), model);
        Assert.Empty(build.Picks);
        Assert.Empty(build.Inputs);           // 禁推在分布之前就 return 了
        Assert.Equal(0, build.Funnel.Distributed);

        var run = new MilkRun
        {
            RunId = "20261113T164000-fedcba",
            StartedAt = Today.AddHours(16).AddMinutes(40),
            RawMatches = 1,                    // 过滤前确实是 1 场
            SalesDates = new[] { Today },
            Capture = Capture(1, 1)
        };

        var plan = MilkBoardPlan.Create(run, build.Picks, Today);

        Assert.Equal(MilkBoardMode.Rewrite, plan.Mode);
        Assert.Single(plan.Writes);
        Assert.Empty(plan.Writes[0].Picks);
    }

    private static ScorelineModel BuildModel()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 2, AwayScore = 1 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 1, AwayScore = 2 });
        }
        return ScorelineModel.Fit(samples);
    }
}

/// <summary>
/// 一轮的落库结果（2026-11-13 第三轮）。
///
/// 发布判据不看「有没有行」，看四类落库是否都成功：注单、闸门、输入、存档。
/// 任一步失败都可能只写了一半，此时切榜面等于让用户看到半份数据。
/// </summary>
public class MilkRunStepsTests
{
    private static readonly string[] Required = { "picks", "gate", "input", "snapshot" };

    [Fact]
    public void All_Four_Ok_Publishes()
    {
        var s = new MilkRunSteps();
        foreach (var step in Required) s.Ok(step);

        Assert.True(s.AllOk(Required));
        Assert.Null(s.FirstFailure);
        Assert.Equal("picks=ok,gate=ok,input=ok,snapshot=ok", s.Summary);
    }

    [Fact]
    public void One_Failure_Blocks_Publish()
    {
        var s = new MilkRunSteps();
        s.Ok("picks");
        s.Ok("gate");
        s.Ok("input");
        s.Fail("snapshot", new InvalidOperationException("列名 'RunId' 无效"));

        Assert.False(s.AllOk(Required));
        Assert.Contains("snapshot=FAIL", s.Summary);
        Assert.Contains("InvalidOperationException", s.Detail);
        Assert.Contains("列名", s.Detail);
    }

    /// <summary>没记录 = 没跑成。少一步同样不许发布（异常可能在记录之前就抛出）。</summary>
    [Fact]
    public void Missing_Step_Blocks_Publish()
    {
        var s = new MilkRunSteps();
        s.Ok("picks");
        s.Ok("gate");
        s.Ok("input");

        Assert.False(s.AllOk(Required));
        Assert.True(s.AllOk("picks", "gate", "input"));
    }

    [Fact]
    public void Empty_Journal_Never_Publishes()
    {
        Assert.False(new MilkRunSteps().AllOk(Required));
    }

    /// <summary>有输入没有存档 = 存档缺失，不许发布（11-13 那张被清空的表就是这么来的）。</summary>
    [Fact]
    public void Snapshot_Must_Cover_Inputs()
    {
        Assert.False(MilkRunSteps.SnapshotCoversInputs(3, 0));
        Assert.True(MilkRunSteps.SnapshotCoversInputs(0, 0));      // 全被过滤：没有输入也就没有存档
        Assert.True(MilkRunSteps.SnapshotCoversInputs(3, 15));
    }
}

/// <summary>
/// 轮次标识（2026-11-13 第二轮）。四张表靠它关联到同一轮，格式必须可排序、且同秒不撞。
/// </summary>
public class MilkRunIdTests
{
    [Fact]
    public void RunId_Is_Unique_Within_The_Same_Second()
    {
        var at = new DateTime(2026, 11, 13, 16, 40, 52);
        var ids = Enumerable.Range(0, 50).Select(_ => MilkRun.NewId(at)).ToList();

        Assert.Equal(50, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("20261113T164052-", id));
    }

    /// <summary>
    /// RunId 可排序只是「方便看时刻」，**不是榜单语义**：
    /// 哪一轮上榜由 FbMilkRun.Status + StartedAt 决定。失败轮的 RunId 更大也不会被选中，
    /// 这一条由 audit-check 的发布段（C 轮 RunId 故意更大）在真库上验证。
    /// </summary>
    [Fact]
    public void RunId_Sorts_By_Time()
    {
        var a = MilkRun.NewId(new DateTime(2026, 11, 13, 9, 5, 0));
        var b = MilkRun.NewId(new DateTime(2026, 11, 13, 16, 40, 0));

        Assert.True(string.CompareOrdinal(a, b) < 0);
    }
}
