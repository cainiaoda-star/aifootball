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

    /// <summary>客胜盈亏指数（PnlIdx）深负阈值：钱在净流入客胜，主胜热门被客队爆冷的前兆。</summary>
    public const decimal AwayPnlDeepNegative = -60m;

    /// <summary>客胜必发占比异常走高阈值：低水热门场客胜占比通常 &lt;25%，超过即异常。</summary>
    public const decimal AwayRatioAbnormal = 30m;

    /// <summary>
    /// 给一场已发出的注做转向诊断。返回要落库的告警，没有转向则返回 null。
    ///
    /// <paramref name="pickDir"/> 是这一注指向的胜负方向（3 主胜 / 1 平 / 0 客胜），
    /// 总进球没有方向，传 null（此时只看信号本身，不判「与注相反」）。
    /// <paramref name="awayMoney"/> 是必发静态资金快照（客胜盈亏指数 PnlIdxAway、客胜占比 RatioAway）。
    /// 前三个信号是「临场时间序列转向」，这个第四个是「静态资金异常」——
    /// 主胜大热反向/客胜资金涌入这种信号没有时间漂移，净胜球诊断不会报，
    /// 但它是欧战低水热门被爆冷（AC米兰/勒沃库森/桑德兰）最直接的铁证。
    /// </summary>
    public static TurnAlertRow? Judge(
        MilkPickRow pick,
        char? pickDir,
        ScoreDiagRow? diag,
        (decimal OddsAtIssue, decimal OddsNow)? rangSheng,
        (decimal? PnlIdxAway, decimal? RatioAway)? awayMoney = null,
        PriceDivergence.Result? divergence = null)
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

        // 信号 4（静态资金异常）：钱往客胜跑，主胜热门被爆冷的前兆。
        // 这个信号没有时间漂移——它是「主胜大热反向/客胜资金涌入」的静态铁证，
        // 净胜球诊断（每 2 分钟刷新的时间序列）报不出来，必须单独看必发静态快照。
        var awayPnlDeep = awayMoney is { } am && am.PnlIdxAway is <= AwayPnlDeepNegative;
        var awayRatioHot = awayMoney is { } am2 && am2.RatioAway is >= AwayRatioAbnormal;
        if (awayPnlDeep)
            notes.Add($"客胜盈亏指数 {awayMoney!.Value.PnlIdxAway:0}（深负，钱净流入客胜）");
        if (awayRatioHot)
            notes.Add($"客胜必发占比 {awayMoney!.Value.RatioAway:0.#}%（异常走高）");

        // 信号 5（定价背离，2026-11-08 加）：竞彩单边砸价、必发不认。
        // 与信号 1-4 不是一回事——那四条是「市场在转向」，这一条是「这个价本身就是假的」：
        // 2026-11-08 英超 诺丁汉森林 3:1 曼城，竞彩客胜 1.60→1.43（-10.6%），
        // 必发 PriceAway 只动 -1.1%。散户在竞彩砸热门，专业资金一个价都不接，结果直接翻车。
        // 回测见 PriceDivergence：客胜侧该形态 13 场实际 23.1% vs 隐含 40.1%（-17 点），
        // 主胜侧 9 场 +3.4 点（无偏离，所以主胜侧这里只报 mid，不报 high）。
        var fakeHotAway = divergence is { AwayFakeHot: true };
        var fakeHotHome = divergence is { HomeFakeHot: true };
        if (fakeHotAway)
            notes.Add($"客胜被竞彩单边砸价（{divergence!.Away!.Value.JcPct:0.#}%），必发只动 {divergence.Away.Value.ExPct:0.#}%");
        if (fakeHotHome)
            notes.Add($"主胜被竞彩单边砸价（{divergence!.Home!.Value.JcPct:0.#}%），必发只动 {divergence.Home.Value.ExPct:0.#}%");

        if (notes.Count == 0) return null;

        // 转向方向：下修期望净胜球 + 客队深档降价 + 客胜资金涌入都指向「主队要走弱」→ 市场转向客队。
        // 与注的方向相反时才值得报警；注没有方向（总进球）则只报告信号。
        var turningAway = gdShrunk || deepDrop || awayPnlDeep || awayRatioHot;   // 指向客队/主队走弱
        // 定价背离的「相反」是另一回事：注押的那一侧，其价格本身是散户堆出来的假价。
        var priceFakeAgainstPick = pickDir switch
        {
            '0' => fakeHotAway,
            '3' => fakeHotHome,
            _ => false
        };
        var againstPick = (pickDir is char pd && turningAway && pd == '3') || priceFakeAgainstPick;
        if (pickDir is not null && !againstPick && !rangShengUp) return null;

        if (gdShrunk && deepDrop) high = true;
        if (awayPnlDeep && (gdShrunk || deepDrop)) high = true;
        if (fakeHotAway) high = true;

        var reason = string.Join("；", notes);
        if (againstPick)
            reason = priceFakeAgainstPick
                ? $"这一注指{MilkDirectionDiagnosis.DirName(pickDir)}，但该侧价格是竞彩单边砸出来的、必发不认：" + reason
                : $"这一注指{MilkDirectionDiagnosis.DirName(pickDir)}，市场在临场反向转向：" + reason;

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
