using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 积分榜回算的护栏。三个坑都是真的：
/// 1. 同开赛时间的场次互相"看到"对方赛果 → 排名虚高；
/// 2. 跨赛季不断开 → 榜首带着上赛季积分跑一年；
/// 3. 赛季前两轮就出排名 → 那是队名排序，不是实力。
/// </summary>
public class StandingMathTests
{
    private static StandingMatch M(long id, string league, DateTime t, string h, string a, int hs, int @as)
        => new()
        {
            MatchId = id, LeagueName = league, MatchTime = t,
            HomeTeam = h, AwayTeam = a, HomeScore = hs, AwayScore = @as,
        };

    [Fact]
    public void SameKickoff_DoesNotSeeEachOthersResult()
    {
        // 关键是让 B 和 C 赛前**同分同净胜球**（并列第 2），第 4 轮两场同时开球。
        // 若实现是「一场一场更新」，先算 A vs B（B 惨败）之后，C 的名次会自动前进到第 2；
        // 正确实现必须仍按赛前状态给 C 判第 3。
        var t0 = new DateTime(2026, 8, 1, 20, 0, 0);
        var matches = new List<StandingMatch>
        {
            M(1, "测试联赛", t0, "A", "B", 1, 0),
            M(2, "测试联赛", t0, "C", "D", 1, 0),
            M(3, "测试联赛", t0.AddDays(7), "A", "C", 1, 0),
            M(4, "测试联赛", t0.AddDays(7), "B", "D", 1, 0),
            M(5, "测试联赛", t0.AddDays(14), "A", "D", 1, 0),
            M(6, "测试联赛", t0.AddDays(14), "B", "C", 0, 0),   // B、C 各 4 分、净胜球 0，并列
            // 第 4 轮同开：A vs B、C vs D
            M(7, "测试联赛", t0.AddDays(21), "A", "B", 5, 0),
            M(8, "测试联赛", t0.AddDays(21), "C", "D", 5, 0),
        };

        var ranks = StandingMath.Compute(matches);
        var r7 = ranks.Single(r => r.MatchId == 7);
        var r8 = ranks.Single(r => r.MatchId == 8);

        // 赛前：A 9 分第 1；B、C 各 4 分并列 2、3（按队名序 B 在前）；D 0 分第 4
        Assert.Equal(1, r7.HomeRankPre);   // A
        Assert.Equal(2, r7.AwayRankPre);   // B
        Assert.Equal(3, r8.HomeRankPre);   // C —— 不能因为同批的 A vs B 已经结算而变成第 2
        Assert.Equal(4, r8.AwayRankPre);   // D

        // 赛前场次也不能包含本轮：各队都只踢过 3 场
        Assert.Equal(3, r7.HomePlayedPre);
        Assert.Equal(3, r7.AwayPlayedPre);
        Assert.Equal(9, r7.HomePtsPre);    // A 赛前三连胜
        Assert.Equal(4, r8.HomePtsPre);    // C 赛前 1 胜 1 平 1 负
    }

    [Fact]
    public void LongGap_StartsNewSeason_ShortGapDoesNot()
    {
        var t0 = new DateTime(2026, 8, 1, 20, 0, 0);
        var matches = new List<StandingMatch>
        {
            M(1, "L", t0, "A", "B", 5, 0),
            M(2, "L", t0.AddDays(30), "A", "B", 5, 0),   // 30 天：同赛季
            M(3, "L", t0.AddDays(30 + 70), "A", "B", 1, 0), // 70 天：新赛季
        };

        var ranks = StandingMath.Compute(matches);
        Assert.Equal(1, ranks.Single(r => r.MatchId == 1).SeasonNo);
        Assert.Equal(1, ranks.Single(r => r.MatchId == 2).SeasonNo);
        Assert.Equal(2, ranks.Single(r => r.MatchId == 3).SeasonNo);

        // 新赛季首轮：A 的积分必须归零，不能带着上赛季的 6 分
        Assert.Equal(0, ranks.Single(r => r.MatchId == 3).HomePtsPre);
    }

    [Fact]
    public void EarlySeason_NoRankUntilThreePlayed()
    {
        var t0 = new DateTime(2026, 8, 1, 20, 0, 0);
        var matches = new List<StandingMatch>
        {
            M(1, "L", t0, "A", "B", 2, 0),
            M(2, "L", t0.AddDays(7), "A", "B", 2, 0),
            M(3, "L", t0.AddDays(14), "A", "B", 2, 0),  // 第 3 轮开赛前各赛 2 场 → 不出排名
            M(4, "L", t0.AddDays(21), "A", "B", 2, 0),  // 第 4 轮开赛前各赛 3 场 → 出排名
        };

        var ranks = StandingMath.Compute(matches);
        var r3 = ranks.Single(r => r.MatchId == 3);
        Assert.False(r3.Usable);
        Assert.Equal(0, r3.HomeRankPre);
        Assert.True(ranks.Single(r => r.MatchId == 4).Usable);
    }

    [Fact]
    public void RankPct_ZeroAtTop_OneAtBottom()
    {
        var row = new MatchPreRank { HomeRankPre = 1, AwayRankPre = 20, TeamCount = 20 };
        Assert.Equal(0m, row.HomeRankPct);
        Assert.Equal(1m, row.AwayRankPct);

        // 单队联赛不能除零
        var one = new MatchPreRank { HomeRankPre = 1, AwayRankPre = 1, TeamCount = 1 };
        Assert.Equal(0.5m, one.HomeRankPct);
    }

    [Fact]
    public void BuildTable_OnlyKeepsLatestSeason()
    {
        var t0 = new DateTime(2025, 8, 1, 20, 0, 0);
        var matches = new List<StandingMatch>
        {
            M(1, "L", t0, "A", "B", 3, 0),
            M(2, "L", t0.AddDays(90), "A", "C", 3, 0),
            // 隔 100 天 → 新赛季
            M(3, "L", t0.AddDays(190), "B", "C", 2, 1),
        };

        var table = StandingMath.BuildTable(matches, t0.AddDays(200));
        // 只保留最后赛季：A 上赛季踢过，本赛季没踢 → 不在榜上；B、C 在
        Assert.DoesNotContain(table, r => r.TeamName == "A");
        Assert.Contains(table, r => r.TeamName == "B");
        Assert.Equal(1, table.Single(r => r.TeamName == "B").Rank);
        Assert.Equal(1, table.Single(r => r.TeamName == "B").Played);
    }

    [Fact]
    public void LeadOutcome_UnderdogDetection()
    {
        // 主队第 18/20（垫底），半场 1:0，全场 1:2 → 落后方先进球且被逆转
        var r = new LeadOutcomeRow
        {
            HomeRankPre = 18, AwayRankPre = 6, TeamCount = 20,
            HalfHome = 1, HalfAway = 0, HomeScore = 1, AwayScore = 2,
            HasRank = true, HasHalf = true, Finished = true,
        };
        Assert.Equal(1, r.LeadSide);
        Assert.True(r.LeadIsUnderdog);
        Assert.Equal(false, r.LeadWon);
        Assert.Equal(false, r.LeadDrawn);
        Assert.Equal(true, r.LeadLost);

        // 强队主场先进球且赢：不是 underdog
        var strong = new LeadOutcomeRow
        {
            HomeRankPre = 1, AwayRankPre = 18, TeamCount = 20,
            HalfHome = 1, HalfAway = 0, HomeScore = 2, AwayScore = 0,
            HasRank = true, HasHalf = true, Finished = true,
        };
        Assert.False(strong.LeadIsUnderdog);
        Assert.Equal(true, strong.LeadWon);

        // 11-07 周六007 的实际情形：10/24 打 5/24，名次更低但分位 0.39（中上游）。
        // 用户说的「排名靠后」只指名次比对手低，这种必须算进去，否则会漏掉三场里的第一场。
        var mid = new LeadOutcomeRow
        {
            HomeRankPre = 10, AwayRankPre = 5, TeamCount = 24,
            HalfHome = 1, HalfAway = 0, HomeScore = 1, AwayScore = 2,
            HasRank = true, HasHalf = true, Finished = true,
        };
        Assert.True(mid.LeadIsUnderdog);
        Assert.Equal(true, mid.LeadLost);
    }
}
