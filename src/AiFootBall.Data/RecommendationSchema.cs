using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>启动时执行推荐表的增量脚本。脚本本身可重复执行。</summary>
public static class RecommendationSchema
{
    private static readonly Regex GoSplitter = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static async Task EnsureAsync(SqlConnectionFactory factory, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        foreach (var file in new[]
                 {
                     "03_recommendation_integrity.sql", "04_lotto_recommendation.sql",
                     "06_recommendation_stale_results.sql", "07_recommendation_handicap.sql",
                     "08_bd_history.sql", "09_calibration.sql",
                     // 票面台账、否决台账、票腿双概率。三个脚本都用 OBJECT_ID / COL_LENGTH 守卫，
                     // 重复执行安全；放进来自动建，免得每加一张表都得手工跑一遍。
                     "10_ticket_kpi.sql", "11_recommendation_veto.sql", "12_ticket_leg_probability.sql",
                     // 七乐彩/七星彩停用（IsActive 此前从没被查询用过，字段是摆设）
                     "13_deactivate_unsupported_lottery.sql",
                     // 大奶推荐台账 + 票腿玩法代码（比分/半全场/总进球的判定不走让球逻辑）
                     "14_milk_pick.sql",
                     // 竞彩赔率历史补 MatchId 索引：110 万行没有这个索引，大奶每次重算都是全表扫，
                     // 赔率回补按「有无竞彩场次」排序时直接超时（2026-11-04 实测）
                     "15_jc_odds_index.sql",
                     // 数字彩用户选号票：避热门号选号器保存的票 + 手输票，按期对奖
                     "16_lotto_user_ticket.sql"
                 })
        {
            var sql = LoadScript(file);
            foreach (var batch in GoSplitter.Split(sql).Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 60 };
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static string LoadScript(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未找到嵌入脚本 {fileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
