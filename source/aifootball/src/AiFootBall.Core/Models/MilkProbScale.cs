using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiFootBall.Core.Models;

/// <summary>
/// 存档三列概率的**单位与归一**校验（2026-11-14 加）。
///
/// 起因（11-14 08:55 验收）：BF 档的 <see cref="MilkProbSnapshot.ModelPctPure"/> 写的是**小数**
/// （0.073），而同表其他四个玩法写的是**百分数**（7.3）。库里当场表现为
/// `SUM(ModelPctPure)=0.997` 对 `SUM(BF BlendedPct)=30.68`，逐场合计三列各自为
/// 1.00 / 99.95 / 30.69，谁也不等于 100。
///
/// 这不是「显示不好看」：这张表是赛后复盘「模型错还是市场错」的唯一凭据，
/// 单位错一列的后果是任何按它算的汇总（Hit@1、权重校准、玩法对照）都被放大 100 倍或缩小 100 倍。
///
/// 为什么必须有这道校验，而不是改一行乘 100 就完事（用户 2026-11-14 明确要求）：
///   · 乘 100 只修了**当前这一处**；同一类错误（某档用了 0~1、某档用了 0~100）随时会再犯，
///     而它不会让任何东西报错——只会让数字悄悄变小一百倍；
///   · 归一才是判据：一个玩法的全部档位概率**必须**合计 100%（±容差），
///     这跟「拿什么单位写的」无关，写错了必然对不上。
/// 所以校验只看两件事：**概率分布合计是不是 100**、**三列彼此是不是同一把尺**。
/// 它的结论是「这一轮不许发布」，而不是「记一条警告」。
///
/// <see cref="MilkProbSnapshot.MarketPct"/> 不参加合计。它的定义是
/// <c>round(100 ÷ 赔率 ÷ 该玩法固定水位, 3)</c>，固定水位不是本场 Σ(1/赔率)。
/// 2026-11-17 16:40 轮（RunId 20261117T164001-a67bb5，版本戳仍是 1.0.0.0+e4b46bc2）
/// 比分 31 档的 MarketPct 合计 103.425~103.536，纯模型和混合列都在 99.998~100.002。
/// 抽查 1503020 的 1:0：赔率 5.750，100÷5.750÷1.410=12.334，与存档一致。
/// 多出来的约 3.5 个点是固定水位 1.410 小于当场 Σ(1/赔率)≈1.459，不是网格尾部被加了两次。
/// </summary>
public static class MilkProbScale
{
    /// <summary>
    /// 概率分布允许的舍入偏差（百分点）。
    ///
    /// 存档百分数四舍五入到 3 位。比分 31 档最多累积 31×0.0005=0.0155 个百分点，
    /// 总进球 8 档 0.004，半全场 9 格 0.0045。0.05 盖住这个舍入，
    /// 盖不住少算或多算一整段尾部（那种偏差是几个百分点）。
    /// </summary>
    public const decimal Tolerance = 0.05m;

    /// <summary>
    /// 校验一轮的全部存档行。返回的问题进发布闸（有任何一条即不发布）。
    /// </summary>
    public static List<MilkLedgerIssue> Verify(IEnumerable<MilkProbSnapshot>? rows)
    {
        var issues = new List<MilkLedgerIssue>();
        var list = (rows ?? Array.Empty<MilkProbSnapshot>()).Where(r => r.MatchId > 0).ToList();
        if (list.Count == 0) return issues;

        foreach (var g in list.GroupBy(r => (r.MatchId, r.PlayType)))
        {
            var pure = g.Where(r => r.ModelPctPure is not null).Select(r => r.ModelPctPure!.Value).ToList();
            var blend = g.Where(r => r.BlendedPct is not null).Select(r => r.BlendedPct!.Value).ToList();

            // 纯模型列是必填的：一档都没有纯模型值，说明这个玩法根本没算（那是另一类缺陷）。
            if (pure.Count == 0) continue;

            // 合计只在**整组都有值**时才判：缺值的档是「没算出来」，
            // 差额不能算到单位头上，也不能因为缺一档就把一轮拦下来（那是另一条判据的事）。
            var complete = pure.Count == g.Count();
            if (complete) Check(issues, g.Key, "ModelPctPure", pure);
            if (complete && blend.Count == g.Count()) Check(issues, g.Key, "BlendedPct", blend);
            // MarketPct 是固定水位下的逐档隐含，不要求 31/8/9 档加总为 100。
            // 单位混用仍由下面的 pure/market 比值抓住。

            // 三列同尺：混合值是模型与市场的加权，不可能比纯模型大一个数量级。
            // 这一条专门抓「某列用了 0~1、另一列用了 0~100」——单位混用时比值会差 100 倍。
            // 只在两列都非空的档上比，所以不依赖「整组是否完整」。
            // 单档也要判：只有一档时「合计」没法验，但两列的量级关系照样能抓出单位混用。
            var paired = g.Where(r => r.ModelPctPure is not null && r.MarketPct is not null).ToList();
            if (paired.Count >= 1)
            {
                var sumPure = paired.Sum(r => r.ModelPctPure!.Value);
                var sumMarket = paired.Sum(r => r.MarketPct!.Value);
                if (sumPure > 0m && sumMarket > 0m)
                {
                    var ratio = sumPure / sumMarket;
                    if (ratio < 0.9m || ratio > 1.1m)
                        issues.Add(new MilkLedgerIssue
                        {
                            Table = "snapshot", Kind = "unit-mismatch",
                            Key = $"{g.Key.MatchId}|{g.Key.PlayType}"
                                  + $"|pure={sumPure.ToString("0.###", CultureInfo.InvariantCulture)}"
                                  + $"|market={sumMarket.ToString("0.###", CultureInfo.InvariantCulture)}",
                            Expected = 1, Actual = 0
                        });
                }
            }
        }

        return issues;
    }

    private static void Check(
        List<MilkLedgerIssue> issues, (long MatchId, string PlayType) key, string column, List<decimal> values)
    {
        var sum = values.Sum();
        if (Math.Abs(sum - 100m) <= Tolerance) return;
        var hint = sum is > 0m and < 2m ? "（疑似 0~1 小数当百分数）" : string.Empty;
        issues.Add(new MilkLedgerIssue
        {
            Table = "snapshot", Kind = "prob-not-normalized",
            Key = $"{key.MatchId}|{key.PlayType}|{column}"
                  + $"|sum={sum.ToString("0.###", CultureInfo.InvariantCulture)}{hint}",
            Expected = 100, Actual = (int)Math.Round(sum)
        });
    }
}
