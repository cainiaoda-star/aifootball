namespace AiFootBall.Core.Models;

/// <summary>
/// 不进串关的联赛黑名单（2026-11-07 用户拍板，起因见下）。
///
/// 澳超：无升降级联赛，弱队无保级压力、强队争冠无望即摆烂，比赛动机天然不受约束；
/// 全库 1003 场实测：场均 3.05 球（全库 2.80）、总进球≥4 占 36.3%（全库 30.8%）、
/// 主队不胜率 58.3%（全库 56.7%，主场优势最弱的一档）——高进球 + 高方差 + 判罚争议高发。
/// 2026-11-07 珀斯光荣 vs 纽卡斯尔喷气机：越位进球被判有效引发争议，而该场我们推的半全场
/// 「负负」正是实购 1600 倍 4 串 1 的唯一挂腿。用户要求整类踢出串关。
///
/// 用法：任何"要拿去组串"的选注（北单串关、大奶榜）都先过 <see cref="IsExcluded"/>。
/// 需要恢复某联赛只要从 <see cref="Names"/> 里删掉即可，不要在调用点硬编码。
/// </summary>
public static class NoParlayLeagues
{
    /// <summary>不进串关的联赛名（含子串匹配，如「澳超」也拦「澳超联」）。</summary>
    private static readonly string[] Names = { "澳超" };

    /// <summary>该联赛是否禁止进串关。</summary>
    public static bool IsExcluded(string? leagueName)
    {
        if (string.IsNullOrWhiteSpace(leagueName)) return false;
        var lg = leagueName.Trim();
        foreach (var n in Names)
        {
            if (lg.Length == 0) return false;
            if (lg.Equals(n, StringComparison.Ordinal)) return true;
            if (lg.Contains(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
