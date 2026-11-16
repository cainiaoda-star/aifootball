using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 把当日推荐拼成一张票，并给出票面概率。
/// 与 /api/recommend 显示的那张票走同一段代码，避免「页面说的」和「落库记的」对不上。
/// </summary>
public static class TicketPlanBuilder
{
    /// <summary>
    /// 从推荐里挑腿组票。挑不出腿（无让球方向、无固定奖金、全被剔除）时返回 null。
    /// </summary>
    public static TicketDraft? Build(
        IReadOnlyList<MatchRecommendation> picks,
        IReadOnlyDictionary<long, (decimal Win, decimal Draw, decimal Loss)> prices,
        DateTime planDate,
        string board = Boards.Jc)
    {
        var legs = new List<(MatchRecommendation Rec, decimal Odds, string Option, decimal? LegP, decimal? ModelP, decimal? MarketP)>();
        foreach (var r in picks)
        {
            if (r.RecHandicap is not ('3' or '1' or '0') || r.MatchId is not long id) continue;
            if (!prices.TryGetValue(id, out var price)) continue;
            // 澳超客胜这个形态回测不过关，票里也剔掉，与页面口径一致
            if ((r.Tags & SignalTag.ALeagueAwayNotFeatured) != 0) continue;
            var leg = RecommendationSelection.JcCoverTicket.TryHandicapLeg(
                r.RecHandicap, r.HandicapProbability, price.Win, price.Draw, price.Loss);
            if (leg is null) continue;
            var marketP = RecommendationSelection.JcCoverTicket.LegProbability(
                leg.Value.Side, price.Win, price.Draw, price.Loss);
            // 模型值用 rec 上留的泊松结果，盘口值用出票查询到的让球价现算，两者同源同快照。
            var modelP = r.HandicapModelProbability is decimal mp ? mp / 100m : (decimal?)null;
            var pickP = RecommendationSelection.JcCoverTicket.PickProbability(modelP, marketP).Value;
            legs.Add((r, leg.Value.Odds, leg.Value.Option, pickP, modelP, marketP));
        }

        var picked = RecommendationSelection.JcCoverTicket.Pick(legs, x => x.Odds, x => x.LegP * 100m);
        if (picked.Count == 0) return null;

        var n = picked.Count;
        var pass = RecommendationSelection.JcCoverTicket.PassFor(n);
        var allPriced = picked.All(x => x.LegP is not null);

        decimal? hitP = null, backP = null, ev = null;
        if (allPriced)
        {
            var plan = RecommendationSelection.JcCoverTicket.Plan(
                picked.Select(x => (x.Odds, x.LegP!.Value)).ToList(), pass);
            hitP = Math.Round(plan.AnyPrize * 100m, 1);
            backP = Math.Round(plan.BackStake * 100m, 1);
            ev = Math.Round(plan.Expected, 3);
        }

        return new TicketDraft
        {
            PlanDate = planDate.Date,
            Board = board,
            PassType = pass,
            LegCount = n,
            BetCount = RecommendationSelection.JcCoverTicket.BetCountFor(n),
            Multiple = RecommendationSelection.JcCoverTicket.Multiple,
            BetAmount = RecommendationSelection.JcCoverTicket.StakeFor(n),
            HitProbability = hitP,
            BackProbability = backP,
            ExpectedReturn = ev,
            Legs = picked.Select((x, i) => new TicketLegDraft
            {
                Seq = i + 1,
                MatchId = x.Rec.MatchId,
                MatchNo = x.Rec.MatchNo,
                HomeTeam = x.Rec.HomeTeam,
                AwayTeam = x.Rec.AwayTeam,
                PickOption = x.Option,
                // 让球方向和让球数一起落库：赛后按「比分 + 让球数」判定，
                // 不必回头找推荐记录（竞彩没进精选的场次根本没有记录）
                PickSide = x.Rec.RecHandicap,
                Handicap = x.Rec.Handicap,
                Odds = x.Odds,
                LegProbability = x.LegP is null ? null : Math.Round(x.LegP.Value * 100m, 1),
                ModelProbability = x.ModelP is null ? null : Math.Round(x.ModelP.Value * 100m, 1),
                MarketProbability = x.MarketP is null ? null : Math.Round(x.MarketP.Value * 100m, 1)
            }).ToList()
        };
    }

    /// <summary>
    /// 结算一张票：按命中腿算奖金，口径与手工购票完全一致（含 1 万以上扣 20%）。
    /// 还有腿未判定时返回 null，表示「还不能结」。
    /// </summary>
    public static (bool Won, int HitCount, decimal Prize)? Settle(TicketRecord ticket)
    {
        if (ticket.Legs.Count == 0) return null;
        if (ticket.Legs.Any(l => l.LineHit is null)) return null;

        var legs = ticket.Legs
            .OrderBy(l => l.Seq)
            .Select(l => (hit: l.LineHit == true, odds: (decimal?)l.Odds))
            .ToList();
        var prize = PurchaseSettleMath.CalculatePrize(legs, ticket.PassType, Math.Max(1, ticket.Multiple));
        var hitCount = legs.Count(l => l.hit);
        return (prize > 0, hitCount, prize);
    }
}
