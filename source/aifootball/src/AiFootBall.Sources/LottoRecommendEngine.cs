using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 数字彩选号引擎：乐透型（双色球/大乐透）主推 1 + 备选 3；
/// 数字型（3D/排三）直选主推 1 + 备选 2，排五直选主推 1 + 备选 3。
/// 规则可解释，不作「必中」承诺。
/// </summary>
public static class LottoRecommendEngine
{
    public static LottoPlanSet Recommend(LotteryTypeInfo lt, IReadOnlyList<LotteryDrawRecord> drawsDesc)
    {
        if (drawsDesc.Count < 30)
        {
            return new LottoPlanSet
            {
                LotteryCode = lt.Code,
                LotteryName = lt.Name,
                TargetIssue = "",
                RecDate = DateOnly.FromDateTime(DateTime.Today),
                Summary = "历史开奖不足 30 期，暂不生成方案"
            };
        }

        var based = drawsDesc[0].Issue;
        // 官方下一销售期规则未核实前不自动绑定期号（禁止 +1 / 跨年猜测）。
        LottoIssue.TryResolveNextSalesIssue(lt.Code, based, out var target, out var issueReason);
        var set = new LottoPlanSet
        {
            LotteryCode = lt.Code,
            LotteryName = lt.Name,
            TargetIssue = target,
            BasedOnIssue = based,
            RecDate = DateOnly.FromDateTime(DateTime.Today)
        };

        if (LottoCodes.IsDigitGame(lt.Code))
            set.Picks = RecommendDigit(lt, drawsDesc);
        else if (LottoCodes.IsKl8(lt.Code))
            set.Picks = RecommendKl8(lt, drawsDesc);
        else
            set.Picks = RecommendCombo(lt, drawsDesc);

        var danTuo = LottoCodes.IsDlt(lt.Code) ? DltFourDanFullTuo(lt, drawsDesc) : null;
        var singles = set.Picks.Count;
        if (danTuo is not null)
        {
            danTuo.RankNo = set.Picks.Count + 1;
            set.Picks.Add(danTuo);
        }

        if (set.Picks.Count == 0)
            set.Summary = "未能生成有效方案";
        else if (!set.IssueBound)
            set.Summary =
                $"未绑定期号的号码示例（基于第 {based} 期开奖统计）。{issueReason} 不保存、不进入正式推荐或复盘。";
        else if (LottoCodes.IsKl8(lt.Code))
            set.Summary = $"基于第 {based} 期，为第 {target} 期生成 {set.Picks.Count} 注选十（主推 {set.Picks.Count(p => p.IsPrimary)} 注）。每天开奖，只供参考";
        else if (danTuo is not null)
            set.Summary = $"基于第 {based} 期，为第 {target} 期生成单式 {singles} 注（主推 {set.Picks.Count(p => p.IsPrimary)} 注），另附{danTuo.PlayType} {danTuo.BetCount} 注";
        else
            set.Summary = $"基于第 {based} 期，为第 {target} 期生成 {set.Picks.Count} 注参考选号（主推 {set.Picks.Count(p => p.IsPrimary)} 注）";
        return set;
    }

    /// <summary>
    /// 大乐透四胆全拖：前区 4 个胆每注都带，其余 31 个号全部当拖，每注再取 1 个拖码。
    /// 后区只定 2 个号。后区 12 个全包不是这个方案。
    /// </summary>
    public static LottoPick? DltFourDanFullTuo(LotteryTypeInfo lt, IReadOnlyList<LotteryDrawRecord> drawsDesc)
    {
        if (!LottoCodes.IsDlt(lt.Code) || drawsDesc.Count < 30 || lt.RedMax < 6 || lt.BlueCount < 2)
            return null;

        var redFreq = LotteryMath.ComputeFrequency(drawsDesc, 'R', lt.RedMax);
        var blueFreq = LotteryMath.ComputeFrequency(drawsDesc, 'B', lt.BlueMax);
        var dan = redFreq.OrderByDescending(BalancedRed).ThenBy(f => f.Number).Take(4)
            .Select(f => f.Number).OrderBy(x => x).ToList();
        var tuo = Enumerable.Range(1, lt.RedMax).Where(n => !dan.Contains(n)).ToList();
        if (dan.Count != 4 || tuo.Count < 2) return null;

        var blues = blueFreq.OrderByDescending(BalancedBlue).ThenBy(f => f.Number).Take(lt.BlueCount)
            .Select(f => f.Number).OrderBy(x => x).ToList();
        if (blues.Count != lt.BlueCount) return null;

        var score = (decimal)Math.Round(dan.Average(n => BalancedRed(redFreq.First(f => f.Number == n))) * 100, 1);
        var backWays = lt.BlueMax * (lt.BlueMax - 1) / 2;
        return new LottoPick
        {
            IsPrimary = false,
            PlayType = "四胆全拖",
            Reds = dan,
            TuoReds = tuo,
            Blues = blues,
            BetCount = tuo.Count,
            Score = score,
            SignalDesc = $"前区 4 胆，拖码是除胆以外的全部 {tuo.Count} 个。每注是这 4 个胆再加 1 个拖码，共 {tuo.Count} 注。后区定 {blues.Count} 个，不把后区全包（全包是 {tuo.Count * backWays} 注）。4 个胆要同时开进前区，少一个这 {tuo.Count} 注的前区都没有奖。"
        };
    }

    private static double BalancedRed(NumberFrequency f) =>
        0.45 * HotScore(f) + 0.35 * ColdScore(f) + 0.20 * MidOmissionScore(f);

    private static double BalancedBlue(NumberFrequency f) =>
        0.50 * HotScore(f) + 0.50 * ColdScore(f);

    /* ---------------- 快乐8：每天开 20 个，推荐可买的选十 ---------------- */

    private const int Kl8PickCount = 10;
    // 1–80 里均匀抽 10 个，和值均值 405，大约一个标准差内是 347–463。
    private const int Kl8SumLo = 347;
    private const int Kl8SumHi = 463;

    private static List<LottoPick> RecommendKl8(LotteryTypeInfo lt, IReadOnlyList<LotteryDrawRecord> drawsDesc)
    {
        var freq = LotteryMath.ComputeFrequency(drawsDesc, 'R', lt.RedMax);
        var styles = new (string Name, Func<NumberFrequency, double> Score)[]
        {
            ("均衡冷热", f => 0.45 * Kl8Hot(f) + 0.35 * Kl8Cold(f) + 0.20 * Kl8Mid(f)),
            ("热号延续", f => 0.70 * Kl8Hot(f) + 0.20 * Kl8Mid(f) + 0.10 * Kl8Cold(f)),
            ("冷号回补", f => 0.70 * Kl8Cold(f) + 0.20 * Kl8Mid(f) + 0.10 * Kl8Hot(f)),
            ("和值形态", f => 0.40 * Kl8Hot(f) + 0.40 * Kl8Cold(f) + 0.20 * Kl8Mid(f)),
        };

        var picks = new List<LottoPick>();
        var used = new HashSet<string>();
        for (var i = 0; i < styles.Length; i++)
        {
            var (name, scoreFn) = styles[i];
            var reds = PickCombination(Kl8PickCount, lt.RedMax, freq, scoreFn, Kl8SumLo, Kl8SumHi, 5, i * 17 + 3, forceSumFit: i == 3);
            var key = string.Join("-", reds) + "|";
            if (!used.Add(key))
            {
                reds = NudgeReds(reds, lt.RedMax, freq, scoreFn, used, new List<int>());
                key = string.Join("-", reds) + "|";
                if (!used.Add(key)) continue;
            }

            var sum = reds.Sum();
            var odd = reds.Count(x => x % 2 == 1);
            var score = (decimal)Math.Round(reds.Average(n => scoreFn(freq.First(f => f.Number == n))) * 100, 1);
            picks.Add(new LottoPick
            {
                RankNo = picks.Count + 1,
                IsPrimary = picks.Count == 0,
                PlayType = "选十",
                Reds = reds,
                Blues = new List<int>(),
                Score = score,
                SignalDesc = $"{name}：选十和值 {sum}（参考带 {Kl8SumLo}–{Kl8SumHi}），奇偶 {odd}:{reds.Count - odd}"
            });
        }

        picks = picks.OrderByDescending(p => p.Score).ThenBy(p => p.RankNo).Take(4).ToList();
        for (var i = 0; i < picks.Count; i++)
        {
            picks[i].RankNo = i + 1;
            picks[i].IsPrimary = i == 0;
            picks[i].SignalDesc = (i == 0 ? "主推 · " : $"备选{i} · ") + picks[i].SignalDesc;
        }
        return picks;
    }

    private static double Kl8Hot(NumberFrequency f) => Math.Min(1.0, f.CountLast30 / 12.0);
    private static double Kl8Cold(NumberFrequency f) => Math.Min(1.0, f.CurrentOmission / 8.0);
    private static double Kl8Mid(NumberFrequency f)
    {
        var o = f.CurrentOmission;
        if (o is >= 2 and <= 6) return 1.0;
        if (o is >= 1 and <= 10) return 0.6;
        return 0.2;
    }

    /* ---------------- 乐透型：双色球 / 大乐透 ---------------- */

    private static List<LottoPick> RecommendCombo(LotteryTypeInfo lt, IReadOnlyList<LotteryDrawRecord> drawsDesc)
    {
        var redFreq = LotteryMath.ComputeFrequency(drawsDesc, 'R', lt.RedMax);
        var blueFreq = lt.BlueCount > 0
            ? LotteryMath.ComputeFrequency(drawsDesc, 'B', lt.BlueMax)
            : new List<NumberFrequency>();

        var sumDist = LotteryMath.ComputeSumDistribution(drawsDesc.Take(200).ToList());
        var sumLo = PercentileKey(sumDist, 0.20);
        var sumHi = PercentileKey(sumDist, 0.80);
        var preferOdd = (int)Math.Round(lt.RedCount / 2.0);

        // 四种风格：均衡 / 偏热 / 偏冷 / 和值优先
        var styles = new (string Name, Func<NumberFrequency, double> RedScore, Func<NumberFrequency, double> BlueScore)[]
        {
            ("均衡冷热",
                f => 0.45 * HotScore(f) + 0.35 * ColdScore(f) + 0.20 * MidOmissionScore(f),
                f => 0.50 * HotScore(f) + 0.50 * ColdScore(f)),
            ("热号延续",
                f => 0.70 * HotScore(f) + 0.20 * MidOmissionScore(f) + 0.10 * ColdScore(f),
                f => 0.75 * HotScore(f) + 0.25 * ColdScore(f)),
            ("冷号回补",
                f => 0.70 * ColdScore(f) + 0.20 * MidOmissionScore(f) + 0.10 * HotScore(f),
                f => 0.70 * ColdScore(f) + 0.30 * HotScore(f)),
            ("和值形态",
                f => 0.40 * HotScore(f) + 0.40 * ColdScore(f) + 0.20 * MidOmissionScore(f),
                f => 0.50 * HotScore(f) + 0.50 * ColdScore(f)),
        };

        var picks = new List<LottoPick>();
        var used = new HashSet<string>();
        for (var i = 0; i < styles.Length; i++)
        {
            var (name, redScore, blueScore) = styles[i];
            var reds = PickCombination(lt.RedCount, lt.RedMax, redFreq, redScore, sumLo, sumHi, preferOdd, i * 17 + 3, forceSumFit: i == 3);
            var blues = lt.BlueCount > 0
                ? PickCombination(lt.BlueCount, lt.BlueMax, blueFreq, blueScore, null, null, null, i * 31 + 5, forceSumFit: false)
                : new List<int>();

            var key = string.Join("-", reds) + "|" + string.Join("-", blues);
            if (!used.Add(key))
            {
                // 撞车则微调：替换一个红球
                reds = NudgeReds(reds, lt.RedMax, redFreq, redScore, used, blues);
                key = string.Join("-", reds) + "|" + string.Join("-", blues);
                if (!used.Add(key)) continue;
            }

            var sum = reds.Sum();
            var odd = reds.Count(x => x % 2 == 1);
            var score = (decimal)Math.Round(reds.Average(n => redScore(redFreq.First(f => f.Number == n))) * 100, 1);
            picks.Add(new LottoPick
            {
                RankNo = picks.Count + 1,
                IsPrimary = picks.Count == 0,
                PlayType = "单式",
                Reds = reds,
                Blues = blues,
                Score = score,
                SignalDesc = $"{name}：和值 {sum}（参考带 {sumLo}–{sumHi}），奇偶 {odd}:{reds.Count - odd}"
            });
        }

        // 主推取分数最高的一注，其余按分排序为备选
        picks = picks.OrderByDescending(p => p.Score).ThenBy(p => p.RankNo).Take(4).ToList();
        for (var i = 0; i < picks.Count; i++)
        {
            picks[i].RankNo = i + 1;
            picks[i].IsPrimary = i == 0;
            if (i == 0) picks[i].SignalDesc = "主推 · " + picks[i].SignalDesc;
            else picks[i].SignalDesc = $"备选{i} · " + picks[i].SignalDesc;
        }
        return picks;
    }

    private static List<int> PickCombination(
        int count, int max, List<NumberFrequency> freq,
        Func<NumberFrequency, double> scoreFn,
        int? sumLo, int? sumHi, int? preferOdd,
        int salt, bool forceSumFit)
    {
        var ranked = freq.OrderByDescending(scoreFn).ThenBy(f => f.Number).Select(f => f.Number).ToList();
        // 从高分池里交错取样，避免全取前 N
        var pool = ranked.Take(Math.Min(max, Math.Max(count * 3, count + 8))).ToList();
        var pick = new List<int>();
        for (var i = 0; i < pool.Count && pick.Count < count; i++)
        {
            var idx = (i * 3 + salt) % pool.Count;
            var n = pool[idx];
            if (!pick.Contains(n)) pick.Add(n);
        }
        foreach (var n in ranked)
        {
            if (pick.Count >= count) break;
            if (!pick.Contains(n)) pick.Add(n);
        }
        pick = pick.Take(count).OrderBy(x => x).ToList();

        if (forceSumFit && sumLo is not null && sumHi is not null)
            pick = FitSum(pick, max, sumLo.Value, sumHi.Value, salt);

        if (preferOdd is not null)
            pick = FitOdd(pick, max, preferOdd.Value, salt);

        return pick.OrderBy(x => x).ToList();
    }

    private static List<int> FitSum(List<int> pick, int max, int lo, int hi, int salt)
    {
        var cur = pick.ToList();
        for (var round = 0; round < 40; round++)
        {
            var sum = cur.Sum();
            if (sum >= lo && sum <= hi) return cur;
            var i = (round + salt) % cur.Count;
            var delta = sum < lo ? 1 : -1;
            var next = cur[i] + delta;
            if (next < 1 || next > max || cur.Contains(next)) continue;
            cur[i] = next;
            cur = cur.OrderBy(x => x).ToList();
        }
        return cur;
    }

    private static List<int> FitOdd(List<int> pick, int max, int preferOdd, int salt)
    {
        var cur = pick.ToList();
        for (var round = 0; round < 20; round++)
        {
            var odd = cur.Count(x => x % 2 == 1);
            if (odd == preferOdd || Math.Abs(odd - preferOdd) <= 1) return cur;
            var wantOdd = odd < preferOdd;
            var i = (round + salt) % cur.Count;
            if (wantOdd == (cur[i] % 2 == 1)) continue;
            var next = cur[i] + (wantOdd ? 1 : -1);
            if (next < 1 || next > max || cur.Contains(next)) next = cur[i] + (wantOdd ? -1 : 1);
            if (next < 1 || next > max || cur.Contains(next)) continue;
            if (wantOdd != (next % 2 == 1)) continue;
            cur[i] = next;
            cur = cur.OrderBy(x => x).ToList();
        }
        return cur;
    }

    private static List<int> NudgeReds(List<int> reds, int max, List<NumberFrequency> freq,
        Func<NumberFrequency, double> scoreFn, HashSet<string> used, List<int> blues)
    {
        var ranked = freq.OrderByDescending(scoreFn).Select(f => f.Number).ToList();
        foreach (var cand in ranked)
        {
            if (reds.Contains(cand) || cand < 1 || cand > max) continue;
            var next = reds.ToList();
            next[^1] = cand;
            next = next.Distinct().OrderBy(x => x).ToList();
            if (next.Count != reds.Count) continue;
            var key = string.Join("-", next) + "|" + string.Join("-", blues);
            if (used.Contains(key)) continue;
            return next;
        }
        return reds;
    }

    /* ---------------- 数字型：3D / 排三 / 排五 ---------------- */

    private static List<LottoPick> RecommendDigit(LotteryTypeInfo lt, IReadOnlyList<LotteryDrawRecord> drawsDesc)
    {
        var positions = lt.RedCount;
        var posScores = new List<double[]>();
        for (var pos = 0; pos < positions; pos++)
            posScores.Add(ScoreDigitsAtPosition(drawsDesc, pos));

        var want = lt.Code == LottoCodes.Pl5 ? 4 : 3; // 排五 1+3；3D/排三 1+2
        var picks = new List<LottoPick>();
        var used = new HashSet<string>();

        // 风格：各位取最高 / 次高交错 / 冷号位 / 形态组选（仅三位数）
        for (var s = 0; s < 6 && picks.Count < want; s++)
        {
            var digits = new int[positions];
            for (var pos = 0; pos < positions; pos++)
            {
                var order = Enumerable.Range(0, 10)
                    .OrderByDescending(d => posScores[pos][d])
                    .ThenBy(d => d)
                    .ToList();
                digits[pos] = s switch
                {
                    0 => order[0],
                    1 => order[Math.Min(1, order.Count - 1)],
                    2 => order[Math.Min(2 + pos % 2, order.Count - 1)],
                    3 => order[Math.Min(3, order.Count - 1)],
                    _ => order[(s + pos) % order.Count]
                };
            }

            // 第 4 风格：三码组六形态（三位互不相同）
            if (s == 4 && positions == 3)
            {
                var pool = Enumerable.Range(0, 10)
                    .Select(d => (d, score: posScores.Average(p => p[d])))
                    .OrderByDescending(x => x.score)
                    .Select(x => x.d)
                    .ToList();
                digits[0] = pool[0];
                digits[1] = pool[1];
                digits[2] = pool[2];
            }

            var key = string.Join(",", digits);
            if (!used.Add(key)) continue;

            var score = (decimal)Math.Round(
                Enumerable.Range(0, positions).Average(p => posScores[p][digits[p]]) * 100, 1);
            var play = "直选";
            var desc = $"各位冷热加权直选：{string.Join(" ", digits.Select(d => d.ToString()))}";
            if (s == 4 && positions == 3 && digits.Distinct().Count() == 3)
            {
                play = "组选六";
                desc = "组选六形态（三位各异），按位均分冷热";
            }

            picks.Add(new LottoPick
            {
                RankNo = picks.Count + 1,
                IsPrimary = false,
                PlayType = play,
                Reds = digits.ToList(),
                Blues = new List<int>(),
                Score = score,
                SignalDesc = desc
            });
        }

        picks = picks.OrderByDescending(p => p.Score).Take(want).ToList();
        for (var i = 0; i < picks.Count; i++)
        {
            picks[i].RankNo = i + 1;
            picks[i].IsPrimary = i == 0;
            picks[i].SignalDesc = (i == 0 ? "主推 · " : $"备选{i} · ") + picks[i].SignalDesc;
        }
        return picks;
    }

    /// <summary>某位数字 0–9 的综合分（近 30 热 + 遗漏回补）。</summary>
    private static double[] ScoreDigitsAtPosition(IReadOnlyList<LotteryDrawRecord> drawsDesc, int pos)
    {
        var scores = new double[10];
        var asc = drawsDesc.Reverse().ToList();
        var lastHit = Enumerable.Repeat(-1, 10).ToArray();
        var hit30 = new int[10];
        for (var i = 0; i < asc.Count; i++)
        {
            if (pos >= asc[i].Reds.Count) continue;
            var d = asc[i].Reds[pos];
            if (d is < 0 or > 9) continue;
            lastHit[d] = i;
            if (i >= asc.Count - 30) hit30[d]++;
        }
        for (var d = 0; d <= 9; d++)
        {
            var omit = lastHit[d] < 0 ? asc.Count : asc.Count - 1 - lastHit[d];
            var hot = hit30[d] / 30.0;
            var cold = Math.Min(1.0, omit / 12.0);
            scores[d] = 0.55 * hot + 0.45 * cold;
        }
        return scores;
    }

    /* ---------------- 工具 ---------------- */

    private static double HotScore(NumberFrequency f) => Math.Min(1.0, f.CountLast30 / 8.0);
    private static double ColdScore(NumberFrequency f) => Math.Min(1.0, f.CurrentOmission / 12.0);
    private static double MidOmissionScore(NumberFrequency f)
    {
        // 遗漏适中给分，极端过长略降
        var o = f.CurrentOmission;
        if (o is >= 3 and <= 10) return 1.0;
        if (o is >= 1 and <= 15) return 0.6;
        return 0.2;
    }

    private static int PercentileKey(Dictionary<int, int> dist, double p)
    {
        if (dist.Count == 0) return 0;
        var total = dist.Values.Sum();
        var need = total * p;
        var acc = 0;
        foreach (var kv in dist.OrderBy(x => x.Key))
        {
            acc += kv.Value;
            if (acc >= need) return kv.Key;
        }
        return dist.Keys.Max();
    }

    /// <summary>已废弃：禁止用最新开奖 +1 猜测目标期。请用 <see cref="LottoIssue.TryResolveNextSalesIssue"/>。</summary>
    [Obsolete("Do not invent next issue by +1; official sales-issue rules are unverified in-repo.")]
    public static string NextIssue(string issue)
    {
        if (LottoIssue.TryResolveNextSalesIssue("", issue, out var next, out _))
            return next;
        return "";
    }
}
