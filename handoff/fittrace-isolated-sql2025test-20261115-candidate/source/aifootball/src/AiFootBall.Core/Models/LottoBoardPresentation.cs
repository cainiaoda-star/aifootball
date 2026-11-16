namespace AiFootBall.Core.Models;

/// <summary>
/// 推荐板展示口径（无 IO）：区分历史推荐 / 未绑定期号示例，禁止把历史标成「今天的当期」。
/// </summary>
public static class LottoBoardPresentation
{
    public const string KindHistorical = "historical";
    public const string KindPreview = "preview";

    public static string BoardKind(bool issueBound, bool fromSavedRecords) =>
        fromSavedRecords && issueBound ? KindHistorical
        : !issueBound ? KindPreview
        : KindHistorical;

    public static string StatusLabel(string boardKind, string? targetIssue, DateTime? generatedAt)
    {
        var when = generatedAt?.ToString("yyyy-MM-dd HH:mm") ?? generatedAt?.ToString("yyyy-MM-dd") ?? "未知时间";
        if (boardKind == KindPreview)
            return $"未绑定期号的号码示例 · 生成于 {when}（不入正式推荐/复盘）";
        var issue = string.IsNullOrWhiteSpace(targetIssue) ? "（无目标期）" : $"目标期 {targetIssue}";
        return $"历史推荐 · {issue} · 生成于 {when}（非今日当期冒充）";
    }

    public static object GradeApi(LottoHitMath.Grade? grade, bool drawAvailable)
    {
        if (!drawAvailable)
        {
            var pending = LottoHitMath.Pending();
            return new
            {
                status = LottoHitStatusRules.ToApi(LottoHitStatus.Pending),
                countsInHitRate = false,
                showAsWin = false,
                determined = false,
                note = pending.Note,
                redHits = (int?)null,
                blueHits = (int?)null
            };
        }

        if (grade is null)
        {
            return new
            {
                status = LottoHitStatusRules.ToApi(LottoHitStatus.Undetermined),
                countsInHitRate = false,
                showAsWin = false,
                determined = false,
                note = "无法判定",
                redHits = (int?)null,
                blueHits = (int?)null
            };
        }

        return new
        {
            status = LottoHitStatusRules.ToApi(grade.Status),
            countsInHitRate = LottoHitStatusRules.CountsInHitRate(grade.Status),
            showAsWin = LottoHitStatusRules.ShowAsWin(grade.Status),
            showMatchMarks = LottoHitStatusRules.ShowMatchMarks(grade.Status),
            determined = grade.Determined,
            note = grade.Note,
            redHits = grade.HasMatchDetail ? grade.RedHits : (int?)null,
            blueHits = grade.HasMatchDetail ? grade.BlueHits : (int?)null
        };
    }

    /// <summary>从生成结果汇总真实保存数（不把生成数当保存数）。</summary>
    public static LottoGenerateSaveResult BuildSaveResult(
        IReadOnlyList<LottoPlanSet> plans,
        IReadOnlyList<string> savedLotteryCodes)
    {
        var savedSet = new HashSet<string>(savedLotteryCodes, StringComparer.OrdinalIgnoreCase);
        var generated = plans.Sum(p => p.Picks.Count);
        var savedPicks = plans.Where(p => savedSet.Contains(p.LotteryCode)).Sum(p => p.Picks.Count);
        var savedBoards = plans.Count(p => savedSet.Contains(p.LotteryCode));
        var skips = new List<string>();
        foreach (var p in plans)
        {
            if (savedSet.Contains(p.LotteryCode)) continue;
            if (p.Picks.Count == 0)
                skips.Add($"{p.LotteryName}：无方案，未保存");
            else if (!p.IssueBound)
                skips.Add($"{p.LotteryName}：未绑定期号，跳过保存（生成 {p.Picks.Count} 注仅作示例）");
            else
                skips.Add($"{p.LotteryName}：未保存");
        }

        var msg = savedPicks == 0
            ? $"实际保存 0 注（生成 {generated} 注；" + (skips.Count > 0 ? string.Join("；", skips) : "全部跳过") + "）"
            : $"实际保存 {savedPicks} 注 / {savedBoards} 个彩种（生成 {generated} 注"
              + (skips.Count > 0 ? "；" + string.Join("；", skips) : "") + "）";

        return new LottoGenerateSaveResult
        {
            Plans = plans.ToList(),
            GeneratedPickCount = generated,
            SavedPickCount = savedPicks,
            SavedBoardCount = savedBoards,
            SkippedBoardCount = plans.Count - savedBoards,
            SkipReasons = skips,
            Message = msg
        };
    }
}
