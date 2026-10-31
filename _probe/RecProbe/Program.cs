using AiFootBall.Core.Models;
using AiFootBall.Sources;
var signal = RecommendationEngine.Recommend(Boards.Jc, null, "001", 1, "英超", "主", "客", DateTime.Today, null, -0.10m, 65m, 20m, null, 1.65m, 3.60m, 4.80m);
var fallback = RecommendationEngine.Recommend(Boards.Jc, null, "002", 2, "英超", "主", "客", DateTime.Today, null, null, null, null, null, 1.20m, 5.50m, 8.00m);
Console.WriteLine($"signal featured={RecommendationSelection.ForDisplay(Boards.Jc, new[]{signal}).Count} descHas照发={signal.SignalDesc.Contains("照发")}");
Console.WriteLine($"fallback featured={RecommendationSelection.ForDisplay(Boards.Jc, new[]{fallback}).Count} fb={fallback.IsOddsFallback} desc={fallback.SignalDesc.Contains("不进精选")}");
Console.WriteLine($"passType3={RecommendationSelection.PassTypeValue(Boards.Jc, 3)}");
