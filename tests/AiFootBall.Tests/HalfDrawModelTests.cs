using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 半全场「胜平 3-1 / 负平 0-1」公平价模型（2026-11-08 加）。
///
/// 这几个断言守的是模型**结构**，不是具体数值——数值会随样本刷新而变，
/// 但下面几条一旦坏了，说明实现走了形：
/// ① 没有 SPF 就出不了结果（主客强弱是唯一入口）；
/// ② 方向必须不对称：胜平恒大于负平（实测方向率 20.5% 对 19.0%）；
/// ③ 0:0 不计入任何一档（半场必平，两档都不可能中）；
/// ④ JQ 缺档时必须回落泊松而不是当成 0。
///
/// ⚠ 不要在这里断言「EV 会大于 1」——实测 495 场只有 1 场过 1，这一档是负期望，
/// 模型的价值在于给出公平价，不是找出可买的注。
/// </summary>
public class HalfDrawModelTests
{
    // 一场中等强度对抗的 SPF：主 2.10 / 平 3.40 / 客 3.60
    private static readonly decimal?[] Jq = { 9.0m, 5.2m, 3.6m, 4.5m, 6.5m, 11m, 18m, 22m };

    [Fact]
    public void 缺SPF三项任一则不建模()
    {
        Assert.Null(HalfDrawModel.Estimate(null, 3.4m, 3.6m, Jq, 15m, 15m));
        Assert.Null(HalfDrawModel.Estimate(2.1m, null, 3.6m, Jq, 15m, 15m));
        Assert.Null(HalfDrawModel.Estimate(2.1m, 3.4m, null, Jq, 15m, 15m));
        // 非法价（1.00 及以下）同样不能进模型——除零会算出 ±∞
        Assert.Null(HalfDrawModel.Estimate(1.0m, 3.4m, 3.6m, Jq, 15m, 15m));
    }

    [Fact]
    public void 胜平概率恒大于负平()
    {
        var e = HalfDrawModel.Estimate(2.10m, 3.40m, 3.60m, Jq, 15m, 15m);
        Assert.NotNull(e);
        // 实测方向率：平局里半场主队领先 20.5%、客队领先 19.0%，差约 8%
        Assert.True(e!.ProbWinDraw > e.ProbLoseDraw,
            $"胜平 {e.ProbWinDraw}% 应大于负平 {e.ProbLoseDraw}%");
        Assert.InRange(e.ProbWinDraw / e.ProbLoseDraw, 1.02m, 1.15m);
    }

    [Fact]
    public void 概率量级在一档合理区间()
    {
        var e = HalfDrawModel.Estimate(2.10m, 3.40m, 3.60m, Jq, 15m, 15m);
        Assert.NotNull(e);
        // 竞彩这一档报价 13~16 倍，隐含概率 5%~6%，模型不该跑出数量级外的数
        Assert.InRange(e!.ProbWinDraw, 3m, 9m);
        Assert.InRange(e.ProbLoseDraw, 3m, 9m);
        Assert.InRange(e.DrawProbability, 18m, 32m);
        // 平局 23% 上下时，两个方向合计不该超过平局本身
        Assert.True(e.ProbWinDraw + e.ProbLoseDraw <= e.DrawProbability);
    }

    [Fact]
    public void 公平赔率与EV都建立在同一概率上()
    {
        var e = HalfDrawModel.Estimate(2.10m, 3.40m, 3.60m, Jq, 15m, 15m)!;
        Assert.NotNull(e.FairOddsWinDraw);
        Assert.NotNull(e.EvWinDraw);

        // EV = P × 赔率（概率按小数计）
        var ev = Math.Round(e.ProbWinDraw / 100m * 15m, 2);
        Assert.InRange(Math.Abs(e.EvWinDraw!.Value - ev), 0m, 0.02m);

        // 公平赔率 = 1 / P
        var fair = Math.Round(1m / (e.ProbWinDraw / 100m), 1);
        Assert.InRange(Math.Abs(e.FairOddsWinDraw!.Value - fair), 0m, 0.15m);
    }

    [Fact]
    public void 用总进球报价时标记来源()
    {
        var withJq = HalfDrawModel.Estimate(2.10m, 3.40m, 3.60m, Jq, 15m, 15m)!;
        Assert.True(withJq.UsedTotalGoalsOdds);

        // 缺一档（J7 为 null）必须整体回落泊松，不能把缺的当 0
        var partial = new decimal?[] { 9.0m, 5.2m, 3.6m, 4.5m, 6.5m, 11m, 18m, null };
        var fallback = HalfDrawModel.Estimate(2.10m, 3.40m, 3.60m, partial, 15m, 15m)!;
        Assert.False(fallback.UsedTotalGoalsOdds);
        // 回落路径照样要出合理结果，不能是 0 或 NaN
        Assert.InRange(fallback.ProbWinDraw, 2m, 10m);
    }

    [Fact]
    public void 强弱悬殊时主队方向显著抬升()
    {
        // 主队超级大热（1.25 / 6.50 / 12.00），平局里半场主队领先的比例应明显更高
        var hot = HalfDrawModel.Estimate(1.25m, 6.50m, 12.00m, Jq, 20m, 20m)!;
        var even = HalfDrawModel.Estimate(2.60m, 3.20m, 2.60m, Jq, 20m, 20m)!;

        Assert.True(hot.ProbWinDraw / hot.ProbLoseDraw > even.ProbWinDraw / even.ProbLoseDraw,
            "主队大热时，胜平相对负平的比值应高于均势场");
        // 同价时（竞彩就是这么报的），大热场的胜平 EV 明显高于负平
        Assert.True(hot.EvWinDraw > hot.EvLoseDraw);
    }

    [Fact]
    public void 负平判定恒为永不购买()
    {
        var e = HalfDrawModel.Estimate(2.10m, 3.40m, 3.60m, Jq, 15m, 15m)!;
        // 市场把胜平的价格套用到负平上，负平真实概率低约 8% —— 这是结构性高估，不是概率问题
        Assert.Contains("永不购买", e.VerdictLoseDraw);

        // 胜平判定按 EV 分级，但返回的一定是"贵/接近/便宜"，不能是买入建议
        Assert.DoesNotContain("买入", e.VerdictWinDraw);
        Assert.DoesNotContain("推荐", e.VerdictWinDraw);
    }
}
