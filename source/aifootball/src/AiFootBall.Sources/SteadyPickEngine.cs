using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 稳胆出注（2026-11-09 加，用户拍板）。
///
/// 规则只有四步，**每一步都能被复算**——这是它和主榜最大的区别：
///   1. 排除不推名单（欧国联/友谊赛/青年赛）与黑名单（澳超）；
///   2. 取该场竞彩胜平负（SPF，不让球）三档的赛前最后快照，去水成概率；
///   3. 取概率最高的那一档 —— 这就是「市场最看好」；
///   4. 该档赔率 ≤ <see cref="SteadyPickRules.MaxOdds"/>，全天后按赔率升序取前
///      <see cref="SteadyPickRules.MaxPerDay"/> 注。
///
/// 为什么不掺模型：DirectionStudy 实测模型在胜平负上 51.9% 对市场 56.9%，
/// 让球更是和无脑「永远让负」一模一样（42.3%）。这个栏目的唯一卖点是命中率，
/// 掺进一个更差的判断只会把它拉低。**主榜负责找价值，稳胆负责能中，各干各的。**
///
/// 为什么不看必发/亚盘/体能：同上。回测里这些信号没有一条能提高胜平负 Top-1 的命中率，
/// 加了只是给了一个「看起来很专业」的理由。
/// </summary>
public static class SteadyPickEngine
{
    /// <summary>出注结果 + 每场为什么没出（诊断用，榜空时唯一能自动解释原因的东西）。</summary>
    public sealed class Result
    {
        public List<SteadyPick> Picks { get; set; } = new();
        public List<string> Skipped { get; set; } = new();
        public int Candidates { get; set; }
        public int Excluded { get; set; }
        public int OverOdds { get; set; }
        public int CutByRank { get; set; }
    }

    public static Result Build(
        IReadOnlyList<MatchRecommendation> recs,
        IReadOnlyDictionary<long, List<JcOddsHistoryPoint>> odds)
    {
        var result = new Result();
        if (recs.Count == 0 || odds.Count == 0) return result;

        var pool = new List<SteadyPick>();

        foreach (var r in recs)
        {
            if (r.MatchId is not long mid) continue;
            var label = $"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}";

            // 1) 名单。与主榜共用同一份，不另立一套——名单维护在一处，两边行为才一致。
            if (NoPickLeagues.IsExcluded(r.LeagueName) || NoParlayLeagues.IsExcluded(r.LeagueName))
            {
                result.Excluded++;
                if (result.Skipped.Count < 30)
                    result.Skipped.Add($"{label}：{r.LeagueName} 在不出注名单");
                continue;
            }

            if (!odds.TryGetValue(mid, out var points))
            {
                if (result.Skipped.Count < 30) result.Skipped.Add($"{label}：没有竞彩固定奖金快照");
                continue;
            }

            // 2) 胜平负三档。缺一档就不算——两档凑出来的「去水概率」是假精度。
            decimal? oh = null, od = null, oa = null;
            foreach (var p in points)
            {
                if (p.PlayType != JcPlayTypes.Spf || p.Odds <= 1m) continue;
                switch (p.OptionCode)
                {
                    case "3": oh = p.Odds; break;
                    case "1": od = p.Odds; break;
                    case "0": oa = p.Odds; break;
                }
            }
            if (oh is not decimal h || od is not decimal d || oa is not decimal a)
            {
                if (result.Skipped.Count < 30) result.Skipped.Add($"{label}：胜平负三档不全");
                continue;
            }

            var over = 1m / h + 1m / d + 1m / a;
            if (over is < SteadyPickRules.MinOverRound or > SteadyPickRules.MaxOverRound)
            {
                if (result.Skipped.Count < 30)
                    result.Skipped.Add($"{label}：胜平负去水分母 {over:0.000} 超出合理区间（快照残缺）");
                continue;
            }

            var ph = 1m / h / over * 100m;
            var pd = 1m / d / over * 100m;
            var pa = 1m / a / over * 100m;

            char side;
            decimal pickOdds, pickPct;
            if (ph >= pd && ph >= pa) { side = '3'; pickOdds = h; pickPct = ph; }
            else if (pd >= pa) { side = '1'; pickOdds = d; pickPct = pd; }
            else { side = '0'; pickOdds = a; pickPct = pa; }

            // 3) 赔率上限。先用「该档赔率」，不用去水概率——用户看的是能买到几倍。
            if (pickOdds > SteadyPickRules.MaxOdds)
            {
                result.OverOdds++;
                if (result.Skipped.Count < 30)
                    result.Skipped.Add($"{label}：最看好档 {DirName(side)} {pickOdds:0.00} 超过上限 {SteadyPickRules.MaxOdds:0.00}");
                continue;
            }

            // 一场均衡局（最高概率都不到 40%）不该出现在「稳胆」里。
            // 回测里这类场次集中在赔率 2.0~2.6 箱，命中率 39~46%——和抛硬币差不多。
            if (pickPct < SteadyPickRules.MinMarketPct)
            {
                result.OverOdds++;
                if (result.Skipped.Count < 30)
                    result.Skipped.Add($"{label}：{DirName(side)} 去水仅 {pickPct:0.0}%，属均衡局");
                continue;
            }

            pool.Add(new SteadyPick
            {
                MatchId = mid,
                MatchNo = r.MatchNo,
                LeagueName = r.LeagueName,
                HomeTeam = r.HomeTeam,
                AwayTeam = r.AwayTeam,
                MatchTime = r.MatchTime,
                Side = side,
                Odds = pickOdds,
                MarketPct = Math.Round(pickPct, 1)
            });
        }

        result.Candidates = pool.Count;

        // 4) 全天后按赔率升序取前 N。同日同赔率时用开赛时间、场次号做稳定排序，
        //    否则同一批数据每次重算的排序可能不同，用户会看到注单跳来跳去。
        var ordered = pool
            .OrderBy(p => p.Odds)
            .ThenBy(p => p.MatchTime ?? DateTime.MaxValue)
            .ThenBy(p => p.MatchNo, StringComparer.Ordinal)
            .ToList();

        var rank = 1;
        foreach (var p in ordered)
        {
            if (rank > SteadyPickRules.MaxPerDay) { result.CutByRank++; continue; }
            p.Rank = rank++;
            result.Picks.Add(p);
        }

        return result;
    }

    private static string DirName(char d) => d switch
    {
        '3' => "主胜",
        '1' => "平局",
        _ => "客胜"
    };
}
