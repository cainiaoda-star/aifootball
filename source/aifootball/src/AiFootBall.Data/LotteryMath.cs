using AiFootBall.Core.Models;

namespace AiFootBall.Data;

public sealed class DrawStat
{
    public int? RedSum { get; init; }
    public int? RedSpan { get; init; }
    public int? OddCount { get; init; }
    public int? BigCount { get; init; }
}

/// <summary>开奖号码衍生指标计算。</summary>
public static class LotteryMath
{
    public static DrawStat Compute(LotteryTypeInfo lt, LotteryDrawRecord draw)
    {
        var reds = draw.Reds;
        if (reds.Count == 0)
            return new DrawStat();

        var sum = reds.Sum();
        var span = reds.Max() - reds.Min();
        var odd = reds.Count(x => x % 2 == 1);

        // 数字型彩票（3D/排列三/七星彩）无大小号概念
        int? big = lt.RedMax > 9 ? reds.Count(x => x > lt.RedMax / 2.0) : null;

        return new DrawStat { RedSum = sum, RedSpan = span, OddCount = odd, BigCount = big };
    }

    /// <summary>按球类型计算频率、平均间隔、当前遗漏与最大遗漏。</summary>
    public static List<NumberFrequency> ComputeFrequency(IReadOnlyList<LotteryDrawRecord> drawsDesc,
        char ballType, int numberMax)
    {
        // 转为正序（旧 -> 新），遗漏从最新一期往回算
        var asc = drawsDesc.Reverse().ToList();
        var result = new List<NumberFrequency>(numberMax);

        for (var n = 1; n <= numberMax; n++)
        {
            var hitIndexes = new List<int>();
            for (var i = 0; i < asc.Count; i++)
            {
                var balls = ballType == 'B' ? asc[i].Blues : asc[i].Reds;
                if (balls.Contains(n)) hitIndexes.Add(i);
            }

            var total = hitIndexes.Count;
            var lastIndex = total > 0 ? hitIndexes[^1] : -1;

            // 当前遗漏：距今多少期未出现（0 表示最新一期刚开出）
            var currentOmission = asc.Count - 1 - lastIndex;

            // 最大遗漏：历史最长连续未出现期数
            var maxOmission = total > 0 ? hitIndexes[0] : asc.Count;
            for (var i = 1; i < hitIndexes.Count; i++)
                maxOmission = Math.Max(maxOmission, hitIndexes[i] - hitIndexes[i - 1] - 1);
            maxOmission = Math.Max(maxOmission, currentOmission);

            var avgInterval = total > 1
                ? Math.Round((double)(hitIndexes[^1] - hitIndexes[0]) / (total - 1), 2)
                : 0;

            var countLast30 = hitIndexes.Count(i => i >= asc.Count - 30);
            var countLast100 = hitIndexes.Count(i => i >= asc.Count - 100);

            result.Add(new NumberFrequency
            {
                Number = n,
                BallType = ballType.ToString(),
                TotalCount = total,
                CountLast30 = countLast30,
                CountLast100 = countLast100,
                AvgInterval = avgInterval,
                CurrentOmission = currentOmission,
                MaxOmission = maxOmission
            });
        }

        return result;
    }

    /// <summary>和值分布</summary>
    public static Dictionary<int, int> ComputeSumDistribution(IReadOnlyList<LotteryDrawRecord> draws)
        => draws.GroupBy(d => d.Reds.Sum()).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>奇偶比分布（如 3:2）</summary>
    public static Dictionary<string, int> ComputeOddEvenDistribution(IReadOnlyList<LotteryDrawRecord> draws)
        => draws.GroupBy(d =>
            {
                var odd = d.Reds.Count(x => x % 2 == 1);
                return $"{odd}:{d.Reds.Count - odd}";
            })
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>连号期数占比</summary>
    public static double ConsecutiveRate(IReadOnlyList<LotteryDrawRecord> draws)
    {
        if (draws.Count == 0) return 0;
        var hit = draws.Count(d =>
        {
            var sorted = d.Reds.Distinct().OrderBy(x => x).ToList();
            return sorted.Zip(sorted.Skip(1), (a, b) => b - a).Any(diff => diff == 1);
        });
        return Math.Round((double)hit / draws.Count * 100, 2);
    }

    /// <summary>与上一期的重号个数序列</summary>
    public static List<int> RepeatCountSeries(IReadOnlyList<LotteryDrawRecord> drawsDesc)
    {
        var asc = drawsDesc.Reverse().ToList();
        var list = new List<int>();
        for (var i = 1; i < asc.Count; i++)
            list.Add(asc[i].Reds.Intersect(asc[i - 1].Reds).Count());
        return list;
    }
}
