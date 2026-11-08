using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 临场转向哨兵的方向性（2026-11-08 重写 Judge 时补的回归测试）。
///
/// 这批测试锁死的是**一个具体的冤枉**：2026-11-08 江原FC vs 全北现代 半全场「负负」，
/// 系统连报 6 条告警——「让胜 5.40→6.10 拉升」「客胜盈亏 -99」「客胜占比 63%」，
/// 三条信号全部指向客队走强，而这一注押的正是客胜。全是利好，被当成风险喊了出来。
/// 用户当场质问：「你看好这一注？」——报警喊反方向比不喊更害人。
/// </summary>
public sealed class TurnGuardTests
{
    private static MilkPickRow Pick(string playType, string optionCode) => new()
    {
        PickId = 1,
        MatchId = 100,
        MatchNo = "周日005",
        LeagueName = "K1",
        HomeTeam = "江原FC",
        AwayTeam = "全北现代",
        MatchTime = new DateTime(2026, 11, 8, 15, 30, 0),
        PlayType = playType,
        OptionCode = optionCode,
        OptionName = "测试",
        Odds = 5.30m
    };

    /// <summary>客队走强的诊断：净胜球下修 + 客队深档整批降价。</summary>
    private static ScoreDiagRow DiagAwayStrong() => new()
    {
        MatchId = 100,
        ExpGdOpen = -0.30m,
        ExpGd = -0.45m,
        ExpGdMove = -0.15m,
        DeepNegDrop = 5,
        TailTag = "客队深档整批降价"
    };

    [Fact]
    public void AwayPick_MoneyFlowingToAway_IsNotAnAlert()
    {
        // 江原FC vs 全北现代 半全场 负负 = 客胜方向。客队资金涌入是印证，不是风险。
        var pick = Pick(JcPlayTypes.Bq, "0-0");
        var dir = TurnGuard.PickDirection(pick.PlayType, pick.OptionCode);
        Assert.Equal('0', dir);

        var alert = TurnGuard.Judge(
            pick, dir, DiagAwayStrong(),
            rangSheng: (5.40m, 6.10m),
            awayMoney: (-99m, 63m),
            divergence: null);

        Assert.Null(alert);
    }

    [Fact]
    public void HomePick_SameSignals_IsHighAlert()
    {
        // 反过来，同样一批信号对「胜胜」（主胜方向）就是坏消息，而且够高危。
        var pick = Pick(JcPlayTypes.Bq, "3-3");
        var dir = TurnGuard.PickDirection(pick.PlayType, pick.OptionCode);
        Assert.Equal('3', dir);

        var alert = TurnGuard.Judge(
            pick, dir, DiagAwayStrong(),
            rangSheng: (2.00m, 2.28m),
            awayMoney: (-73m, 82m),
            divergence: null);

        Assert.NotNull(alert);
        Assert.Equal("high", alert!.Severity);
        // 只记「对注不利」的信号，且按名字排序——这是去重键，必须稳定。
        Assert.Equal("awayPnl,awayRatio,deepDrop,gdShrink,rangSheng", alert.SignalCode);
        Assert.Contains("这一注指主胜", alert.Reason);
    }

    [Fact]
    public void TotalGoalsPlay_HasNoDirection_NeverAlerts()
    {
        // 总进球没有胜负方向。「客胜必发占比 76%」跟「总进球是不是 2 个」毫无关系。
        // 2026-11-08 一天里，水晶宫vs利物浦 / 热那亚vsAC米兰 / 朗斯vs马赛 这三注
        // 各被连报十几次，全是这条噪音。
        var pick = Pick(JcPlayTypes.Jq, "2");
        Assert.Null(TurnGuard.PickDirection(pick.PlayType, pick.OptionCode));

        var alert = TurnGuard.Judge(
            pick, null, DiagAwayStrong(),
            rangSheng: (2.00m, 2.28m),
            awayMoney: (-90m, 76m),
            divergence: null);

        Assert.Null(alert);
    }

    [Fact]
    public void DrawPick_NeverAlerts()
    {
        // 平平/胜平/平负这类注，库里没有任何「什么信号会推翻平局」的实测证据，硬报就是拿猜测冒充告警。
        var pick = Pick(JcPlayTypes.Bq, "1-1");
        var dir = TurnGuard.PickDirection(pick.PlayType, pick.OptionCode);
        Assert.Equal('1', dir);

        var alert = TurnGuard.Judge(
            pick, dir, DiagAwayStrong(),
            rangSheng: (2.00m, 2.28m),
            awayMoney: (-90m, 76m),
            divergence: null);

        Assert.Null(alert);
    }

    [Fact]
    public void AwayPick_WhenAwayPriceIsFakeHot_IsHigh()
    {
        // 例外：客胜方向唯一真正的坏消息是「客胜这个价本身是散户砸出来的、必发不认」。
        // 2026-11-08 英超 诺丁汉森林 3:1 曼城（客胜 1.60→1.43，必发只动 -1.1%）。
        var pick = Pick(JcPlayTypes.Bf, "0:1");
        var dir = TurnGuard.PickDirection(pick.PlayType, pick.OptionCode);
        Assert.Equal('0', dir);

        var divergence = new PriceDivergence.Result
        {
            Away = new PriceDivergence.SideMove(JcPct: 12m, ExPct: 1.5m)
        };
        Assert.True(divergence.AwayFakeHot);

        var alert = TurnGuard.Judge(
            pick, dir, DiagAwayStrong(),
            rangSheng: (2.00m, 2.28m),
            awayMoney: (null, null),
            divergence: divergence);

        Assert.NotNull(alert);
        Assert.Equal("high", alert!.Severity);
        Assert.Equal("fakeHotAway", alert.SignalCode);
        Assert.Contains("竞彩单边砸出来", alert.Reason);
    }

    [Fact]
    public void WeakRatioOnly_SingleSignal_DoesNotAlert()
    {
        // 「客胜必发占比异常」单独一条太薄：强客队场次（利物浦/AC米兰做客）占比 70%+ 是常态。
        // 没有强信号陪着，不报。
        var pick = Pick(JcPlayTypes.Bq, "3-3");
        var diag = new ScoreDiagRow
        {
            MatchId = 100, ExpGdOpen = 0.10m, ExpGd = 0.08m, ExpGdMove = -0.02m, DeepNegDrop = 0
        };

        var alert = TurnGuard.Judge(
            pick, '3', diag,
            rangSheng: (1.80m, 1.82m),
            awayMoney: (null, 76m),
            divergence: null);

        Assert.Null(alert);
    }

    [Fact]
    public void Alert_CarriesStableKey_ForWriteBackToBoard()
    {
        // 回写键必须是 (MatchId, PlayType, OptionCode)，不能是 PickId——
        // 每次重算都是 DELETE + INSERT，PickId 会变，拿它关联的话撤注标记下一轮就丢。
        var pick = Pick(JcPlayTypes.Bq, "3-3");
        var alert = TurnGuard.Judge(pick, '3', DiagAwayStrong(),
            rangSheng: (2.00m, 2.28m), awayMoney: (-73m, 82m), divergence: null);

        Assert.NotNull(alert);
        Assert.Equal(JcPlayTypes.Bq, alert!.PlayType);
        Assert.Equal("3-3", alert.OptionCode);
        Assert.Equal(pick.MatchId, alert.MatchId);
    }

    // ───────── 以下锁死「EV 破线」判据（2026-11-08 加） ─────────
    //
    // 起因：江原FC vs 全北现代 半全场负负 @5.30，基线概率 14.37% → 实证 EV 0.762，
    // 而半全场返奖率是 0.797。也就是说这一注的期望回报比「同玩法随便挑一注」还差。
    // 但市场从头到尾没转向（赔率 5.25→5.30），于是转向哨兵一路报「无告警」。
    // 哨兵只问「变了没有」，从不问「这价到底值不值」——这是判据缺口，不是阈值问题。

    [Fact]
    public void EvBelowFloor_MarksRed_ButMustNotVoid()
    {
        // 江原那注的实数：EV 0.762 < 0.797，且没有任何转向信号、连诊断都取不到。
        //
        // ★ 2026-11-08 晚改语义：这种情况**只标红、不撤注**。
        //   算一下就清楚：5.30 的竞彩价去水后隐含 15.04%，基线给 14.37%，
        //   两者只差 4 个百分点，全是「分档均值 vs 单场概率」的估计误差
        //   （同一档内平平率就能从 12% 摆到 22%）。拿 4% 判「撤注」等于拿噪声杀人。
        //   而且出票闸 MinBaselineEvRatioBq=1.00 已经在源头拦住这种注，这里再撤一次
        //   不增加信息，只会在赔率小幅波动时把榜面搅得天天抖。
        var pick = Pick(JcPlayTypes.Bq, "0-0");
        pick.BaselineEv = 0.762m;
        var dir = TurnGuard.PickDirection(pick.PlayType, pick.OptionCode);
        Assert.Equal('0', dir);

        var alert = TurnGuard.Judge(
            pick, dir, diag: null,
            rangSheng: null,
            awayMoney: null,
            divergence: null);

        Assert.NotNull(alert);
        Assert.Equal("mid", alert!.Severity);            // mid = 只红标，不撤注
        Assert.Equal("evBroken", alert.SignalCode);
        Assert.Contains("0.762", alert.Reason);
    }

    [Fact]
    public void EvAbovePayback_NoSignal_NoAlert()
    {
        // 那不勒斯 负负 @4.40，基线 21.16% → EV 0.931 > 0.797。没有转向信号就不该报。
        var pick = Pick(JcPlayTypes.Bq, "0-0");
        pick.BaselineEv = 0.931m;

        var alert = TurnGuard.Judge(
            pick, '0', diag: null, rangSheng: null, awayMoney: null, divergence: null);

        Assert.Null(alert);
    }

    [Fact]
    public void TotalGoals_EvBroken_StillAlerts()
    {
        // 总进球没有方向，转向类信号一律不报——但 EV 偏低是绝对判据，跟方向无关，照样标红。
        var pick = Pick(JcPlayTypes.Jq, "2");
        pick.BaselineEv = 0.600m;   // 总进球出票下限 0.797×0.85=0.677，0.600 确实破线

        var alert = TurnGuard.Judge(
            pick, pickDir: null, diag: null, rangSheng: null, awayMoney: null, divergence: null);

        Assert.NotNull(alert);
        Assert.Equal("mid", alert!.Severity);
        Assert.Equal("evBroken", alert.SignalCode);
    }

    [Fact]
    public void PickThatPassedIssueGate_MustNotBeVoidedByTheSameGate()
    {
        // ★ 2026-11-08 修的自相矛盾：出票闸与撤注闸门槛不一致，
        //   比分/总进球出票只要 EV ≥ 返奖率×0.85，但哨兵原来拿「返奖率本身」当线。
        //   于是 EV 0.68 的比分注（0.710×0.85=0.6035 ≤ 0.68 < 0.710）
        //   出票放行、下一轮哨兵立刻判 high 撤掉——系统在撤销自己刚出的票。
        //   修法：两边共用 MilkPickEngine 的门槛；出票时过得了的，哨兵不会拿同一条判据撤它。
        var pick = Pick(JcPlayTypes.Bf, "1:1");
        pick.BaselineEv = 0.680m;   // 比分返奖率 0.710，出票下限 0.6035 → 出票闸放行

        Assert.True(pick.BaselineEv >= TurnGuard.BaselineEvFloor(JcPlayTypes.Bf));

        var alert = TurnGuard.Judge(
            pick, pickDir: null, diag: null, rangSheng: null, awayMoney: null, divergence: null);
        Assert.Null(alert);

        // 反过来，真正跌破出票下限的才报（@0.55 < 0.6035）。
        pick.BaselineEv = 0.550m;
        var alert2 = TurnGuard.Judge(
            pick, pickDir: null, diag: null, rangSheng: null, awayMoney: null, divergence: null);
        Assert.NotNull(alert2);
        Assert.Equal("mid", alert2!.Severity);
    }

    [Fact]
    public void DrawPick_EvBroken_StillAlerts()
    {
        // 平局方向同样不报转向信号，但 EV 破线要报。
        var pick = Pick(JcPlayTypes.Bq, "1-1");   // 平平
        pick.BaselineEv = 0.700m;
        Assert.Equal('1', TurnGuard.PickDirection(pick.PlayType, pick.OptionCode));

        var alert = TurnGuard.Judge(
            pick, '1', diag: null, rangSheng: null, awayMoney: null, divergence: null);

        Assert.NotNull(alert);
        Assert.Equal("evBroken", alert!.SignalCode);
    }

    [Fact]
    public void EvBroken_ReasonMustNotClaimMarketTurned()
    {
        // 文案必须说实话：市场没转向，只是这注按基线偏低。
        // 上一轮的事故就是文案与实际行为不符——用户看到「市场转向」会以为有实时情报。
        // 2026-11-08 晚又改一次：既然不撤注了，文案更要说清「只标红」，
        // 否则用户看到红标以为被撤了，反而去手动弃单。
        var pick = Pick(JcPlayTypes.Bq, "0-0");
        pick.BaselineEv = 0.724m;   // 塞尔塔 平胜的实际值

        var alert = TurnGuard.Judge(
            pick, '0', diag: null, rangSheng: null, awayMoney: null, divergence: null);

        Assert.NotNull(alert);
        Assert.Contains("只标红", alert!.Reason);
        Assert.DoesNotContain("市场在临场反向转向", alert.Reason);
    }

    [Fact]
    public void EvBroken_KeepsOtherSignals_AndSaysBoth()
    {
        // EV 破线 + 市场真转向同时出现时，两条原因都要在，severity 仍是 high。
        var pick = Pick(JcPlayTypes.Bq, "3-3");
        pick.BaselineEv = 0.650m;

        var alert = TurnGuard.Judge(
            pick, '3', DiagAwayStrong(),
            rangSheng: (2.00m, 2.28m), awayMoney: (-73m, 82m), divergence: null);

        Assert.NotNull(alert);
        Assert.Equal("high", alert!.Severity);
        Assert.Contains("evBroken", alert.SignalCode);
        Assert.Contains("gdShrink", alert.SignalCode);
        Assert.Contains("EV", alert.Reason);
        Assert.Contains("反向转向", alert.Reason);
    }
}
