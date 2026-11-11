using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 每日逐场跟进复盘（2026-11-06 起的新「每日复盘」口径）。
///
/// 用户 2026-11-05 重定义复盘：
///  1. 删掉旧复盘方法，从 11-06 竞彩开始全新复盘。
///  2. 每天抓竞彩官方赛程，给每场一个大奶（比分/半全场/总进球三选一）。
///  3. 根据水位、必发数据变化做合理更改，重大变化标注。
///  4. 每次抓取时间、变化详情都记录。
///  5. 每场结束后在该场推荐后写复盘（当时为什么这么下 / 命中理由 / 错误原因）。
///  6. 每天总结一个知识技能。
///  7. 先这么做，后面有变化再说。
///
/// 与旧 DailyReview（每天一篇汇总 md）的区别：
/// 旧的是「当天全部场次的汇总表」，新的是「逐场跟进账」——每场一注，
/// 记录抓取时刻的水位/必发，赛后逐场回填复盘，最后每天沉淀一条技能。
/// </summary>
public sealed class DailyFollowService
{
    private readonly RecommendationService _rec;
    private readonly BettingRepository _repo;
    private readonly DailyFollowRepository _follow;

    public DailyFollowService(RecommendationService rec, BettingRepository repo, DailyFollowRepository follow)
    {
        _rec = rec;
        _repo = repo;
        _follow = follow;
    }

    /// <summary>生成/刷新当日逐场跟进：每场一注 + 抓取时间 + 水位必发变化 + 重大变化标注。</summary>
    public async Task<CollectionResult> BuildDailyAsync(DateTime day, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        // 每场一注（perMatch=true）：竞彩每场都给一个大奶，不做全局砍单。
        var build = await _rec.RecommendMilkWithGateAsync(null, ct, perMatch: true);
        // 2026-11-11 起引擎每场最多出 2 注（双选）。本表仍是一场一行：
        // 第一注进主列，第二注进 Alt* 列（见 DailyFollowRepository 的幂等 ALTER）。
        // 不拆成两行——SaveDayDiffAsync 按 MatchId 一对一，同场两行会直接抛重复键；
        // 而「逐场跟进」的产品形态本来就是一场一张卡，卡片里并排列出主推/备选更直观。
        var byMatch = build.Picks
            .Where(p => p.MatchId is > 0)
            .GroupBy(p => p.MatchId!.Value)
            .ToList();
        if (byMatch.Count == 0)
            return CollectionResult.Ok(0, build.Funnel.Skipped.Count > 0
                ? "无候选注：" + string.Join("；", build.Funnel.Skipped.Take(3))
                : "无候选注（模型未就绪或今日无竞彩）");

        var fids = byMatch.Select(g => g.Key).ToList();
        var openEu = await _repo.GetOpenEuAsync(fids, ct);
        var closeEu = await _repo.GetClosingEuAsync(fids, ct);
        var exSpan = await _repo.GetExchangeSpanAsync(fids, ct);
        var freshness = await _repo.GetMarketFreshnessAsync(fids, ct);

        var rows = new List<DailyFollowRow>();
        foreach (var g in byMatch)
        {
            var list = g.ToList();
            var p = list[0];                 // 引擎顺序即优先级：主推在前
            var alt = list.Count > 1 ? list[1] : null;
            var mid = g.Key;
            openEu.TryGetValue(mid, out var open);
            closeEu.TryGetValue(mid, out var close);
            exSpan.TryGetValue(mid, out var ex);
            freshness.TryGetValue(mid, out var lastAt);

            var major = MajorChangeOf(open, close, ex);
            // FollowDate 用「开赛日」（MatchTime 的日期），而不是生成日——
            // 竞彩通常是晚上 23 点后生成次日场次，若用生成日，11-06 开赛的场会被记成 11-05，
            // 导致 11-06 的 settle/skill 结算不到它们（时间错位 bug，2026-11-05 修）。
            var followDate = p.MatchTime?.Date ?? day.Date;
            rows.Add(new DailyFollowRow
            {
                FollowDate = followDate,
                MatchId = mid,
                MatchNo = p.MatchNo,
                LeagueName = p.LeagueName,
                HomeTeam = p.HomeTeam,
                AwayTeam = p.AwayTeam,
                MatchTime = p.MatchTime,
                PlayType = p.PlayType,
                OptionCode = p.OptionCode,
                OptionName = p.OptionName,
                Odds = p.Odds,
                ModelProbability = p.ModelProbability,
                Ev = p.Ev,
                Reason = ReasonOf(p),
                FirstCapturedAt = now,
                LastCapturedAt = lastAt == default ? null : lastAt,
                OddsOpenHome = open.Home, OddsOpenDraw = open.Draw, OddsOpenAway = open.Away,
                OddsLastHome = close.Home, OddsLastDraw = close.Draw, OddsLastAway = close.Away,
                ExRatioOpen = ex.LastHome,   // 必发占比只有最近值，初值暂无独立采样，先并记
                ExRatioLast = ex.LastHome,
                MajorChange = major,
                TriggerSignal = major,      // 触发本次变更（若有）的水位/必发信号
                // 双选的第二注。为空表示这场只出了 1 注。
                AltPlayType = alt?.PlayType,
                AltOptionCode = alt?.OptionCode,
                AltOptionName = alt?.OptionName,
                AltOdds = alt?.Odds,
                AltModelProbability = alt?.ModelProbability,
                AltEv = alt?.Ev
            });
        }

        // 按开赛日分组保存，跨天的一批也能正确归类。
        var totalUpserted = 0;
        var totalChanged = 0;
        foreach (var grp in rows.GroupBy(r => r.FollowDate))
        {
            var (upserted, changed) = await _follow.SaveDayDiffAsync(grp.Key, grp.ToList(), ct);
            totalUpserted += upserted;
            totalChanged += changed;
        }
        var changeNote = totalChanged > 0 ? $"，其中 {totalChanged} 场因水位/必发变化改判（已留痕）" : "";
        return CollectionResult.Ok(totalUpserted, $"逐场跟进已写入 {totalUpserted} 场（候选 {build.Funnel.Candidates} 场）{changeNote}");
    }

    /// <summary>赛后逐场回填赛果 + 写复盘。返回结算场数。</summary>
    public async Task<CollectionResult> SettleDailyAsync(DateTime day, CancellationToken ct = default)
    {
        var rows = await _follow.GetDayAsync(day, ct);
        var pending = rows.Where(r => r.SettledAt is null).ToList();
        if (pending.Count == 0)
            return CollectionResult.Ok(0, $"{day:yyyy-MM-dd} 没有待结算的逐场跟进");

        var fids = pending.Select(r => r.MatchId).Distinct().ToList();
        var scores = await _repo.GetReviewScoresAsync(fids, ct);

        var settled = 0;
        foreach (var r in pending)
        {
            if (!scores.TryGetValue(r.MatchId, out var score) || score.Finished != true)
                continue;   // 还没完场，下一轮再判

            var (hit, altHit, actualScore, halfScore, goals) = Judge(r, score);
            var review = ReviewOf(r, hit, score);
            await _follow.SettleAsync(r.MatchId, actualScore, halfScore, goals, hit, altHit, review, ct);
            settled++;
        }

        return CollectionResult.Ok(settled, $"{day:yyyy-MM-dd} 逐场复盘 {settled}/{pending.Count} 场");
    }

    /// <summary>每天总结一条知识技能（基于当日复盘命中/错误）。</summary>
    public async Task<CollectionResult> SummarizeSkillAsync(DateTime day, CancellationToken ct = default)
    {
        var rows = await _follow.GetDayAsync(day, ct);
        var settled = rows.Where(r => r.SettledAt is not null).ToList();
        if (settled.Count == 0)
            return CollectionResult.Ok(0, $"{day:yyyy-MM-dd} 还没有可总结的复盘（0 场结算）");

        var hit = settled.Count(r => r.Hit == true);
        var miss = settled.Count(r => r.Hit == false);
        var hitRate = settled.Count > 0 ? Math.Round(100.0 * hit / settled.Count, 1) : 0d;

        var (title, body, evidence) = SkillFrom(settled, hit, miss, (decimal)hitRate);
        await _follow.SaveSkillAsync(day, title, body, evidence, ct);
        return CollectionResult.Ok(1, $"技能已总结：{title}");
    }

    /* ---------------- 辅助 ---------------- */

    /// <summary>重大变化标注：主胜赔率降/升 >5%、必发摸到极端大热（≥75% 或 ≥60% 偏热）。</summary>
    static string? MajorChangeOf(
        (decimal? Home, decimal? Draw, decimal? Away) open,
        (decimal? Home, decimal? Draw, decimal? Away) close,
        (decimal? LastHome, decimal? LastDraw, decimal? LastAway, decimal? MaxHome, decimal? MaxAway) ex)
    {
        var notes = new List<string>();

        // 主胜赔率变化：降 = 看好主队（变热），升 = 看衰主队（变冷）
        if (open.Home is > 0 && close.Home is > 0)
        {
            var pct = (close.Home.Value - open.Home.Value) / open.Home.Value * 100m;
            if (pct <= -5m) notes.Add($"主胜赔率大降 {Math.Abs(pct):0.0}%（市场转热主队）");
            else if (pct >= 5m) notes.Add($"主胜赔率大升 {pct:0.0}%（市场看衰主队）");
            else if (pct <= -2m) notes.Add($"主胜赔率小降 {Math.Abs(pct):0.1}%");
            else if (pct >= 2m) notes.Add($"主胜赔率小升 {pct:0.1}%");
        }
        if (open.Away is > 0 && close.Away is > 0)
        {
            var pct = (close.Away.Value - open.Away.Value) / open.Away.Value * 100m;
            if (pct <= -5m) notes.Add($"客胜赔率大降 {Math.Abs(pct):0.0}%（市场转热客队）");
            else if (pct >= 5m) notes.Add($"客胜赔率大升 {pct:0.0}%");
        }

        // 必发极端大热/偏热
        if (ex.MaxHome is >= 75m) notes.Add($"必发主胜摸到极端大热 {ex.MaxHome:0.0}%");
        else if (ex.MaxHome is >= 60m) notes.Add($"必发主胜偏热 {ex.MaxHome:0.0}%");
        if (ex.MaxAway is >= 75m) notes.Add($"必发客胜摸到极端大热 {ex.MaxAway:0.0}%");
        else if (ex.MaxAway is >= 60m) notes.Add($"必发客胜偏热 {ex.MaxAway:0.0}%");

        return notes.Count == 0 ? null : string.Join("；", notes);
    }

    /// <summary>「当时为什么这么下」——首抓时的一句话理由。</summary>
    static string ReasonOf(MilkPick p)
    {
        var dir = p.MarketDirection switch { '3' => "主胜", '1' => "平局", '0' => "客胜", _ => null };
        var dirNote = dir is null ? "" : $"，市场方向 {dir}";
        var diverge = p.DivergedFromMarket ? "（与市场方向相反）" : "";
        return $"模型 {p.OptionName} 概率 {p.ModelProbability:0.0}%，赔率 {p.Odds:0.00}{dirNote}{diverge}";
    }

    /// <summary>判中：比分/半全场/总进球分别对照赛果。</summary>
    static (bool Hit, bool? AltHit, string Score, string? Half, int? Goals) Judge(DailyFollowRow r, ReviewScore score)
    {
        var h = score.HomeScore!.Value;
        var a = score.AwayScore!.Value;
        var scoreText = $"{h}:{a}";
        var half = score.HalfHomeScore is int hh && score.HalfAwayScore is int ha ? $"{hh}:{ha}" : null;
        var goals = h + a;

        var hit = HitOf(r.PlayType, r.OptionCode, h, a, score.HalfHomeScore, score.HalfAwayScore);
        // 双选第二注（2026-11-11）。没备选时返回 null，与「备选判为未中」区分开。
        bool? altHit = string.IsNullOrWhiteSpace(r.AltPlayType) || string.IsNullOrWhiteSpace(r.AltOptionCode)
            ? null
            : HitOf(r.AltPlayType!, r.AltOptionCode!, h, a, score.HalfHomeScore, score.HalfAwayScore);
        return (hit, altHit, scoreText, half, goals);
    }

    /// <summary>单注是否命中。主推与备选共用，避免两处判定口径漂移。</summary>
    private static bool HitOf(string playType, string optionCode, int h, int a, int? hh, int? ha)
    {
        var scoreText = $"{h}:{a}";
        var goals = h + a;
        return playType switch
        {
            JcPlayTypes.Bf => scoreText == optionCode,
            JcPlayTypes.Bq => HalfFullOf(hh, ha, h, a) == optionCode,
            JcPlayTypes.Jq => goals >= 7 ? optionCode == "7" : optionCode == goals.ToString(),
            _ => false
        };
    }

    static string? HalfFullOf(int? hh, int? ha, int h, int a)
    {
        if (hh is null || ha is null) return null;
        char SideOf(int x, int y) => x > y ? '3' : x < y ? '0' : '1';
        return $"{SideOf(hh.Value, ha.Value)}-{SideOf(h, a)}";
    }

    /// <summary>赛后复盘文字：命中理由 / 错误原因。</summary>
    static string ReviewOf(DailyFollowRow r, bool hit, ReviewScore score)
    {
        var res = $"{score.HomeScore}:{score.AwayScore}";
        if (hit)
            return $"命中。当时下 {r.OptionName}（模型概率 {r.ModelProbability:0.0}%），" +
                   $"赛果 {res} 兑现了这个剧本。依据：{r.Reason}";
        return $"未中。当时下 {r.OptionName}（模型概率 {r.ModelProbability:0.0}%），" +
               $"实际 {res}。偏差点：{MissReasonOf(r, score)}";
    }

    static string MissReasonOf(DailyFollowRow r, ReviewScore score)
    {
        var h = score.HomeScore!.Value;
        var a = score.AwayScore!.Value;
        return r.PlayType switch
        {
            JcPlayTypes.Bf => $"比分差在 {r.OptionCode} vs 实际 {h}:{a}，模型网格对该比分的概率估计偏了",
            JcPlayTypes.Bq => $"半全场节奏没踩准（{r.OptionCode}），实际半场/全场走向不同",
            JcPlayTypes.Jq => $"总进球档差（推 {r.OptionName}，实际 {h + a} 球），进球节奏偏离模型泊松估计",
            _ => "玩法口径不匹配"
        };
    }

    /// <summary>从当日复盘提炼一条技能。</summary>
    static (string Title, string Body, string Evidence) SkillFrom(
        List<DailyFollowRow> settled, int hit, int miss, decimal hitRate)
    {
        // 先看有没有「同一种错误重复出现」的模式
        var missGroups = settled.Where(r => r.Hit == false)
            .GroupBy(r => r.PlayType)
            .OrderByDescending(g => g.Count())
            .ToList();

        string title, body, evidence;
        if (missGroups.Count > 0)
        {
            var worst = missGroups.First();
            var worstName = worst.Key switch { JcPlayTypes.Bf => "比分", JcPlayTypes.Bq => "半全场", JcPlayTypes.Jq => "总进球", _ => worst.Key };
            title = $"大奶 {worstName} 档位连续失手，复盘其概率分布偏差";
            body = $"当日 {miss} 场未中里，{worstName} 占 {worst.Count()} 场，是最主要的失手来源。" +
                   $"逐场看，失手点集中在「模型给的概率高于实际频率」的档位——模型对这些档的泊松/网格估计偏乐观。" +
                   $"下一步：回看这几场的水位/必发是否已经提前转向（主胜赔率大升、必发反向），" +
                   $"如果转向信号在先、模型没跟上，就是「模型滞后于市场」，下次对 {worstName} 大奶要等转向稳定再下。";
            evidence = string.Join("；", worst.Select(r => $"{r.HomeTeam}vs{r.AwayTeam} 推{r.OptionName} 实际{(r.ActualScore ?? "—")}"));
        }
        else if (hit >= miss)
        {
            title = $"命中率 {hitRate:0.0}% 站住，验证「概率优先、赔率≥4」的大奶筛选有效";
            body = $"当日 {settled.Count} 场命中 {hit} 场（{hitRate:0.0}%）。" +
                   $"命中集中在概率最高的那一档，说明「每场取概率最高一注」的逐场跟进口径能筛出相对最可能中的大奶。" +
                   $"继续坚持：不改门槛，等样本翻倍再看哪一玩法、哪一档位长期站得住。";
            evidence = $"{hit}/{settled.Count}，命中率 {hitRate:0.0}%";
        }
        else
        {
            title = $"命中率 {hitRate:0.0}% 低于随机基线，逐场跟进口径需回看方向闸";
            body = $"当日 {settled.Count} 场只中 {hit} 场（{hitRate:0.0}%），低于大奶玩法应有的随机命中。" +
                   $"逐场回看：失手场次里有多少是「模型方向 vs 市场方向背离」还照下的？" +
                   $"方向闸 EnforceDirectionVeto 目前关着（回测负 alpha），但逐场跟进模式下，「背离还下」的注会拖垮整张账。" +
                   $"下一步：把背离场次单独拆出来看命中，若稳定差，就在逐场跟进里对强背离场次降级（不出或标高风险）。";
            evidence = $"{hit}/{settled.Count}，命中率 {hitRate:0.0}%";
        }
        return (title, body, evidence);
    }
}
