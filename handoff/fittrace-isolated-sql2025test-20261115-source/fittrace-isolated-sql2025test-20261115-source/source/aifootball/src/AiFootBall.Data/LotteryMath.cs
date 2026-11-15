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
        var reds = ProjectReds(draw.Reds, LottoHitMath.OfficialDigitCount(lt.Code));
        if (reds.Count == 0)
            return new DrawStat();

        var sum = reds.Sum();
        var span = reds.Max() - reds.Min();
        var odd = reds.Count(x => x % 2 == 1);

        int? big = LottoCodes.IsDigitGame(lt.Code) ? null
            : lt.RedMax > 9 ? reds.Count(x => x > lt.RedMax / 2.0) : null;

        return new DrawStat { RedSum = sum, RedSpan = span, OddCount = odd, BigCount = big };
    }

    /// <summary>
    /// 按球类型计算频率、平均间隔、当前遗漏与最大遗漏。
    /// 计数定义（本轮固定）：跨位置合并后，每期该号是否出现（Contains）；
    /// 同一期内重复出现（如组三 1,1,2）对数字 1 <b>只计 1 次</b>。不是分位频率，也不是出现率百分比。
    /// </summary>
    public static List<NumberFrequency> ComputeFrequency(IReadOnlyList<LotteryDrawRecord> drawsDesc,
        char ballType, int numberMax, int numberMin = 1, int? officialRedTake = null)
    {
        var asc = drawsDesc.Reverse().ToList();
        var result = new List<NumberFrequency>(Math.Max(0, numberMax - numberMin + 1));

        for (var n = numberMin; n <= numberMax; n++)
        {
            var hitIndexes = new List<int>();
            for (var i = 0; i < asc.Count; i++)
            {
                var balls = ballType == 'B'
                    ? asc[i].Blues
                    : ProjectReds(asc[i].Reds, officialRedTake);
                if (balls.Contains(n)) hitIndexes.Add(i);
            }

            var total = hitIndexes.Count;
            var lastIndex = total > 0 ? hitIndexes[^1] : -1;
            var currentOmission = asc.Count - 1 - lastIndex;

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

    public static IReadOnlyList<int> ProjectReds(IReadOnlyList<int> reds, int? officialRedTake)
    {
        if (officialRedTake is null || officialRedTake.Value <= 0)
            return reds;
        if (officialRedTake.Value >= reds.Count)
            return reds;
        return reds.Take(officialRedTake.Value).ToList();
    }

    public static List<LotteryDrawRecord> ProjectDraws(IReadOnlyList<LotteryDrawRecord> draws, int? officialRedTake)
    {
        if (officialRedTake is null || officialRedTake.Value <= 0)
            return draws.ToList();
        return draws.Select(d => new LotteryDrawRecord
        {
            LotteryCode = d.LotteryCode,
            Issue = d.Issue,
            DrawDate = d.DrawDate,
            Reds = ProjectReds(d.Reds, officialRedTake).ToList(),
            Blues = d.Blues,
            SalesAmount = d.SalesAmount,
            PoolAmount = d.PoolAmount,
            Prizes = d.Prizes,
            Source = d.Source
        }).ToList();
    }

    /// <summary>
    /// 按彩种代码选择频率/分布参数：数字型用 LottoCodes.IsDigitGame（含 qxc），含 0，截取官方开奖位。
    /// </summary>
    public static (int NumberMin, int NumberMax, int? OfficialRedTake, string CountDefinition)
        FrequencyParams(string lotteryCode, LotteryTypeInfo lt, char ballType)
    {
        const string def =
            "per_draw_presence_across_positions：跨位置合并后统计每期是否出现该号；重号当期只计 1 次。非分位、非出现率%。";
        var code = LottoCodes.Normalize(lotteryCode);
        if (ballType == 'B')
            return (1, lt.BlueMax, null, def);

        if (LottoCodes.IsDigitGame(code))
        {
            var take = LottoHitMath.OfficialDigitCount(code);
            return (0, 9, take > 0 ? take : null, def);
        }

        return (1, lt.RedMax, null, def);
    }

    public static Dictionary<int, int> ComputeSumDistribution(IReadOnlyList<LotteryDrawRecord> draws)
        => draws.GroupBy(d => d.Reds.Sum()).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    public static Dictionary<string, int> ComputeOddEvenDistribution(IReadOnlyList<LotteryDrawRecord> draws)
        => draws.GroupBy(d =>
            {
                var odd = d.Reds.Count(x => x % 2 == 1);
                return $"{odd}:{d.Reds.Count - odd}";
            })
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

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

    public static List<int> RepeatCountSeries(IReadOnlyList<LotteryDrawRecord> drawsDesc)
    {
        var asc = drawsDesc.Reverse().ToList();
        var list = new List<int>();
        for (var i = 1; i < asc.Count; i++)
            list.Add(asc[i].Reds.Intersect(asc[i - 1].Reds).Count());
        return list;
    }
}
