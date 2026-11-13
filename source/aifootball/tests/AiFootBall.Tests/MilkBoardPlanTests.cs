using System;
using System.Collections.Generic;
using System.Linq;
using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 榜面重写计划（2026-11-13 第二轮）。
///
/// 要钉住的是**两种形态不能混**：
///   · 成功取得有效期号、全部被过滤 → 必须按确认的受注日撤掉失效旧票；
///   · 采集失败 / 输入不完整 → 一行都不许动。
///
/// 11-13 的现场就是第一种被当成了第二种：竞彩把国家队热身赛叫「国际赛」，
/// 并入禁推名单后受注 11 场全被挡在引擎之前，`build.Picks` 为空、`Inputs` 为空、
/// 闸表为空——三个「空」长得跟「采集失败」一模一样，
/// 结果 11:40 那注 新加坡 vs 巴拉圭 从上午一直挂到晚上。
///
/// 判据因此只有一个：**过滤前**的受注场次与受注日。下面第
/// <see cref="Scope_Comes_From_Prefilter_Matches_Not_From_Picks"/> 条专门钉这一点。
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

    /// <summary>
    /// 11-13 原样复现：受注 11 场全在禁推名单内 → 一注都没出，
    /// 但受注日仍然确认（11-13），所以必须重写这一天的榜面。
    /// </summary>
    [Fact]
    public void AllFiltered_StillRewrites_Confirmed_SalesDate()
    {
        var run = new MilkRun
        {
            RunId = "20261113T164000-abcdef",
            StartedAt = Today.AddHours(16).AddMinutes(40),
            RawMatches = 11,
            SalesDates = new[] { Today }
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
            SalesDates = Array.Empty<DateTime>()
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
        var run = new MilkRun { RunId = "r1", RawMatches = 3, SalesDates = Array.Empty<DateTime>() };

        var plan = MilkBoardPlan.Create(run, Array.Empty<MilkPick>(), Today);

        Assert.Equal(MilkBoardMode.Skip, plan.Mode);
        Assert.Empty(plan.Writes);
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
            SalesDates = new[] { Today, tomorrow }
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
        var run = new MilkRun { RunId = "r2", RawMatches = 5, SalesDates = new[] { Today } };

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
            SalesDates = new[] { Today }
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
        // 前缀是时刻，可排序：MAX(RunId) 就是最新一轮（同秒内的先后不保证，这已够用）。
        Assert.All(ids, id => Assert.StartsWith("20261113T164052-", id));
    }

    [Fact]
    public void RunId_Sorts_By_Time()
    {
        var a = MilkRun.NewId(new DateTime(2026, 11, 13, 9, 5, 0));
        var b = MilkRun.NewId(new DateTime(2026, 11, 13, 16, 40, 0));

        Assert.True(string.CompareOrdinal(a, b) < 0);
    }
}
