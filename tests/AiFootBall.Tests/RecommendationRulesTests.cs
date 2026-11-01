using AiFootBall.Core.Models;
using Xunit;
using AiFootBall.Sources;

namespace AiFootBall.Tests;

public class RecommendationRulesTests
{
    [Fact]
    public void DualSignal_SmallDrop_AtOrAbove190_StaysHome()
    {
        var rec = Recommend(delta: -0.10m, home: 65m, away: 20m, spfHome: 1.90m, spfDraw: 3.40m, spfAway: 3.80m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(3, rec.SignalLevel);
        Assert.Equal(85.7m, rec.Confidence);
        Assert.True(RecommendationScoring.CountsInHitRate(rec.RecResult, false, rec.Confidence));
    }

    [Fact]
    public void DualSignal_BigDrop_IsTrap_NoPick()
    {
        var rec = Recommend(delta: -0.25m, home: 65m, away: 20m, spfHome: 1.90m, spfDraw: 3.40m, spfAway: 3.80m);

        Assert.Null(rec.RecResult);
        Assert.Contains("诱盘", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void DualSignal_BigRise_AwayHot_IsStrongAway()
    {
        // 大升+客热 ≠ 大降诱盘镜像，是客向强信号
        var rec = Recommend(delta: 0.25m, home: 20m, away: 65m, spfHome: 3.80m, spfDraw: 3.40m, spfAway: 1.95m);

        Assert.Equal('0', rec.RecResult);
        Assert.Equal(3, rec.SignalLevel);
        Assert.Contains("大升", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void DeepHomeHandicap_Above150_DoesNotEnterFeatured()
    {
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -0.75m,
            0m, 40m, 30m, null,
            1.85m, 3.40m, 4.20m,
            null, null,
            asianOpen: -0.75m, asianClose: -0.75m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("不进精选", rec.SignalDesc);
        rec.HitProbability = 60m;
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void DeepHomeHandicap_ShortPrice_StaysLevel2()
    {
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -1m,
            0m, 40m, 30m, null,
            1.45m, 4.20m, 6.50m,
            null, null,
            asianOpen: -1m, asianClose: -1m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(2, rec.SignalLevel);
        Assert.Contains("低水主胜", rec.SignalDesc);
    }

    [Fact]
    public void ALeague_Away_DoesNotEnterFeatured()
    {
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "澳超", "布里斯班狮吼", "奥克兰FC",
            DateTime.Today.AddHours(20), 0.25m,
            0.25m, 30m, 40m, null,
            2.60m, 3.40m, 2.40m,
            null, null);

        Assert.Equal('0', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("澳超客胜不进精选", rec.SignalDesc);
        rec.HitProbability = 60m;
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void SmallDropAlone_LongPrice_DoesNotFeature()
    {
        var rec = Recommend(delta: -0.10m, home: 40m, away: 30m, spfHome: 2.10m, spfDraw: 3.40m, spfAway: 3.50m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("不进精选", rec.SignalDesc);
        rec.HitProbability = 60m;
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void SmallDropAlone_ShortPrice_StaysLevel2()
    {
        var rec = Recommend(delta: -0.10m, home: 40m, away: 30m, spfHome: 1.80m, spfDraw: 3.50m, spfAway: 4.20m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(2, rec.SignalLevel);
        Assert.Equal(61.3m, rec.Confidence);
    }

    [Fact]
    public void SmallDrop_WithoutExchangeLead_IsNotPublished()
    {
        // 2026-11-01 周日004：小降 -0.06，必发 39.8 对 38.6，不能再标精选主胜
        var rec = Recommend(delta: -0.06m, home: 39.8m, away: 38.6m, spfHome: 1.59m, spfDraw: 3.43m, spfAway: 4.60m);

        Assert.Null(rec.RecResult);
        Assert.Equal(0, rec.SignalLevel);
        Assert.Contains("没有高出 10 个点", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
        Assert.Empty(RecommendationSelection.ForJcCalculated(new[] { rec }));
    }

    [Fact]
    public void BigDropAlone_IsTrap_NoPick()
    {
        var rec = Recommend(delta: -0.25m, home: 40m, away: 30m, spfHome: 2.10m, spfDraw: 3.40m, spfAway: 3.50m);

        Assert.Null(rec.RecResult);
        Assert.Contains("诱盘", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Sfc, new[] { rec }).Where(x => x.RecResult is not null));
    }

    [Fact]
    public void LowWater_Favorite_IsPublishedWithMark()
    {
        var rec = Recommend(delta: -0.10m, home: 65m, away: 20m, spfHome: 1.65m, spfDraw: 3.60m, spfAway: 4.80m);

        Assert.Equal('3', rec.RecResult);
        Assert.False(rec.IsOddsFallback);
        Assert.Contains("不是稳胆", rec.SignalDesc);
        Assert.DoesNotContain("照发", rec.SignalDesc);
        Assert.Equal(85.7m, rec.Confidence);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void LowWater_OddsFallback_DoesNotEnterFeatured()
    {
        // 无升降/必发，仅欧赔兜底的一边倒：标注低水，但不抬进精选
        var rec = Recommend(delta: null, home: null, away: null,
            spfHome: 1.20m, spfDraw: 5.50m, spfAway: 8.00m);

        Assert.Equal('3', rec.RecResult);
        Assert.True(rec.IsOddsFallback);
        Assert.Contains("不进精选", rec.SignalDesc);
        Assert.True(rec.Confidence < RecommendationScoring.PublishConfidence);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void ExtremeHomeHeat_IsNotPublished()
    {
        var rec = Recommend(delta: -0.10m, home: 97.5m, away: 2m, spfHome: 1.35m, spfDraw: 4.50m, spfAway: 8.00m);

        Assert.Null(rec.RecResult);
        Assert.Contains("极端大热", rec.SignalDesc);
        Assert.False(RecommendationScoring.CountsInHitRate(rec.RecResult, false, rec.Confidence));
    }

    [Fact]
    public void ColdSignal_DoesNotSwitchOntoLowWater()
    {
        var rec = Recommend(delta: 0.12m, home: 40m, away: 30m, spfHome: 1.42m, spfDraw: 4.10m, spfAway: 2.40m);

        Assert.Equal('0', rec.RecResult);
        Assert.Equal(41.7m, rec.Confidence);
        Assert.DoesNotContain("低水不发布", rec.SignalDesc);
    }

    [Fact]
    public void AwayLowWater_Favorite_IsPublishedWithMark()
    {
        var rec = Recommend(delta: 0.12m, home: 20m, away: 65m, spfHome: 4.80m, spfDraw: 3.60m, spfAway: 1.65m);

        Assert.Equal('0', rec.RecResult);
        Assert.Contains("不是稳胆", rec.SignalDesc);
        Assert.DoesNotContain("照发", rec.SignalDesc);
        Assert.Equal(71.4m, rec.Confidence);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void SignalConflict_NoPick()
    {
        var rec = Recommend(delta: -0.10m, home: 20m, away: 70m, spfHome: 2.10m, spfDraw: 3.40m, spfAway: 3.50m);

        Assert.Null(rec.RecResult);
        Assert.Contains("冲突", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void BigRiseAlone_LongAwayPrice_DoesNotFeature()
    {
        var rec = Recommend(delta: 1.03m, home: 48.6m, away: 35m,
            spfHome: 4.90m, spfDraw: 3.85m, spfAway: 2.20m, handicap: 1m);

        Assert.Equal('0', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("不进精选", rec.SignalDesc);
        rec.HitProbability = 60m;
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void BigRiseAlone_ShortAwayPrice_StaysLevel2()
    {
        var rec = Recommend(delta: 0.25m, home: 30m, away: 40m,
            spfHome: 3.40m, spfDraw: 3.30m, spfAway: 1.75m);

        Assert.Equal('0', rec.RecResult);
        Assert.Equal(2, rec.SignalLevel);
        Assert.Contains("看客胜", rec.SignalDesc);
    }

    [Fact]
    public void AsianConflict_DemotesHomeWhenLineLeavesDeepHome()
    {
        // 小降看主 + 亚盘从半球回撤到平手附近 → 降权出精选
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -0.5m,
            -0.10m, 40m, 30m, null,
            2.10m, 3.40m, 3.50m,
            null, null,
            asianOpen: -0.5m, asianClose: -0.25m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("亚盘冲突", rec.SignalDesc);
        Assert.True(rec.Confidence < RecommendationScoring.PublishConfidence);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void AsianMove_WithinDeepHome_DoesNotDemote()
    {
        // 终盘仍主让 0.75+：回测最强桶，小幅回撤不误杀
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -1m,
            -0.10m, 65m, 20m, null,
            1.85m, 3.40m, 4.20m,
            null, null,
            asianOpen: -1.00m, asianClose: -0.75m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(3, rec.SignalLevel);
        Assert.DoesNotContain("亚盘冲突", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void LateReverse_DemotesSingleSignalOutOfFeatured()
    {
        // 小降单信号 + 尾盘反抽 → 踢出精选
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -0.5m,
            -0.10m, 40m, 30m, null,
            2.10m, 3.40m, 3.50m,
            null, null,
            lateHomeDelta: 0.10m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("尾盘反抽", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void HomeHotAlone_DoesNotEnterFeatured()
    {
        var rec = Recommend(delta: 0m, home: 65m, away: 20m,
            spfHome: 2.10m, spfDraw: 3.40m, spfAway: 3.50m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("不进精选", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void FormVeto_DemotesZeroPointHome()
    {
        // 小降单信号 L2，近 5 场 0 分则否决；双信号小降 L3 不受影响
        var weak = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), 0m,
            -0.10m, 40m, 30m, null,
            2.10m, 3.40m, 3.50m,
            null, null,
            homeFormPts: 0);

        Assert.Equal('3', weak.RecResult);
        Assert.Equal(1, weak.SignalLevel);
        Assert.Contains("近况极差", weak.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { weak }));

        var strong = RecommendationEngine.Recommend(
            Boards.Jc, null, "002", 2, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -0.25m,
            -0.10m, 65m, 20m, null,
            2.10m, 3.40m, 3.50m,
            null, null,
            homeFormPts: 0);
        Assert.Equal(3, strong.SignalLevel);
        Assert.DoesNotContain("近况极差", strong.SignalDesc);
    }

    [Fact]
    public void BigRise_AgainstDeepHome_IsWatchNotHome()
    {
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -1m,
            0.25m, 40m, 30m, null,
            1.70m, 3.60m, 4.50m,
            null, null,
            asianOpen: -1m, asianClose: -1m);

        Assert.Null(rec.RecResult);
        Assert.Contains("观望", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void HomeReceivePlusOne_DoesNotChaseTwoGoalScores()
    {
        // 竞彩主受让 +1 时，胜平负若看主，比分应是小胜，不能把 2:0/3:1 抬成首选
        var rec = Recommend(delta: -0.10m, home: 65m, away: 20m,
            spfHome: 2.35m, spfDraw: 3.15m, spfAway: 2.52m, handicap: 1m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal("1:0", rec.ScoreCandidates[0]);
        Assert.DoesNotContain("3:1", rec.ScoreCandidates);
    }

    [Fact]
    public void HandicapFromSpf_HomeWinMinusOne_IsHandicapDraw()
    {
        // 主胜按 1:0 折让 -1 → 让平，避免 2:0 系统性偏让胜
        Assert.Equal('1', RecommendationSelection.HandicapFromSpf('3', -1m));
        Assert.Equal('3', RecommendationSelection.HandicapFromSpf('3', -0.5m));
        Assert.Equal('0', RecommendationSelection.HandicapFromSpf('0', -1m));
        // 主受让 +1：主胜 1:0 是让胜；客胜 0:1 是让平，不是让负
        Assert.Equal('3', RecommendationSelection.HandicapFromSpf('3', 1m));
        Assert.Equal('1', RecommendationSelection.HandicapFromSpf('0', 1m));
    }

    [Fact]
    public void CoverLeg_PlusOne_UsesTheHandicapPrice()
    {
        // 让胜 1.50 也进。客胜按 0:1 折，+1 后是让平，用让平价。
        var low = RecommendationSelection.JcCoverTicket.TryLeg('3', 1m, 1.50m, 4.40m, 4.08m, 59m);
        Assert.NotNull(low);
        Assert.Equal(1.50m, low.Value.Odds);
        var drawLeg = RecommendationSelection.JcCoverTicket.TryLeg('0', 1m, 1.50m, 4.40m, 4.08m, 60m);
        Assert.NotNull(drawLeg);
        Assert.Equal('1', drawLeg.Value.Side);
        Assert.Equal(4.40m, drawLeg.Value.Odds);
        Assert.Equal("让平", drawLeg.Value.Option);

        var leg = RecommendationSelection.JcCoverTicket.TryLeg('3', 1m, 2.30m, 3.40m, 2.60m, 59m);
        Assert.NotNull(leg);
        Assert.Equal('3', leg.Value.Side);
        Assert.Equal(2.30m, leg.Value.Odds);
        Assert.Equal("让胜", leg.Value.Option);
        Assert.Null(RecommendationSelection.JcCoverTicket.TryLeg('3', 1m, 2.30m, 3.40m, 2.60m, 32m));
    }

    [Fact]
    public void CoverTicket_Chances_FourLegs()
    {
        // 4 场 2串1+3串1+4串1，11 注 22 元。每场 3.00、各一半机会：
        // 中 2 场奖金 18（不回本），中 3 场 108，全中 486。
        var legs = Enumerable.Repeat((3.00m, 0.5m), 4).ToList();
        var c = RecommendationSelection.JcCoverTicket.Chances(legs);
        Assert.Equal(11m / 16m, c.AnyPrize);
        Assert.Equal(5m / 16m, c.BackStake);
        Assert.Equal(1m / 16m, c.AllHit);
    }

    [Fact]
    public void CoverTicket_LegProbability_FromHandicapPrices()
    {
        var p = RecommendationSelection.JcCoverTicket.LegProbability('3', 1.50m, 4.40m, 4.08m);
        Assert.NotNull(p);
        Assert.InRange(p!.Value, 0.58m, 0.64m);
    }

    [Fact]
    public void ExtremeHeatWithoutOddsMove_DoesNotChase()
    {
        var rec = Recommend(delta: 0m, home: 90m, away: 5m, spfHome: 1.45m, spfDraw: 4.00m, spfAway: 6.00m);

        Assert.Null(rec.RecResult);
        Assert.Contains("不追热", rec.SignalDesc);
        Assert.False(RecommendationScoring.CountsInHitRate(rec.RecResult, false, rec.Confidence));
    }

    [Fact]
    public void BeijingSingle_DoesNotPadToFifteen()
    {
        var weak = Enumerable.Range(1, 20).Select(i => new MatchRecommendation
        {
            Board = Boards.Bd,
            MatchNo = i.ToString(),
            RecResult = '0',
            SignalLevel = 1,
            Confidence = 41.7m
        });
        var strong = new MatchRecommendation
        {
            Board = Boards.Bd,
            MatchNo = "99",
            RecResult = '3',
            SignalLevel = 3,
            Confidence = 85.7m,
            HitProbability = 70m,
            RecHandicap = '3',
            HandicapProbability = 70m
        };

        var shown = RecommendationSelection.ForDisplay(Boards.Bd, weak.Append(strong).ToList());

        Assert.Single(shown);
        Assert.Equal("99", shown[0].MatchNo);
    }

    [Fact]
    public void Jingcai_KeepsBestSix_AllFeaturedWithMarkets()
    {
        var recs = Enumerable.Range(1, 10).Select(i => new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = i.ToString("000"),
            RecResult = '3',
            SignalLevel = 3,
            Confidence = 90 - i,
            HitProbability = 70 - i,
            Handicap = -1,
            ScoreCandidates = new List<string> { "2:0", "1:0" },
            GoalCandidates = new List<int> { 2, 1 }
        }).ToList();

        var shown = RecommendationSelection.ForDisplay(Boards.Jc, recs);

        Assert.Equal(RecommendationSelection.PublishLimit, shown.Count);
        Assert.All(shown, x =>
        {
            Assert.True(x.IsFeatured);
            Assert.False(x.PublishMarkets);
            Assert.Empty(x.ScoreCandidates);
            Assert.Empty(x.GoalCandidates);
            Assert.Equal(new[] { "2:0", "1:0" }, x.RefScores);
            Assert.Equal(new[] { 2, 1 }, x.RefGoals);
            Assert.Null(x.HalfFullPick);
        });
        Assert.Null(shown[0].RecHandicap);
        // 按命中率排序后取前 8，再按官方场次号展示 → 001..008
        Assert.Equal("001", shown[0].MatchNo);
        Assert.Equal("008", shown[^1].MatchNo);
    }

    [Fact]
    public void Jingcai_RanksByHitProbability_NotFakeConfidence()
    {
        var lowP = Rec(Boards.Jc, "001", 3, 90m);
        lowP.HitProbability = 53m;
        var highP = Rec(Boards.Jc, "002", 2, 61m);
        highP.HitProbability = 72m;
        var shown = RecommendationSelection.ForDisplay(Boards.Jc, new[] { lowP, highP });
        // 展示按场次号；强推荐集合应含 002，且若只取 1 场应优先高命中率
        var picked = new[] { lowP, highP }
            .Where(RecommendationSelection.QualifiesForFeatured)
            .OrderByDescending(x => x.HitProbability ?? 0)
            .Take(1)
            .Single();
        Assert.Equal("002", picked.MatchNo);
        Assert.Equal(1, shown.Count);
        Assert.Equal("002", shown[0].MatchNo);
    }

    [Fact]
    public void Jingcai_DropsLowHitProbability()
    {
        var weak = Rec(Boards.Jc, "001", 3, 85m);
        weak.HitProbability = 54.9m;
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { weak }));
        weak.HitProbability = 55m;
        Assert.Single(RecommendationSelection.ForDisplay(Boards.Jc, new[] { weak }));
    }

    [Fact]
    public void EmpiricalGate_DemotesLevel3_WhenBandIsUnder55()
    {
        var rec = new MatchRecommendation
        {
            RecResult = '3',
            SignalLevel = 3,
            Confidence = 85.7m,
            SignalDesc = "双信号"
        };
        var buckets = new Dictionary<(char, string), (int Hits, int Total)>
        {
            [('3', "2to3")] = (10, 20)
        };
        HitProbabilityService.Stamp(
            rec,
            new HitProbabilityService.Snapshot(HitProbabilityModel.DevigOnly, buckets, true),
            2.10m, 3.40m, 3.50m, null, null);
        Assert.Equal(1, rec.SignalLevel);
        Assert.True(rec.Confidence < RecommendationScoring.PublishConfidence);
        Assert.Contains("降权", rec.SignalDesc);
    }

    [Fact]
    public void Jingcai_DoesNotPadWhenFewerThanLimit()
    {
        var recs = Enumerable.Range(1, 3).Select(i => new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = i.ToString("000"),
            RecResult = '3',
            SignalLevel = 2,
            Confidence = 70,
            HitProbability = 60
        }).ToList();

        var shown = RecommendationSelection.ForDisplay(Boards.Jc, recs);

        Assert.Equal(3, shown.Count);
    }

    [Fact]
    public void Beijing_CapsAtEleven_KeepsScores()
    {
        var many = Enumerable.Range(1, 20).Select(i => new MatchRecommendation
        {
            Board = Boards.Bd,
            MatchNo = i.ToString(),
            RecResult = '3',
            SignalLevel = 2,
            Confidence = 70,
            HitProbability = 60m,
            RecHandicap = '3',
            HandicapProbability = 60m,
            ScoreCandidates = new List<string> { "1:0" }
        }).ToList();

        var shown = RecommendationSelection.ForDisplay(Boards.Bd, many);
        Assert.Equal(RecommendationSelection.BdPublishLimit, shown.Count);
        Assert.Equal(Enumerable.Range(1, RecommendationSelection.BdPublishLimit).Select(i => i.ToString()),
            shown.Select(x => x.MatchNo));
        Assert.All(shown, x =>
        {
            Assert.Empty(x.ScoreCandidates);
            Assert.False(x.PublishMarkets);
            // 北单只卖让球胜平负，要买的就是让球选项，不能清掉。
            Assert.NotNull(x.RecHandicap);
        });
        Assert.Equal(RecommendationSelection.BdStrongLimit, shown.Count(x => x.IsFeatured));
        Assert.All(shown.Where(x => x.IsFeatured), x => Assert.True(int.Parse(x.MatchNo!) <= RecommendationSelection.BdStrongLimit));
        Assert.All(shown.Where(x => !x.IsFeatured), x => Assert.True(int.Parse(x.MatchNo!) > RecommendationSelection.BdStrongLimit));

        var few = RecommendationSelection.ForDisplay(Boards.Bd, many.Take(5).ToList());
        Assert.Equal(5, few.Count);
        Assert.All(few, x => Assert.True(x.IsFeatured));
    }

    [Fact]
    public void Beijing_StrongMarksTopByHitProbability()
    {
        var weak = Enumerable.Range(1, 10).Select(i =>
        {
            var r = Rec(Boards.Bd, i.ToString(), 2, 60m + i);
            r.HandicapProbability = 55m + i;
            return r;
        });
        var strong = Enumerable.Range(100, 5).Select(i =>
        {
            var r = Rec(Boards.Bd, i.ToString(), 3, 80m);
            r.HandicapProbability = 80m;
            return r;
        });
        var shown = RecommendationSelection.ForDisplay(Boards.Bd, weak.Concat(strong).ToList());
        Assert.Equal(RecommendationSelection.BdPublishLimit, shown.Count);
        Assert.Equal(RecommendationSelection.BdStrongLimit, shown.Count(x => x.IsFeatured));
        Assert.All(shown.Where(x => x.HandicapProbability >= 80m), x => Assert.True(x.IsFeatured));
    }

    [Fact]
    public void Jc_And_Bd_Display_InOfficialMatchOrder()
    {
        var jc = new[]
        {
            Rec(Boards.Jc, "周六010", 3, 70m),
            Rec(Boards.Jc, "周五003", 3, 90m),
            Rec(Boards.Jc, "周五001", 2, 61m),
            Rec(Boards.Jc, "周日002", 2, 65m),
        };
        Assert.Equal(new[] { "周五001", "周五003", "周六010", "周日002" },
            RecommendationSelection.ForDisplay(Boards.Jc, jc).Select(x => x.MatchNo));

        var bd = new[]
        {
            Rec(Boards.Bd, "212", 2, 70m),
            Rec(Boards.Bd, "44", 3, 80m),
            Rec(Boards.Bd, "1", 2, 61m),
            Rec(Boards.Bd, "99", 2, 65m),
        };
        Assert.Equal(new[] { "1", "44", "99", "212" },
            RecommendationSelection.ForDisplay(Boards.Bd, bd).Select(x => x.MatchNo));
    }

    private static MatchRecommendation Rec(string board, string matchNo, int level, decimal conf) => new()
    {
        Board = board,
        MatchNo = matchNo,
        RecResult = '3',
        SignalLevel = level,
        Confidence = conf,
        HitProbability = 60m,
        RecHandicap = board == Boards.Bd ? '3' : null,
        HandicapProbability = board == Boards.Bd ? 60m : null,
        ScoreCandidates = new List<string> { "1:0" }
    };

    [Fact]
    public void ShengFuCai_KeepsMatchesWithoutDirection()
    {
        var recs = new List<MatchRecommendation>
        {
            new() { Board = Boards.Sfc, MatchNo = "1", RecResult = null, SignalLevel = 0 },
            new() { Board = Boards.Sfc, MatchNo = "2", RecResult = '3', SignalLevel = 2, Confidence = 61.3m }
        };

        var shown = RecommendationSelection.ForDisplay(Boards.Sfc, recs);

        Assert.Equal(2, shown.Count);
    }

    [Theory]
    [InlineData(true, null, '1', null)]
    [InlineData(false, '1', '1', true)]
    [InlineData(false, '3', '1', false)]
    public void DirectionalHit_IgnoresCoverAll(bool cover, char? pick, char actual, bool? expected)
    {
        Assert.Equal(expected, RecommendationScoring.DirectionalHit(pick, cover, actual));
    }

    [Fact]
    public void HitRate_RequiresDirectionAndConfidence()
    {
        Assert.False(RecommendationScoring.CountsInHitRate('1', true, null));
        Assert.False(RecommendationScoring.CountsInHitRate('0', false, 41.7m));
        Assert.True(RecommendationScoring.CountsInHitRate('3', false, 60m));
    }

    [Fact]
    public void OpenGame_SoftDrawAlone_DoesNotPublish()
    {
        var rec = Recommend(delta: 0.01m, home: 40m, away: 35m,
            spfHome: 2.45m, spfDraw: 2.95m, spfAway: 2.90m, handicap: 0m);

        Assert.NotEqual('1', rec.RecResult);
    }

    [Fact]
    public void DrawShapeOnly_WithoutMarket_DoesNotEnterFeatured()
    {
        // 仅去水最高/平水位，无平赔压、无必发平 → 不进精选（可能欧赔兜底出平，也抬不进）
        var rec = Recommend(delta: 0.01m, home: 40m, away: 35m,
            spfHome: 3.20m, spfDraw: 2.95m, spfAway: 3.10m, handicap: 0m);

        Assert.True(rec.IsOddsFallback || rec.SignalLevel < 2
                    || rec.Confidence < RecommendationScoring.PublishConfidence);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void DrawOddsPressAlone_DoesNotPublish()
    {
        var rec = Recommend(delta: 0.01m, home: 40m, away: 35m,
            spfHome: 2.50m, spfDraw: 3.25m, spfAway: 2.80m, handicap: 0m,
            drawDelta: -0.10m);

        Assert.NotEqual('1', rec.RecResult);
    }

    [Fact]
    public void DrawWithMarket_IsReferenceNotFeatured()
    {
        // 有市场动作可给平方向，但回测~37%，不进精选
        var rec = Recommend(delta: 0m, home: 30m, away: 28m,
            spfHome: 2.60m, spfDraw: 3.15m, spfAway: 2.70m, handicap: 0m,
            drawDelta: -0.09m, ratioDraw: 42m);

        Assert.Equal('1', rec.RecResult);
        Assert.Equal(1, rec.SignalLevel);
        Assert.True(rec.Confidence < RecommendationScoring.PublishConfidence);
        Assert.Contains("不进精选", rec.SignalDesc);
        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { rec }));
    }

    [Fact]
    public void OneSidedFavorite_DoesNotPublishDraw()
    {
        var rec = Recommend(delta: 0m, home: 40m, away: 30m,
            spfHome: 1.55m, spfDraw: 3.80m, spfAway: 5.50m, handicap: -0.75m);

        Assert.NotEqual('1', rec.RecResult);
    }

    [Fact]
    public void DeepHandicap_DoesNotPublishDraw()
    {
        var rec = Recommend(delta: 0m, home: 45m, away: 30m,
            spfHome: 2.20m, spfDraw: 3.20m, spfAway: 3.30m, handicap: -1m);

        Assert.NotEqual('1', rec.RecResult);
    }

    [Fact]
    public void HomeDualSignal_StillBeatsDrawShape()
    {
        var rec = Recommend(delta: -0.10m, home: 65m, away: 20m,
            spfHome: 2.20m, spfDraw: 3.20m, spfAway: 3.40m, handicap: 0m);

        Assert.Equal('3', rec.RecResult);
        Assert.Equal(85.7m, rec.Confidence);
    }

    [Fact]
    public void SerieA_RaisesGoalsAndScores_VsSameSignalElsewhere()
    {
        var epl = Recommend(delta: -0.10m, home: 65m, away: 20m,
            spfHome: 1.95m, spfDraw: 3.40m, spfAway: 3.80m, handicap: -0.5m,
            league: "英超", totalLine: 2.5m);
        var serieA = Recommend(delta: -0.10m, home: 65m, away: 20m,
            spfHome: 1.95m, spfDraw: 3.40m, spfAway: 3.80m, handicap: -0.5m,
            league: "意甲", totalLine: 2.5m);

        Assert.Equal('3', serieA.RecResult);
        Assert.Contains("意甲偏大球", serieA.SignalDesc);
        Assert.True(serieA.GoalCandidates.Min() >= 3);
        Assert.True(serieA.GoalCandidates.Average() >= epl.GoalCandidates.Average());
        Assert.All(serieA.ScoreCandidates, s =>
        {
            var p = s.Split(':');
            Assert.True(int.Parse(p[0]) + int.Parse(p[1]) >= 3, s);
        });
    }

    [Theory]
    [InlineData(Boards.Jc, 2, "2串1")]
    [InlineData(Boards.Jc, 3, "2串1 + 3串1")]
    [InlineData(Boards.Jc, 4, "够几场出几场")]
    [InlineData(Boards.Bd, 4, null)]
    public void Jingcai_PassTip_SuggestsLowParlay(string board, int n, string? contains)
    {
        var tip = RecommendationSelection.PassTip(board, n);
        if (contains is null) Assert.Null(tip);
        else Assert.Contains(contains, tip);
    }

    [Theory]
    [InlineData("2026-10-31 21:00", "2026-10-31 22:30", true)]
    [InlineData("2026-10-31 21:58", "2026-10-31 22:30", true)]
    [InlineData("2026-10-31 22:40", "2026-10-31 23:10", true)]
    [InlineData("2026-10-31 22:56", "2026-10-31 23:10", false)]
    [InlineData("2026-10-30 21:55", "2026-10-30 22:30", true)]
    [InlineData("2026-10-30 21:56", "2026-10-30 22:30", false)]
    [InlineData("2026-10-31 00:11", "2026-10-31 03:00", false)]
    [InlineData("2026-10-31 00:11", "2026-10-31 15:00", true)]
    [InlineData("2026-10-31 08:30", "2026-10-31 08:45", false)]
    [InlineData("2026-10-31 08:30", "2026-10-31 10:00", true)]
    public void JcSaleHours_Stops_Overnight_Kickoffs(string now, string kickoff, bool canBuy)
    {
        Assert.Equal(canBuy, RecommendationSelection.JcSaleHours.CanStillBuy(
            DateTime.Parse(now), DateTime.Parse(kickoff)));
    }

    [Theory]
        [InlineData("周六017", "2026-10-31 17:56", true)]
        [InlineData("周日001", "2026-10-31 17:56", false)]
        [InlineData("周日001", "2026-10-31 22:10", false)]
        [InlineData("周日001", "2026-10-31 23:00", true)]
        [InlineData("周六020", "2026-11-01 08:30", true)]
        [InlineData("周日001", "2026-11-01 08:30", true)]
        [InlineData("周日001", "2026-11-01 09:00", true)]
        [InlineData("周六017", "2026-11-01 09:00", false)]
    public void JcSaleHours_Only_Todays_Issue(string matchNo, string now, bool today)
    {
        Assert.Equal(today, RecommendationSelection.JcSaleHours.IsTodaysIssue(matchNo, DateTime.Parse(now)));
    }

    [Fact]
    public void CoverTicket_Is_Eight_Leg_74_Yuan()
    {
        var ticket = RecommendationSelection.JcCoverTicket.AllHit(Enumerable.Repeat(2.00m, 8).ToArray());
        var one = RecommendationSelection.JcCoverTicket.OneMiss(Enumerable.Repeat(2.00m, 8).ToArray());
        var two = RecommendationSelection.JcCoverTicket.TwoMiss(Enumerable.Repeat(2.00m, 8).ToArray());

        Assert.Equal(74m, RecommendationSelection.JcCoverTicket.Stake);
        Assert.Equal("6串1#7串1#8串1", RecommendationSelection.JcCoverTicket.Pass);
        Assert.Equal("4串1#5串1#6串1", RecommendationSelection.JcCoverTicket.PassFor(6));
        Assert.Equal(22, RecommendationSelection.JcCoverTicket.BetCountFor(6));
        Assert.Equal(44m, RecommendationSelection.JcCoverTicket.StakeFor(6));
        Assert.Equal(992m, RecommendationSelection.JcCoverTicket.AllHit(Enumerable.Repeat(2.00m, 6).ToArray()));
        Assert.Equal(6144m, ticket);
        Assert.Equal((1152m, 1152m), one);
        Assert.Equal((128m, 128m), two);
    }

    [Fact]
    public void CoverTicket_Picks_Best_Eight()
    {
        var legs = new (decimal Odds, decimal Hit)[]
        {
            (1.40m, 70m), (2.10m, 50m), (4.80m, 45m),
            (2.30m, 40m), (2.40m, 39m), (2.50m, 38m), (2.60m, 37m),
            (2.70m, 36m), (2.80m, 35m), (3.00m, 34m), (3.20m, 33m)
        };
        var picked = RecommendationSelection.JcCoverTicket.Pick(legs, x => x.Odds, x => (decimal?)x.Hit);
        var odds = picked.Select(x => x.Odds).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { 1.40m, 2.10m, 2.30m, 2.40m, 2.50m, 2.60m, 2.70m, 4.80m }, odds);
    }

    [Theory]
    [InlineData(2, "2串1")]
    [InlineData(3, "2串1#3串1")]
    [InlineData(5, "2串1")]
    public void Jingcai_PassTypeValue_ForForm(int n, string expected)
    {
        Assert.Equal(expected, RecommendationSelection.PassTypeValue(Boards.Jc, n));
    }

    private static MatchRecommendation Recommend(
        decimal? delta, decimal? home, decimal? away,
        decimal? spfHome = null, decimal? spfDraw = null, decimal? spfAway = null,
        decimal? handicap = null, decimal? drawDelta = null, decimal? ratioDraw = null,
        string league = "英超", decimal? totalLine = null)
        => RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, league, "主队", "客队",
            DateTime.Today.AddHours(20), handicap,
            delta, home, away, totalLine,
            spfHome, spfDraw, spfAway,
            drawDelta, ratioDraw);
}
