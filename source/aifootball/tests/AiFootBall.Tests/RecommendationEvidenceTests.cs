using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class RecommendationEvidenceTests
{
    [Fact]
    public void Write_KeepsPageFields_DatabaseRows_AndPicture()
    {
        var root = Path.Combine(Path.GetTempPath(), "aifb-evidence-" + Guid.NewGuid().ToString("N"));
        var rec = new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = "周六010",
            HomeTeam = "勒沃库森",
            AwayTeam = "斯图加特",
            RecResult = '3',
            SignalLevel = 3,
            HitProbability = 58.9m,
            SignalDesc = "赔率主胜小降 + 必发主胜偏热",
            IsFeatured = true,
            RefScores = ["2:0", "1:0"],
            RefGoals = [2, 3]
        };
        var page = RecommendationEvidence.PageOf(rec);
        var boards = new object[]
        {
            new { board = "jc", name = "竞彩", total = 1, published = 1, items = new[] { page } }
        };
        var row = new RecommendationRecord
        {
            Board = Boards.Jc,
            MatchNo = "周六010",
            HomeTeam = "勒沃库森",
            AwayTeam = "斯图加特",
            RecResult = '3',
            RecDate = new DateTime(2026, 10, 31)
        };

        var dir = RecommendationEvidence.Write(root, new DateTime(2026, 10, 31, 21, 39, 0), boards, [row]);

        Assert.True(File.Exists(Path.Combine(dir, "fields.json")));
        Assert.True(File.Exists(Path.Combine(dir, "page.json")));
        Assert.True(File.Exists(Path.Combine(dir, "database.json")));
        var png = Path.Combine(dir, "slate.png");
        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 100);
        var pageText = File.ReadAllText(Path.Combine(dir, "page.json"));
        Assert.Contains("勒沃库森", pageText);
        Assert.Contains("58.9", pageText);
        Assert.Contains("RecResult", File.ReadAllText(Path.Combine(dir, "fields.json")));
    }
}
