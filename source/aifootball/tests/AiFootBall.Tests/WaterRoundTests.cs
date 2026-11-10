using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// water-sample 的轮次分配（2026-11-10 补）。
///
/// 背景：把 water-sample 改成只采欧赔（省下三分之二的请求）之后，
/// 亚盘/大小球就没人更新了 —— 不是变慢，是完全停。因为 water-sample 每轮都写
/// `FbMatch.OddsSampledAt`，而 `odds-sample` / `odds-sample-late` 正是靠这个戳
/// 判断「刚采过就跳过」。10 分钟一次的高频采样把它们全压住了。
/// 实测后果：16:41 两个作业都报「无待采样赛事」，而 6 小时内实际有 27 场。
///
/// 修法是在源头补：每 3 轮采一次三页全量。这里钉住的就是「哪几轮是全量」。
/// 这行极容易写反，而写反不会报任何错 —— 页面全绿，只有盘口判据在悄悄用旧盘。
/// 所以宁可为一个取模表达式写五条测试。
/// </summary>
public class WaterRoundTests
{
    [Fact]
    public void First_Round_Is_Full()
    {
        // 重启后第一轮就要全量：否则最多要等 30 分钟才轮到，
        // 这段时间 AH/OU 一直停在重启前的值。
        Assert.False(JobScheduler.WaterRoundIsEuroOnly(1));
    }

    [Fact]
    public void Second_And_Third_Round_Are_Euro_Only()
    {
        Assert.True(JobScheduler.WaterRoundIsEuroOnly(2));
        Assert.True(JobScheduler.WaterRoundIsEuroOnly(3));
    }

    [Fact]
    public void Every_Third_Round_Starting_From_First_Is_Full()
    {
        for (var round = 1; round <= 30; round++)
        {
            var expectedFull = (round - 1) % 3 == 0;   // 1, 4, 7, 10 …
            Assert.Equal(!expectedFull, JobScheduler.WaterRoundIsEuroOnly(round));
        }
    }

    [Fact]
    public void Full_Rounds_Are_Exactly_One_In_Three()
    {
        // 10 分钟一轮，所以全量间隔正好 30 分钟；这个比例就是通道占用的比例。
        var full = 0;
        for (var round = 1; round <= 30; round++)
            if (!JobScheduler.WaterRoundIsEuroOnly(round)) full++;

        Assert.Equal(10, full);
    }

    [Fact]
    public void Consecutive_Full_Rounds_Never_Happen()
    {
        // 两轮全量挨在一起 = 一分钟内把 3 个页面采两遍，纯浪费且会互相压制。
        for (var round = 1; round < 30; round++)
        {
            var a = JobScheduler.WaterRoundIsEuroOnly(round);
            var b = JobScheduler.WaterRoundIsEuroOnly(round + 1);
            Assert.False(!a && !b, $"第 {round} 与 {round + 1} 轮都是全量");
        }
    }
}
