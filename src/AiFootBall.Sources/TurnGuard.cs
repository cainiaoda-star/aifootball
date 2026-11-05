using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 临场转向哨兵（2026-11-05 加）。
///
/// 为什么需要它：方向闸（MilkDirectionGate）只在出注那一刻跑一次，存的是静态快照——
/// 它回答的是「当时模型和市场各指哪儿」，回答不了「之后市场有没有偷偷转向」。
/// 而市场转向恰恰是时间序列上的漂移：让胜赔率一路爬升、净胜球期望持续下修、
/// 客队深档整批降价——净胜球诊断（FbScoreDiag）每 2 分钟在刷新，却没有一条代码
/// 把「已出注的场次临场发生了市场转向」主动推出来。这就是「早干啥去了」的答案：
/// 数据在采、信号在算，告警闭环断在最后一公里。
///
/// 本类补上这最后一公里：开赛前每 10 分钟扫一次当天已发出的未开赛大奶注，
/// 对比净胜球诊断的转向信号与注的方向，命中的落一条告警到 dbo.FbTurnAlert。
///
/// 判定是纯函数（<see cref="Judge"/>），数据获取由调用方（RecommendationService）注入，
/// 便于单测，也便于事后拿历史盘口回测「当时的告警该不该响」。
/// </summary>
public static class TurnGuard
{
    /// <summary>期望净胜球从开盘到现在的下修幅度超过这个值，才算「主队信心流失」（球）。</summary>
    public const decimal GdShrinkThreshold = 0.10m;

    /// <summary>客队深档（净负 ≥3）里赔率下降 ≥5% 的档数达到这个数，才算「整批降价」。</summary>
    public const int DeepDropThreshold = 3;

    /// <summary>让胜赔率从出注时到临场的拉升幅度超过这个比例，才算「主队赢盘被抛弃」。</summary>
    public const decimal RangShengRisePct = 0.08m;

    /// <summary>
    /// 给一场已发出的注做转向诊断。返回要落库的告警，没有转向则返回 null。
    ///
    /// <paramref name="pickDir"/> 是这一注指向的胜负方向（3 主胜 / 1 平 / 0 客胜），
    /// 总进球没有方向，传 null（此时只看信号本身，不判「与注相反」）。
    /// </summary>
    public static TurnAlertRow? Judge(
        MilkPickRow pick,
        char? pickDir,
        ScoreDiagRow? diag,
        (decimal OddsAtIssue, decimal OddsNow)? rangSheng)
    {
        if (diag is null) return null;

        var notes = new List<string>();
        var high = false;

        // 信号 1：净胜球期望下修——市场对主队信心在流失。
        var gdShrunk = diag.ExpGdMove is decimal move && move <= -GdShrinkThreshold;
        if (gdShrunk)
            notes.Add($"期望净胜球 {diag.ExpGdOpen:0.000}→{diag.ExpGd:0.000}（下修 {diag.ExpGdMove:0.000}）");

        // 信号 2：客队深档整批降价——资金在往客队方向压。
        var deepDrop = diag.DeepNegDrop >= DeepDropThreshold
                       || (diag.TailTag?.Contains("客队深档", StringComparison.Ordinal) ?? false);
        if (deepDrop)
            notes.Add($"客队深档整批降价（{diag.DeepNegDrop} 档），尾盘标签「{diag.TailTag}」");

        // 信号 3：让胜赔率单边拉升——主队赢盘被市场抛弃。
        var rangShengUp = rangSheng is { } r && r.OddsAtIssue > 0
                          && r.OddsNow >= r.OddsAtIssue * (1m + RangShengRisePct);
        if (rangShengUp)
            notes.Add($"让胜赔率 {rangSheng!.Value.OddsAtIssue:0.00}→{rangSheng.Value.OddsNow:0.00}（单边拉升）");

        if (notes.Count == 0) return null;

        // 转向方向：下修期望净胜球 + 客队深档降价都指向「主队要走弱」→ 市场转向客队。
        // 与注的方向相反时才值得报警；注没有方向（总进球）则只报告信号。
        var turningAway = gdShrunk || deepDrop;   // 指向客队/主队走弱
        var againstPick = pickDir is char pd && turningAway && pd == '3';
        if (pickDir is not null && !againstPick && !rangShengUp) return null;

        if (gdShrunk && deepDrop) high = true;

        var reason = string.Join("；", notes);
        if (againstPick)
            reason = $"这一注指{MilkDirectionDiagnosis.DirName(pickDir)}，市场在临场反向转向：" + reason;

        return new TurnAlertRow
        {
            MatchId = pick.MatchId,
            MatchNo = pick.MatchNo,
            LeagueName = pick.LeagueName,
            HomeTeam = pick.HomeTeam,
            AwayTeam = pick.AwayTeam,
            MatchTime = pick.MatchTime,
            PickOption = pick.PlayType switch
            {
                JcPlayTypes.Bq => $"半全场 {MilkPickMath.ShortName(pick.PlayType, pick.OptionCode)}",
                JcPlayTypes.Bf => $"比分 {pick.OptionCode}",
                JcPlayTypes.Jq => $"总进球 {MilkPickMath.ShortName(pick.PlayType, pick.OptionCode)}",
                _ => pick.OptionName
            },
            Reason = reason,
            Severity = high ? "high" : "mid",
            ScoreDiagTag = diag.TailTag,
            ExpGdOpen = diag.ExpGdOpen,
            ExpGdNow = diag.ExpGd,
            ExpGdMove = diag.ExpGdMove
        };
    }

    /// <summary>从一条大奶注反推它指向的胜负方向（与 MilkPickEngine.FullTimeDirection 同口径）。</summary>
    public static char? PickDirection(string playType, string optionCode)
    {
        switch (playType)
        {
            case JcPlayTypes.Bq:
                return optionCode.Length == 3 ? optionCode[2] : null;

            case JcPlayTypes.Bf:
            {
                if (optionCode.EndsWith("其它", StringComparison.Ordinal))
                    return optionCode[0] switch { '胜' => '3', '平' => '1', '负' => '0', _ => null };
                var s = SplitScore(optionCode);
                if (s is null) return null;
                return s.Value.H > s.Value.A ? '3' : s.Value.H == s.Value.A ? '1' : '0';
            }

            default:
                return null;
        }
    }

    private static (int H, int A)? SplitScore(string code)
    {
        var parts = code.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var a)) return null;
        return (h, a);
    }
}
