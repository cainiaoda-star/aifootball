namespace AiFootBall.Core.Models;

/// <summary>
/// 用去水后的胜平负概率和大小球盘，反推两队进球期望（泊松），再算任意让球数下的让胜/让平/让负。
/// 北单只卖让球胜平负，方向和概率都要按开出的让球数算。
/// </summary>
public static class HandicapMath
{
    private const int MaxGoals = 10;
    private const decimal DefaultTotal = 2.6m;

    public static (double Home, double Away)? FitLambdas(decimal pHome, decimal pAway, decimal? totalLine)
    {
        if (pHome <= 0 || pAway <= 0 || pHome + pAway >= 1) return null;
        var total = (double)(totalLine is > 0.5m and < 8m ? totalLine.Value : DefaultTotal);
        double bestErr = double.MaxValue, bestH = total / 2;
        for (var lh = 0.05; lh <= total - 0.05; lh += 0.01)
        {
            var (h, _, a) = Outcome(lh, total - lh, 0);
            var err = Math.Pow(h - (double)pHome, 2) + Math.Pow(a - (double)pAway, 2);
            if (err < bestErr) { bestErr = err; bestH = lh; }
        }
        return (bestH, total - bestH);
    }

    /// <summary>主队加上 handicap 后的胜/平/负概率（0–1）。handicap 是北单、竞彩写法，主让 2 球为 -2。</summary>
    public static (double Win, double Draw, double Loss) Outcome(double lambdaHome, double lambdaAway, decimal handicap)
    {
        var hcp = (double)handicap;
        double win = 0, draw = 0, loss = 0;
        for (var h = 0; h <= MaxGoals; h++)
        {
            var ph = Poisson(h, lambdaHome);
            for (var a = 0; a <= MaxGoals; a++)
            {
                var p = ph * Poisson(a, lambdaAway);
                var diff = h + hcp - a;
                if (diff > 0.001) win += p;
                else if (diff < -0.001) loss += p;
                else draw += p;
            }
        }
        var sum = win + draw + loss;
        return sum > 0 ? (win / sum, draw / sum, loss / sum) : (0, 0, 0);
    }

    /// <summary>欧赔三项 + 大小球盘 → 该让球数下的让胜/让平/让负概率（0–1）。</summary>
    public static (decimal Win, decimal Draw, decimal Loss)? FromOdds(
        decimal? home, decimal? draw, decimal? away, decimal? totalLine, decimal handicap)
    {
        var p = HitRateMath.Devig(home, draw, away);
        if (p is null) return null;
        var l = FitLambdas(p.Value.Home, p.Value.Away, totalLine);
        if (l is null) return null;
        var o = Outcome(l.Value.Home, l.Value.Away, handicap);
        return ((decimal)o.Win, (decimal)o.Draw, (decimal)o.Loss);
    }

    /// <summary>比分按让球数折成让胜(3)/让平(1)/让负(0)。</summary>
    public static char Result(int home, int away, decimal handicap)
    {
        var diff = home + handicap - away;
        if (diff > 0.001m) return '3';
        if (diff < -0.001m) return '0';
        return '1';
    }

    private static double Poisson(int k, double lambda)
    {
        if (lambda < 1e-9) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }
}
