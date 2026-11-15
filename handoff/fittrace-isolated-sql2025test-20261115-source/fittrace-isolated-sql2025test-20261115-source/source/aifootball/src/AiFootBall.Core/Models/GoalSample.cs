namespace AiFootBall.Core.Models;

/// <summary>
/// 「联赛 × 比分」的聚合样本，用来回答「2 球 / 3 球 / 4 球该押哪一档」。
///
/// 为什么按 (联赛, 比分) 聚合而不是逐场拉：
/// 基线要的是「同一批竞彩联赛近两年怎么进球」，样本两万多场，逐场传进内存没必要；
/// 按联赛+比分分组后最多几百行，档位分布、比分频率、联赛习惯三种统计都能从它算出来。
/// </summary>
public sealed class GoalSampleRow
{
    /// <summary>联赛名，与竞彩受注卡的 LeagueName 同一套写法（英超/德甲/日职…）。</summary>
    public string LeagueName { get; set; } = "";

    public int HomeScore { get; set; }

    public int AwayScore { get; set; }

    /// <summary>这个联赛打出这个比分的场次。</summary>
    public int Matches { get; set; }
}
