using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 三个概率空间各自的守恒。期望值在本文件里用泊松公式手算，不读现行快照测试，也不调用被测函数来生成期望。
/// 总进球 7+ = 双方进球之和 ≥ 7。比分「其它」= 该方向里没有单列的比分。半全场是九格结果，没有第七档。
/// </summary>
public class ScoreSpaceTests
{
    const double Lh = 2;
    const double La = 1;

    [Fact]
    public void TotalGoal_SevenPlus_IsTheMatchTotal_IncludingTheOutsideTail()
    {
        var grid = Box(Lh, La, rho: 0);
        var bands = MilkPickEngine.TotalGoalBands(grid, Lh, La);

        var (mass, insideGe7, rawAtMost6) = Split(grid);
        var outside = 1 - Cdf(6, Lh) * Cdf(6, La);
        var z = mass + outside;
        var totalGe7 = (insideGe7 + outside) / z;

        Assert.Equal(1d, (double)bands.Sum(), 8);
        Assert.Equal(Cell(0, 0, Lh, La, 0) / z, (double)bands[0], 8);
        Assert.Equal(totalGe7, (double)bands[7], 8);
        // λh=2、λa=1 时，手算 P(总进球≥7) = 0.033509。单队主队≥7 只有 0.004534，旧实现把尾部归一掉之后是 0.029026。
        Assert.Equal(0.033509, (double)bands[7], 5);
        Assert.NotEqual(Math.Round(1 - Cdf(6, Lh), 6), Math.Round((double)bands[7], 6));
        Assert.NotEqual(Math.Round(insideGe7 / mass, 6), Math.Round((double)bands[7], 6));
        var conditional = MilkPickEngine.GoalBands(grid, mass);
        Assert.NotEqual(Math.Round((double)conditional[7], 6), Math.Round((double)bands[7], 6));
        Assert.True(rawAtMost6 > 0);
    }

    [Fact]
    public void HalfFull_EachOfNineCells_MatchesTheIndependentResultSum()
    {
        var joint = MilkPickEngine.HalfFullJointFromLambdas(4.5, 4.5);
        Assert.NotNull(joint);
        var reference = ReferenceJoint(4.5, 4.5, cap: 16);
        double sum = 0;
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            sum += (double)joint![i, j];
            Assert.Equal(reference[i, j], (double)joint[i, j], 4);
        }
        Assert.Equal(1d, sum, 8);
        Assert.Equal(9, JcPlayDomain.Bq.Count);
    }

    [Fact]
    public void TotalGoal_DixonColes_NormalizesOnTheFullSupport()
    {
        const double rho = 0.01;
        var grid = Box(Lh, La, rho);
        var bands = MilkPickEngine.TotalGoalBands(grid, Lh, La);

        var (mass, insideGe7, _) = Split(grid);
        var outside = 1 - Cdf(6, Lh) * Cdf(6, La);
        var z = mass + outside;

        Assert.Equal(Cell(0, 0, Lh, La, rho) / z, (double)bands[0], 8);
        Assert.Equal((insideGe7 + outside) / z, (double)bands[7], 8);
        Assert.Equal(1d, (double)bands.Sum(), 8);
    }

    [Fact]
    public void Scorebook_Other_IsUnlistedScores_NotTheSevenPlusBucket()
    {
        var grid = Box(Lh, La, rho: 0);
        var book = MilkPickEngine.Scorebook(grid, JcPlayDomain.Bf, Lh, La);
        var (mass, _, _) = Split(grid);
        var outside = OutsideByResult(Lh, La, 30);
        var z = mass + outside.Home + outside.Draw + outside.Away;

        var listedHome = JcPlayDomain.Bf.Where(c => c.Contains(':') && HomeSide(c) == '3').ToHashSet();
        Assert.Contains("5:2", listedHome);
        Assert.DoesNotContain("6:0", listedHome);

        Assert.Equal(Cell(5, 2, Lh, La, 0) / z, (double)book["5:2"], 8);

        var otherHome = Unlisted(grid, '3') + outside.Home;
        var otherDraw = Unlisted(grid, '1') + outside.Draw;
        var otherAway = Unlisted(grid, '0') + outside.Away;
        Assert.Equal(otherHome / z, (double)book["胜其它"], 8);
        Assert.Equal(otherDraw / z, (double)book["平其它"], 8);
        Assert.Equal(otherAway / z, (double)book["负其它"], 8);
        Assert.True((double)book["胜其它"] > Cell(6, 0, Lh, La, 0) / z);
        Assert.True((double)book["负其它"] > Cell(0, 6, Lh, La, 0) / z);
        Assert.DoesNotContain("6:0", book.Keys);
        Assert.DoesNotContain("0:6", book.Keys);

        var bands = MilkPickEngine.TotalGoalBands(grid, Lh, La);
        Assert.True((double)bands[6] + 1e-9 >= Cell(6, 0, Lh, La, 0) / z);
        Assert.NotEqual(Math.Round((double)book["胜其它"], 6), Math.Round((double)bands[7], 6));

        var sum = book.Values.Sum(v => (double)v);
        Assert.Equal(1d, sum, 8);
    }

    [Fact]
    public void HalfFull_NineCellsSumToOne_AndASevenGoalHalfStaysAWin()
    {
        var joint = MilkPickEngine.HalfFullJointFromLambdas(4.5, 4.5);
        Assert.NotNull(joint);
        double got = 0;
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            got += (double)joint![i, j];
        Assert.Equal(1d, got, 8);

        var reference = ReferenceJoint(4.5, 4.5, cap: 16);
        double max = 0;
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            max = Math.Max(max, Math.Abs((double)joint[i, j] - reference[i, j]));
        // 半场单队 7 球仍是该半场的胜或负，不能在截断到 6 之后摊进九格。
        Assert.True(max < 1e-4, $"half-full max abs error {max}");
    }

    static List<(int H, int A, double P)> Box(double lh, double la, double rho)
    {
        var cells = new List<(int H, int A, double P)>(49);
        for (var h = 0; h <= 6; h++)
        for (var a = 0; a <= 6; a++)
            cells.Add((h, a, Tau(h, a, lh, la, rho) * Pois(h, lh) * Pois(a, la)));
        return cells;
    }

    static (double Mass, double InsideGe7, double AtMost6) Split(List<(int H, int A, double P)> grid)
    {
        double mass = 0, ge7 = 0, low = 0;
        foreach (var c in grid)
        {
            mass += c.P;
            if (c.H + c.A >= 7) ge7 += c.P;
            else low += c.P;
        }
        return (mass, ge7, low);
    }

    static double Unlisted(List<(int H, int A, double P)> grid, char side)
    {
        var listed = JcPlayDomain.Bf.Where(c => c.Contains(':')).ToHashSet(StringComparer.Ordinal);
        double sum = 0;
        foreach (var c in grid)
        {
            var s = c.H > c.A ? '3' : c.H == c.A ? '1' : '0';
            if (s != side) continue;
            if (listed.Contains($"{c.H}:{c.A}")) continue;
            sum += c.P;
        }
        return sum;
    }

    static char HomeSide(string code)
    {
        var p = code.Split(':');
        var h = int.Parse(p[0]);
        var a = int.Parse(p[1]);
        return h > a ? '3' : h == a ? '1' : '0';
    }

    static double[,] ReferenceJoint(double lh, double la, int cap)
    {
        var first = Margin(lh * MilkPickEngine.HalfGoalShare, la * MilkPickEngine.HalfGoalShare, cap);
        var second = Margin(lh * (1 - MilkPickEngine.HalfGoalShare), la * (1 - MilkPickEngine.HalfGoalShare), cap);
        var mass1 = first.Sum(c => c.P);
        var mass2 = second.Sum(c => c.P);
        var joint = new double[3, 3];
        foreach (var h1 in first)
        foreach (var h2 in second)
        {
            var ht = h1.H > h1.A ? 0 : h1.H == h1.A ? 1 : 2;
            var fh = h1.H + h2.H;
            var fa = h1.A + h2.A;
            var ft = fh > fa ? 0 : fh == fa ? 1 : 2;
            joint[ht, ft] += h1.P * h2.P;
        }
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            joint[i, j] /= mass1 * mass2;
        return joint;
    }

    static List<(int H, int A, double P)> Margin(double lh, double la, int cap)
    {
        var cells = new List<(int H, int A, double P)>();
        for (var h = 0; h <= cap; h++)
        for (var a = 0; a <= cap; a++)
            cells.Add((h, a, Pois(h, lh) * Pois(a, la)));
        return cells;
    }

    static (double Home, double Draw, double Away) OutsideByResult(double lh, double la, int cap)
    {
        double home = 0, draw = 0, away = 0;
        for (var h = 0; h <= cap; h++)
        for (var a = 0; a <= cap; a++)
        {
            if (h <= 6 && a <= 6) continue;
            var p = Pois(h, lh) * Pois(a, la);
            if (h > a) home += p;
            else if (h == a) draw += p;
            else away += p;
        }
        return (home, draw, away);
    }

    static double Cell(int h, int a, double lh, double la, double rho)
        => Tau(h, a, lh, la, rho) * Pois(h, lh) * Pois(a, la);

    static double Tau(int x, int y, double lh, double la, double rho) => (x, y) switch
    {
        (0, 0) => 1 - lh * la * rho,
        (1, 0) => 1 + la * rho,
        (0, 1) => 1 + lh * rho,
        (1, 1) => 1 - rho,
        _ => 1d
    };

    static double Cdf(int k, double lambda)
    {
        double s = 0;
        for (var i = 0; i <= k; i++) s += Pois(i, lambda);
        return s;
    }

    static double Pois(int k, double lambda)
    {
        if (k < 0) return 0;
        if (lambda < 1e-6) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }
}
