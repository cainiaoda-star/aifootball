namespace AiFootBall.Core.Models;

/// <summary>已结算命中率的区间，以及单场概率用到的去水、收缩和连乘。</summary>
public static class HitRateMath
{
    public readonly record struct WilsonInterval(double Point, double Low, double High);

    /// <summary>Wilson 区间。样本少的时候比「中了几场除以总场」宽，避免把 6 中 7 写成 85.7%。</summary>
    public static WilsonInterval Wilson(int hits, int total, double z = 1.96)
    {
        if (total <= 0 || hits < 0) return new WilsonInterval(0, 0, 0);
        if (hits > total) hits = total;
        var n = (double)total;
        var p = hits / n;
        var z2 = z * z;
        var den = 1 + z2 / n;
        var centre = p + z2 / (2 * n);
        var margin = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n));
        var low = Math.Clamp((centre - margin) / den, 0, 1);
        var high = Math.Clamp((centre + margin) / den, 0, 1);
        return new WilsonInterval(p, low, high);
    }

    /// <summary>乘法去水。三项赔率都要大于 1，结果加总为 1。</summary>
    public static (decimal Home, decimal Draw, decimal Away)? Devig(decimal? home, decimal? draw, decimal? away)
    {
        if (home is not > 1 || draw is not > 1 || away is not > 1) return null;
        var h = 1m / home.Value;
        var d = 1m / draw.Value;
        var a = 1m / away.Value;
        var sum = h + d + a;
        if (sum <= 0) return null;
        return (h / sum, d / sum, a / sum);
    }

    /// <summary>
    /// Beta-Binomial 后验均值。prior 是这一场的市场或模型概率，strength 是先验当多少场。
    /// 已结算场次为 0 时结果就是 prior。
    /// </summary>
    public static decimal Shrink(int hits, int total, decimal prior, decimal strength = 80m)
    {
        if (total < 0) total = 0;
        if (hits < 0) hits = 0;
        if (hits > total) hits = total;
        if (strength < 0) strength = 0;
        var p = Math.Clamp(prior, 0.01m, 0.99m);
        return (hits + p * strength) / (total + strength);
    }

    /// <summary>胜负彩或串关：单场概率连乘。空列表返回 null。</summary>
    public static decimal? Parlay(IEnumerable<decimal> probabilities)
    {
        decimal product = 1m;
        var any = false;
        foreach (var raw in probabilities)
        {
            any = true;
            product *= Math.Clamp(raw, 0.001m, 0.999m);
        }
        return any ? product : null;
    }

    public sealed class HitBucketCount
    {
        public string Side { get; set; } = "";
        public string Band { get; set; } = "";
        public int Hits { get; set; }
        public int Total { get; set; }
    }

    public static string OddsBand(decimal odds)
    {
        // 拆开 lt2：超低水热门与 1.4~2.0 命中率差很大，混桶会误杀小降好场
        if (odds < 1.40m) return "lt14";
        if (odds < 2m) return "14to2";
        if (odds < 3m) return "2to3";
        if (odds < 5m) return "3to5";
        return "ge5";
    }

    public static double Brier(IReadOnlyList<(double Home, double Draw, double Away, char Result)> rows)
    {
        if (rows.Count == 0) return 1;
        double sum = 0;
        foreach (var (home, draw, away, result) in rows)
        {
            var yh = result == '3' ? 1d : 0d;
            var yd = result == '1' ? 1d : 0d;
            var ya = result == '0' ? 1d : 0d;
            sum += (home - yh) * (home - yh) + (draw - yd) * (draw - yd) + (away - ya) * (away - ya);
        }
        return sum / rows.Count;
    }
}
