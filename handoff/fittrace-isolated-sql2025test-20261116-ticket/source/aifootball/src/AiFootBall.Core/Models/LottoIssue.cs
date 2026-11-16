using System.Text.RegularExpressions;

namespace AiFootBall.Core.Models;

/// <summary>
/// 数字彩期号：官方「下一销售期」推算规则尚未在本仓库按彩种核实前，
/// <b>禁止</b>用最新开奖 +1 或跨年重置去猜测目标期号。
/// 用户显式填写的期号只做语法校验（非空数字串），不代表已对照销售日历。
/// </summary>
public static class LottoIssue
{
    private static readonly Regex Digits = new(@"^\d{5,8}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 尝试解析用户填写的目标期号。失败时返回明确错误，供 API 要求重填。
    /// 不根据历史开奖推算下一期。
    /// </summary>
    public static bool TryNormalizeUserIssue(string? raw, out string issue, out string error)
    {
        issue = "";
        error = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "请填写目标期号。系统不会根据最新开奖自动推算下一期（官方期号规则尚未按彩种核实）。";
            return false;
        }

        var t = raw.Trim();
        if (!Digits.IsMatch(t))
        {
            error = "期号格式无效：请填写 5～8 位数字期号（与彩票官方期号一致），不要留空。";
            return false;
        }

        issue = t;
        return true;
    }

    /// <summary>
    /// 自动解析「下一销售期」。当前无已核实的彩种规则表，一律返回 false。
    /// </summary>
    public static bool TryResolveNextSalesIssue(string lotteryCode, string? basedOnIssue, out string issue, out string reason)
    {
        issue = "";
        reason =
            $"彩种 {lotteryCode} 的官方下一销售期规则尚未在本仓库核实；基于 {basedOnIssue ?? "（无）"} 不自动生成目标期号。";
        return false;
    }
}
