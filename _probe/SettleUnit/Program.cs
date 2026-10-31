using AiFootBall.Core.Models;

// 模拟 #10008：让胜 ×4，赛果 0:1 / 5:1 / 2:0 / 3:1，盘口 受让1 / 让1 / 让1 / 让1
var lines = new[]
{
    new { play = "全场比分", opt = "让胜", odds = 2.59m, h = 0, a = 1, hcp = (decimal?)1m },   // 受让1 → 让平，未中
    new { play = "全场比分", opt = "让胜", odds = 2.43m, h = 5, a = 1, hcp = (decimal?)-1m }, // 让胜
    new { play = "全场比分", opt = "让胜", odds = 2.14m, h = 2, a = 0, hcp = (decimal?)-1m },
    new { play = "全场比分", opt = "让胜", odds = 4.20m, h = 3, a = 1, hcp = (decimal?)-1m },
};

var legs = new List<(bool hit, decimal? odds)>();
foreach (var x in lines)
{
    var line = new PurchaseLineRecord { Option1 = x.opt, Odds1 = x.odds, Handicap = x.hcp };
    var score = new PurchaseSettleMath.MatchScore { Home = x.h, Away = x.a, Handicap = x.hcp };
    var hit = PurchaseSettleMath.EvaluateLine(x.play, line, score);
    Console.WriteLine($"{x.h}:{x.a} hcp={x.hcp} → hit={hit.Hit} opt={hit.Option} odds={hit.Odds}");
    legs.Add((hit.Hit, hit.Odds));
}

var prize1 = PurchaseSettleMath.CalculatePrize(legs, "3串1#4串1", 1);
var prize5 = PurchaseSettleMath.CalculatePrize(legs, "3串1#4串1", 5);
Console.WriteLine($"prize x1={prize1} x5={prize5}");
// 期望：仅 003+004+005 的 3串1 = 2*2.43*2.14*4.2 = 43.68
if (!legs[0].hit && legs[1].hit && legs[2].hit && legs[3].hit && prize1 == 43.68m)
    Console.WriteLine("ALL OK");
else
{
    Console.Error.WriteLine("FAIL");
    Environment.Exit(1);
}
