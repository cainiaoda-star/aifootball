using AiFootBall.Core.Models;
var line = new PurchaseLineRecord { Option1 = "2:0", Odds1 = 10m, Option2 = "3:1", Odds2 = 11m };
var hit = PurchaseSettleMath.EvaluateLine("全场比分", line, new PurchaseSettleMath.MatchScore { Home = 2, Away = 0 });
Console.WriteLine($"hit={hit.Hit} opt={hit.Option} odds={hit.Odds}");
var p2 = PurchaseSettleMath.CalculatePrize(new List<(bool, decimal?)> { (true, 8m), (true, 6.75m) }, "2串1", 1);
Console.WriteLine($"2串1={p2}"); // 108
var pMiss = PurchaseSettleMath.CalculatePrize(new List<(bool, decimal?)> { (true, 8m), (true, 10m), (false, 7.7m), (true, 11.5m) }, "3串1#4串1", 1);
Console.WriteLine($"3+4 one miss={pMiss}"); // C(3 of first/second/fourth)=1 combo of 3: 2*8*10*11.5=1840; 4串1=0
var pAll = PurchaseSettleMath.CalculatePrize(new List<(bool, decimal?)> { (true, 8m), (true, 10m), (true, 7.7m), (true, 11.5m) }, "3串1#4串1", 1);
Console.WriteLine($"3+4 all hit={pAll}");
if (hit.Hit != true || hit.Odds != 10m) { Console.Error.WriteLine("FAIL hit"); Environment.Exit(1); }
if (p2 != 108m) { Console.Error.WriteLine("FAIL 2串1"); Environment.Exit(1); }
if (pMiss != 1840m) { Console.Error.WriteLine($"FAIL miss got {pMiss}"); Environment.Exit(1); }
Console.WriteLine("ALL OK");
