using AiFootBall.Core.Models;
using AiFootBall.Data;
using Xunit;

namespace AiFootBall.Tests;

public class LotteryMathFrequencyTests
{
    [Fact]
    public void Digit_Frequency_Includes_Zero_And_Truncates_Junk()
    {
        var lt = new LotteryTypeInfo { Code = LottoCodes.Fc3d, RedCount = 3, RedMax = 9, BlueCount = 0 };
        var draws = new List<LotteryDrawRecord>
        {
            // 官方 0,1,2；尾部脏号 9 不应计入频率
            Draw(LottoCodes.Fc3d, "1", [0, 1, 2, 9, 9, 9]),
            Draw(LottoCodes.Fc3d, "2", [0, 3, 4]),
            Draw(LottoCodes.Fc3d, "3", [5, 6, 7]),
        };

        var (min, max, take, def) = LotteryMath.FrequencyParams(LottoCodes.Fc3d, lt, 'R');
        Assert.Equal(0, min);
        Assert.Equal(9, max);
        Assert.Equal(3, take);
        Assert.Contains("重号当期只计 1 次", def);

        var freq = LotteryMath.ComputeFrequency(draws, 'R', max, min, take);
        Assert.Equal(10, freq.Count);
        Assert.Contains(freq, f => f.Number == 0);
        Assert.Equal(2, freq.Single(f => f.Number == 0).TotalCount);
        // 脏号 9 只在第一期尾部，截断后不应计数
        Assert.Equal(0, freq.Single(f => f.Number == 9).TotalCount);
    }

    [Fact]
    public void Duplicate_In_Same_Draw_Counts_Once()
    {
        var draws = new List<LotteryDrawRecord>
        {
            Draw(LottoCodes.Fc3d, "1", [1, 1, 2]),
        };
        var freq = LotteryMath.ComputeFrequency(draws, 'R', 9, 0, 3);
        Assert.Equal(1, freq.Single(f => f.Number == 1).TotalCount);
        Assert.Equal(1, freq.Single(f => f.Number == 2).TotalCount);
    }

    [Fact]
    public void Ssq_Frequency_Starts_At_One_Not_Zero()
    {
        var lt = new LotteryTypeInfo { Code = LottoCodes.Ssq, RedCount = 6, RedMax = 33, BlueCount = 1, BlueMax = 16 };
        var (min, max, take, _) = LotteryMath.FrequencyParams(LottoCodes.Ssq, lt, 'R');
        Assert.Equal(1, min);
        Assert.Equal(33, max);
        Assert.Null(take);
    }

    [Fact]
    public void Qxc_Frequency_Analysis_Not_Supported()
    {
        Assert.False(LottoCodes.SupportsFrequencyAnalysis("qxc"));
        Assert.False(new LotteryTypeInfo { Code = "qxc", RedMax = 9, BlueCount = 0 }.IsDigitGame);
    }

    [Fact]
    public void Analysis_Projection_Same_As_Frequency_For_Digit_Junk()
    {
        var draws = new List<LotteryDrawRecord>
        {
            new()
            {
                LotteryCode = LottoCodes.Fc3d, Issue = "1", DrawDate = new DateOnly(2026, 1, 1),
                Reds = [1, 2, 3, 9, 9, 9], Blues = Array.Empty<int>(), Source = "t"
            }
        };
        var projected = LotteryMath.ProjectDraws(draws, 3);
        Assert.Equal(new[] { 1, 2, 3 }, projected[0].Reds);
        Assert.Equal(6, LotteryMath.ComputeSumDistribution(projected).Keys.Single());
    }

    private static LotteryDrawRecord Draw(string code, string issue, int[] reds) => new()
    {
        LotteryCode = code,
        Issue = issue,
        DrawDate = new DateOnly(2026, 1, 1),
        Reds = reds,
        Blues = Array.Empty<int>(),
        Source = "test"
    };
}
