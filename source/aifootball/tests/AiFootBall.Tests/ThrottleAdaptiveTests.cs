using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 全站节流器的自适应退避（2026-11-10 补，此前零测试）。
///
/// 为什么现在才写：拆 water-sample 时实测「30 场赔率采样用了 91 秒」，
/// 折合每场 3 秒，而基准间隔是 900ms。差三倍，却在任何页面上都看不出来——
/// OddsHttp 的五个计数器和 CurrentIntervalMs 从来没有被报出来过。
/// 于是只能靠总耗时除以场数反推，而反推只能给出「3 秒」，给不出「为什么 3 秒」。
///
/// 这组测试钉住的是那个「为什么」：429 只翻倍、成功要 20 次才降 1/8。
/// 所以从上限 4000ms 回到 900ms 需要 12 步、240 次成功请求。
/// water-sample 一轮 30 场、一小时 180 场，也就是被打上去之后一个多小时才回得来。
/// 换句话说：一次限流惩罚的代价不是「慢一下」，是「接下来一小时都慢」。
/// 这条性质不写下来，下次看到 91 秒的人只会以为采集本身慢，去调 Limit 或并发，
/// 而真正的开关在别处。
/// </summary>
public class ThrottleAdaptiveTests
{
    [Fact]
    public void Initial_Interval_Is_Base()
    {
        var t = new RequestThrottle(baseIntervalMs: 900, maxIntervalMs: 4000);
        Assert.Equal(900, t.CurrentIntervalMs);
        Assert.Equal(900, t.BaseIntervalMs);
        Assert.Equal(4000, t.MaxIntervalMs);
    }

    [Fact]
    public void RateLimited_Doubles_Until_Max()
    {
        var t = new RequestThrottle(baseIntervalMs: 900, maxIntervalMs: 4000);

        t.OnRateLimited(5);
        Assert.Equal(1800, t.CurrentIntervalMs);
        t.OnRateLimited(5);
        Assert.Equal(3600, t.CurrentIntervalMs);
        t.OnRateLimited(5);
        Assert.Equal(4000, t.CurrentIntervalMs);   // 封顶，不是 7200

        // 继续被限流也不会超过上限
        for (var i = 0; i < 10; i++) t.OnRateLimited(5);
        Assert.Equal(4000, t.CurrentIntervalMs);
    }

    [Fact]
    public void Success_Only_Eases_Every_20_Requests()
    {
        var t = new RequestThrottle(baseIntervalMs: 900, maxIntervalMs: 4000);
        t.OnRateLimited(5);                        // 1800

        // 前 19 次成功不动。这是「恢复慢」的第一层原因。
        for (var i = 0; i < 19; i++) t.OnSuccess();
        Assert.Equal(1800, t.CurrentIntervalMs);

        t.OnSuccess();                             // 第 20 次
        Assert.Equal(1800 - 1800 / 8, t.CurrentIntervalMs);   // 1575
    }

    [Fact]
    public void Recovery_From_Max_To_Base_Takes_240_Requests()
    {
        var t = new RequestThrottle(baseIntervalMs: 900, maxIntervalMs: 4000);
        for (var i = 0; i < 5; i++) t.OnRateLimited(5);
        Assert.Equal(4000, t.CurrentIntervalMs);

        // 每 20 次成功降 1/8，从 4000 降到 900 要 12 步 = 240 次成功。
        // 239 次时还没回到基线——这个数字就是「一次限流惩罚值一小时」的算术依据。
        for (var i = 0; i < 239; i++) t.OnSuccess();
        Assert.True(t.CurrentIntervalMs > 900,
            $"239 次成功就回到基线说明退避曲线变了（现在 {t.CurrentIntervalMs}ms），"
            + "重新量一次每场耗时再改这里的结论");

        t.OnSuccess();
        Assert.Equal(900, t.CurrentIntervalMs);
    }

    [Fact]
    public void Success_Never_Goes_Below_Base()
    {
        var t = new RequestThrottle(baseIntervalMs: 900, maxIntervalMs: 4000);
        for (var i = 0; i < 2000; i++) t.OnSuccess();
        Assert.Equal(900, t.CurrentIntervalMs);
    }
}
