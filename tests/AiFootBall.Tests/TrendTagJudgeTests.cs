using AiFootBall.Data;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 走向标签的验收口径：标签方向 vs 赛果。
///
/// 这组测试守的是「页面上那个 ✓/✗ 到底怎么来的」——
/// 一旦映射写错，页面会把市场判错的日子标成对，复盘就失去意义。
/// </summary>
public class TrendTagJudgeTests
{
    private static MatchTrendRow Row(string? tag, string playType, byte status, string? result) => new()
    {
        MatchId = 1,
        PlayType = playType,
        TrendTag = tag,
        MatchStatus = status,
        ResultSpf = result
    };

    [Theory]
    [InlineData("主胜走强", "3")]
    [InlineData("客胜走强", "0")]
    [InlineData("平局走强", "1")]
    public void SideOfTag_映射到赛果代码(string tag, string side)
    {
        Assert.Equal(side, OddsCaptureRepository.SideOfTag(tag));
    }

    [Theory]
    [InlineData("无明显迁移")]
    [InlineData("热门档切换 1:1→2:1")]
    [InlineData(null)]
    [InlineData("")]
    public void SideOfTag_无方向文案返回空(string? tag)
    {
        Assert.Null(OddsCaptureRepository.SideOfTag(tag));
    }

    [Fact]
    public void 标签方向与赛果一致算命中()
    {
        // 11-05 朗斯 1:9 科莫：客胜走强，赛果客胜
        var hit = OddsCaptureRepository.JudgeTag(Row("客胜走强", "SPF", 3, "0"));
        Assert.True(hit);
    }

    [Fact]
    public void 标签方向与赛果相反算落空()
    {
        // 11-05 多特 2:1 贝蒂斯：标签平局走强，赛果主胜
        var miss = OddsCaptureRepository.JudgeTag(Row("平局走强", "SPF", 3, "3"));
        Assert.False(miss);
    }

    [Fact]
    public void 未完赛不判定()
    {
        Assert.Null(OddsCaptureRepository.JudgeTag(Row("主胜走强", "SPF", 0, null)));
    }

    [Fact]
    public void 让球玩法的平局走强不能套胜平负赛果()
    {
        // 「让球」玩法的平局走强指的是让球平，跟 ResultSpf 不是一回事，必须不判定
        Assert.Null(OddsCaptureRepository.JudgeTag(Row("平局走强", "NSPF", 3, "3")));
    }

    [Fact]
    public void 标签是热门档切换时不判定()
    {
        Assert.Null(OddsCaptureRepository.JudgeTag(Row("热门档切换 1:1→2:1", "SPF", 3, "3")));
    }
}
