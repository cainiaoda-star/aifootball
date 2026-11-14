using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>服务/展示边界：无 DB、无 Host。</summary>
public class LottoBoundaryTests
{
    [Fact]
    public void Save_Result_Reports_Zero_When_All_Unbound()
    {
        var plans = new List<LottoPlanSet>
        {
            new()
            {
                LotteryCode = "ssq", LotteryName = "双色球", TargetIssue = "",
                Picks = { new LottoPick { Reds = [1, 2, 3, 4, 5, 6] }, new LottoPick() }
            }
        };
        var result = LottoBoardPresentation.BuildSaveResult(plans, []);
        Assert.Equal(0, result.SavedPickCount);
        Assert.Contains("实际保存 0 注", result.Message);
    }

    [Fact]
    public void Historical_Board_Label_Not_Today_Current()
    {
        var label = LottoBoardPresentation.StatusLabel(
            LottoBoardPresentation.KindHistorical, "2026100", new DateTime(2026, 10, 1, 12, 0, 0));
        Assert.Contains("历史推荐", label);
        Assert.Contains("非今日当期冒充", label);
    }

    [Fact]
    public void Partial_Hit_Review_Is_PrizeUndetermined_With_Match_Marks()
    {
        var rows = new List<LottoRecommendationRecord>
        {
            new()
            {
                LotteryCode = LottoCodes.Ssq, TargetIssue = "2026001", RankNo = 1,
                PlayType = "单式", RedNumbers = "01,05,09,12,18,33", BlueNumbers = "07"
            }
        };
        var draw = new LotteryDrawRecord
        {
            LotteryCode = LottoCodes.Ssq, Issue = "2026001",
            DrawDate = new DateOnly(2026, 1, 1),
            Reds = [5, 9, 12, 20, 21, 33], Blues = [16], Source = "t"
        };
        var issue = LottoRecommendationService.ReviewIssue(LottoCodes.Ssq, "2026001", rows, draw);
        var picks = ((IEnumerable<object>)issue.GetType().GetProperty("picks")!.GetValue(issue)!).Cast<object>().ToList();
        var p0 = picks[0];
        Assert.Equal("prize_undetermined", (string)p0.GetType().GetProperty("status")!.GetValue(p0)!);
        Assert.False((bool)p0.GetType().GetProperty("showAsWin")!.GetValue(p0)!);
        Assert.False((bool)p0.GetType().GetProperty("countsInHitRate")!.GetValue(p0)!);
        Assert.True((bool)p0.GetType().GetProperty("showMatchMarks")!.GetValue(p0)!);
        Assert.Equal(4, (int)p0.GetType().GetProperty("redHits")!.GetValue(p0)!);
    }

    [Fact]
    public void GroupThree_AAB_Vs_ABB_Still_Lost_In_Review()
    {
        var rows = new List<LottoRecommendationRecord>
        {
            new()
            {
                LotteryCode = LottoCodes.Fc3d, TargetIssue = "2026001", RankNo = 1,
                PlayType = "组选三", RedNumbers = "01,01,02"
            }
        };
        var draw = new LotteryDrawRecord
        {
            LotteryCode = LottoCodes.Fc3d, Issue = "2026001",
            DrawDate = new DateOnly(2026, 1, 1),
            Reds = [1, 2, 2], Blues = Array.Empty<int>(), Source = "t"
        };
        var issue = LottoRecommendationService.ReviewIssue(LottoCodes.Fc3d, "2026001", rows, draw);
        var p0 = ((IEnumerable<object>)issue.GetType().GetProperty("picks")!.GetValue(issue)!).Cast<object>().First();
        Assert.Equal("lost", (string)p0.GetType().GetProperty("status")!.GetValue(p0)!);
        Assert.False((bool)p0.GetType().GetProperty("showAsWin")!.GetValue(p0)!);
    }

    [Fact]
    public void Qxc_Stats_Not_Supported()
    {
        Assert.False(LottoCodes.SupportsFrequencyAnalysis("qxc"));
        Assert.False(LottoCodes.IsDigitGame("qxc"));
        Assert.Equal(0, LottoHitMath.OfficialDigitCount("qxc"));
    }

    [Fact]
    public void Normalize_Code_Entry()
    {
        Assert.Equal("fc3d", LottoCodes.Normalize(" FC3D "));
    }

    /// <summary>
    /// 用户票对奖 DTO：redHits/blueHits 与 prize_undetermined 一并可得，勿只从 note 解析。
    /// （GetUserTicketsAsync 与 ReviewIssue 使用同一 Grade 字段投影。）
    /// </summary>
    [Fact]
    public void UserTicket_Grade_Exposes_Structured_Hits_With_PrizeUndetermined()
    {
        var grade = LottoHitMath.Mark(
            "单式",
            [1, 5, 9, 12, 18, 33], [7],
            [5, 9, 12, 20, 21, 33], [16],
            LottoCodes.Ssq);
        Assert.Equal(LottoHitStatus.PrizeUndetermined, grade.Status);
        Assert.Equal("prize_undetermined", LottoHitStatusRules.ToApi(grade.Status));
        Assert.True(grade.HasMatchDetail);
        Assert.Equal(4, grade.RedHits);
        Assert.Equal(0, grade.BlueHits);
        Assert.False(LottoHitStatusRules.ShowAsWin(grade.Status));
    }
}
