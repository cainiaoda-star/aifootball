using AiFootBall.Data;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 大奶战绩报表的口径测试。
/// 关键点：ROI 必须是「命中赔率合计 ÷ 注数」（平注 1 元），不是「命中赔率占全部赔率」——
/// 后者算出来永远小于 1，看不出到底赚没赚；空样本不能除零。
/// </summary>
public class MilkStatsTests
{
    [Fact]
    public void Roi_is_return_per_bet_not_share_of_all_odds()
    {
        // 10 注、每注 1 元，命中 2 注，赔率分别 5.0 / 8.0 → 回报 13 元 / 投入 10 元 = 1.3
        var s = new MilkPlayStat { Total = 10, Hit = 2, TotalOdds = 60m, HitOdds = 13m };
        Assert.Equal(1.3m, s.Roi);
        Assert.Equal(20m, s.HitRatePct);
    }

    [Fact]
    public void Overconfidence_is_model_probability_minus_actual_hit_rate()
    {
        // 模型平均给 8%，实际只中 5% → 高估 3 个点
        var s = new MilkPlayStat { Total = 100, Hit = 5, AvgProbability = 8m };
        Assert.Equal(3m, s.OverconfidencePct);

        // 模型低估时是负数
        var under = new MilkPlayStat { Total = 100, Hit = 12, AvgProbability = 9m };
        Assert.Equal(-3m, under.OverconfidencePct);
    }

    [Fact]
    public void Empty_sample_does_not_divide_by_zero()
    {
        var s = new MilkPlayStat();
        Assert.Equal(0m, s.Roi);
        Assert.Equal(0m, s.HitRatePct);
        Assert.Equal(0m, s.OverconfidencePct);
    }

    [Fact]
    public void Option_name_uses_short_form_for_display()
    {
        Assert.Equal("负胜", new MilkOptionStat { PlayType = "BQ", OptionCode = "0-3" }.OptionName);
        Assert.Equal("胜负", new MilkOptionStat { PlayType = "BQ", OptionCode = "3-0" }.OptionName);
        Assert.Equal("3:1", new MilkOptionStat { PlayType = "BF", OptionCode = "3:1" }.OptionName);
        Assert.Equal("7+球", new MilkOptionStat { PlayType = "JQ", OptionCode = "7" }.OptionName);
    }
}
