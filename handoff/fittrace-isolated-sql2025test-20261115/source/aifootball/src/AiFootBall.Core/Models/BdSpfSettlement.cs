namespace AiFootBall.Core.Models;

/// <summary>
/// 北单让球胜平负的判中口径（2026-11-09 加，配套 bd-spf-settle）。
///
/// 北单让球数挂在**主队**：`Handicap=-1` 表示主队让 1 球、`+1` 表示主队受让 1 球。
/// 让球后主队净胜 = 主队进球 − 客队进球 + 让球数：
///   &gt;0 → 让胜(3) / =0 → 让平(1，走盘) / &lt;0 → 让负(0)。
///
/// ★ 让球数必须用**北单台账自己的 Handicap**（bd 板），不能借 FbMatch.Handicap（竞彩盘）。
///   11-07 的串关对账报告已经踩过这个坑：同一场比赛两套让球数不同，混用会把走盘判成赢或输。
/// ★ 这是纯函数，SQL 里不写 CASE。原因有两条：
///   ① HomeScore 是 tinyint，`HomeScore - AwayScore` 在 SQL 里会溢出（11-09 实测报 8115）；
///   ② 判定口径要能被单测锁死，塞进 SQL 就只能靠跑库验证。
/// </summary>
public static class BdSpfSettlement
{
    /// <summary>让球后主队净胜球。</summary>
    public static int NetAfterHandicap(int homeScore, int awayScore, decimal handicap)
        => homeScore - awayScore + (int)decimal.Truncate(handicap);

    /// <summary>让球后结果：'3' 让胜 / '1' 让平（走盘）/ '0' 让负。</summary>
    public static char Result(int homeScore, int awayScore, decimal handicap)
    {
        var net = NetAfterHandicap(homeScore, awayScore, handicap);
        return net > 0 ? '3' : net == 0 ? '1' : '0';
    }

    /// <summary>这一注是否命中。side 取 HandicapSide（缺失时退回 Side）。</summary>
    public static bool Judge(char side, int homeScore, int awayScore, decimal handicap)
        => side == Result(homeScore, awayScore, handicap);
}
