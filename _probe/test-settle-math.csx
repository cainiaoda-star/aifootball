#r "D:\AiFootBall\src\AiFootBall.Core\bin\Release\net8.0\AiFootBall.Core.dll"
using AiFootBall.Core.Models;

var line = new PurchaseLineRecord { Option1 = "2:0", Odds1 = 10m, Option2 = "3:1", Odds2 = 11m };
var score = new PurchaseSettleMath.MatchScore { Home = 2, Away = 0 };
var hit = PurchaseSettleMath.EvaluateLine("全场比分", line, score);
Console.WriteLine($"hit={hit.Hit} opt={hit.Option} odds={hit.Odds}");

var legs = new List<(bool, decimal?)> {
  (true, 8m), (true, 10m), (false, 7.7m), (true, 11.5m)
};
var prize = PurchaseSettleMath.CalculatePrize(legs, "3串1#4串1", 1);
Console.WriteLine($"prize 3+4 with one miss={prize}"); // only 3串1 combos without the miss

var legs2 = new List<(bool, decimal?)> {
  (true, 8m), (true, 6.75m)
};
Console.WriteLine($"2串1={PurchaseSettleMath.CalculatePrize(legs2, "2串1", 1)}"); // 2*8*6.75=108
