namespace AiFootBall.Core.Models;

/// <summary>
/// 俱乐部画像（2026-11-07， dbo.FbTeamProfile，db/21_team_profile.sql 回填）。
/// 口径 = FbDailySkill SkillId=4（教训 1h，39.5 万场实测，主客合并全历史）：
///   · 反转率：半全场 6 个反转档（半场方向≠全场方向）合计占比——广岛三箭 43.2%、尤文 41.4%，
///     而拜仁 31.3%、勒沃库森 31.4%、巴黎 29.2%，日韩强队系统性高于欧洲豪门；
///   · 零封率：该队零封对手（对手 0 球）占比；
///   · 半场平转率：半场平且全场分胜负占比。
/// 引擎消费见 MilkPickEngine：热门方反转率≥38% → 反向档提级；
/// 碾压型真强队（反转低+零封低）不做反向——它们"赢球不赢盘"的反面：该赢就真赢。
/// </summary>
public sealed class TeamProfile
{
    public string TeamName { get; init; } = "";
    public int Games { get; init; }
    public decimal ReversalRate { get; init; }
    public decimal CleanSheetRate { get; init; }
    public decimal HalfDrawTurnRate { get; init; }
}
