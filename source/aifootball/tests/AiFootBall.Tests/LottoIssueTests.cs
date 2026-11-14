using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class LottoIssueTests
{
    [Fact]
    public void User_Issue_Requires_NonEmpty_Digits()
    {
        Assert.False(LottoIssue.TryNormalizeUserIssue(null, out _, out var e1));
        Assert.Contains("请填写", e1);

        Assert.False(LottoIssue.TryNormalizeUserIssue("  ", out _, out _));
        Assert.False(LottoIssue.TryNormalizeUserIssue("abc", out _, out var e2));
        Assert.Contains("格式无效", e2);

        Assert.True(LottoIssue.TryNormalizeUserIssue("2026122", out var issue, out _));
        Assert.Equal("2026122", issue);
    }

    [Theory]
    [InlineData("ssq", "2026154")]
    [InlineData("dlt", "2026365")]
    [InlineData("fc3d", "2026001")]
    public void Next_Sales_Issue_Not_Guessed_Without_Verified_Rules(string code, string based)
    {
        Assert.False(LottoIssue.TryResolveNextSalesIssue(code, based, out var next, out var reason));
        Assert.Equal("", next);
        Assert.Contains("尚未在本仓库核实", reason);
    }

    [Fact]
    public void Obsolete_NextIssue_Does_Not_Invent_Plus_One()
#pragma warning disable CS0618
    {
        Assert.Equal("", LottoRecommendEngine.NextIssue("2026122"));
    }
#pragma warning restore CS0618
}
