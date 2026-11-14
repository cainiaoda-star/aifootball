namespace AiFootBall.Core.Models;

/// <summary>
/// 大奶榜的**唯一一份门槛配置**（2026-11-14 第九轮加）。
///
/// 为什么要有这个文件：门槛曾经有两份——引擎用 <c>MilkPickEngine.MinTeamGames = 3</c> 决定
/// 「样本够不够出票」，发布核对用 <c>MilkSourceSlate.DefaultMinTeamGames = 5</c> 判断
/// 「`name-unresolved` 是不是真新队」。两个数字不一样，后果是：一支库内有 4 场样本的队，
/// 引擎认为「够了、可以算」，核对器却认为「不足门槛、当新队放行」——
/// 同一轮里对同一支队给出两个相反的结论，而两边都自称有依据。
///
/// 收敛原则（用户 2026-11-14 明确）：**用同一份明确配置，不擅自调高或调低**。
/// 取值保持 3——这是引擎线上真实生效的值，核对器向它对齐，不是反过来放宽引擎。
/// 至于「3 场够不够」是另一个问题（回测另议），不在本轮范围内改动。
/// </summary>
public static class MilkThresholds
{
    /// <summary>
    /// 一支队在**本联赛**近 18 个月的最少完赛场次。
    /// 低于它：引擎判 thin-sample 不出票；核对器判「真新队」，`name-unresolved` 才可接受。
    /// </summary>
    public const int MinTeamGames = 3;

    /// <summary>判「真新队」独立库检的回看窗口（月）。与引擎取样本的窗口同一口径。</summary>
    public const int TeamHistoryMonths = 18;

    /// <summary>赔率采样允许的最大年龄（分钟）。超龄不是「数据旧」，是「本轮不该拿它出票」。</summary>
    public const int MaxSampleAgeMinutes = 30;
}
