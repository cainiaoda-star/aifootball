namespace AiFootBall.Core.Models;

/// <summary>
/// 终版价口径（2026-11-10 加，用户拍板做「B」）。
///
/// **问题**：三张台账存的都是「推荐生成那一刻」的价。
///   · 北单：推荐 20:40 出，用户 23:00 才买，实购价与台账差 10~46%
///     （阿尔杰什 2.91→4.25、阿劳 2.07→3.74、格勒诺布尔 2.45→3.83）。
///   · 稳胆：赔率 1.0~1.75，回报在 0.95~1.00 之间，**3% 的价差就足以翻转结论**。
/// 拿这种数字算出来的回报不是账本，是「假如我能用推荐价买到」的假设。
///
/// **现在的做法**：不改 <c>Odds</c>（那是当时页面上写的价，改了就是篡改历史），
/// 另加 <c>FinalOdds</c> 存「真正能拿到/真正赔付」的价，战绩同时报两栏。
///
/// 两个来源，语义不同，必须写清楚是哪一个：
///   · <see cref="JcCloseSnapshot"/>：竞彩，取 FbJcOddsHistory 里开赛前最后一次采样。
///     竞彩是固定奖金，停售前那一刻的价就是能买到的价，所以这是「实购价」的最佳近似。
///   · <see cref="BdResultSp"/>：北单，取 FbBdHistory 的**开奖 SP**（ResultSp）。
///     北单按 SP 派奖，开奖 SP 就是真实赔付价，比任何赛前快照都硬。
///     代价：只有命中的那注才有（没开出的选项没有 SP）——但回报的分母是全部注数、
///     未命中的赔付本来就是 0，所以「命中注用开奖 SP、未命中记 0」算出来的回报是**精确值**。
/// </summary>
public static class FinalPriceSource
{
    /// <summary>竞彩：开赛前最后一次赔率采样。</summary>
    public const string JcCloseSnapshot = "jc-close";

    /// <summary>北单：开奖 SP（= 实际派奖赔率）。</summary>
    public const string BdResultSp = "bd-resultsp";

    public static string Label(string? source) => source switch
    {
        JcCloseSnapshot => "停售快照",
        BdResultSp => "开奖SP",
        _ => "—"
    };
}

/// <summary>
/// 终版价口径的算法（纯函数，可单测）。**别在别处另写一份**——
/// 这个项目已经因为「两道闸各写一份常数」出过事故（出票放行后哨兵立刻撤注）。
/// </summary>
public static class FinalPriceMath
{
    /// <summary>
    /// 终版价覆盖率低于这个数，<c>RoiFinal</c> 不能当账本看（命中注里有 5% 拿不到终版价，
    /// 少算的就是这些命中的钱，回报会被系统性低估）。
    /// </summary>
    public const decimal MinHitCoverage = 95m;

    /// <summary>命中注里有多少注拿得到终版价（%）。分母是命中注数，不是全部注数。</summary>
    public static decimal HitCoverage(int hits, int finalHits)
        => hits <= 0 ? 0m : Math.Round(finalHits * 100m / hits, 1);

    /// <summary>
    /// 终版价口径的平注回报 = 命中注终版价合计 ÷ 已结算注数。
    ///
    /// 分母必须是**全部已结算注数**（含未命中），不是「有终版价的注数」：
    /// 北单只有命中注才有开奖 SP，拿它当分母会把回报算成 100% 以上。
    /// 未命中的赔付是 0，缺终版价不影响分子。
    /// </summary>
    public static decimal Roi(int settled, decimal finalHitOddsSum)
        => settled <= 0 ? 0m : Math.Round(finalHitOddsSum / settled, 3);

    /// <summary>这个回报能不能当账本引用（覆盖率够 + 有命中注）。</summary>
    public static bool Trustworthy(int hits, int finalHits)
        => hits > 0 && HitCoverage(hits, finalHits) >= MinHitCoverage;
}
