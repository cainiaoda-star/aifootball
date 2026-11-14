using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiFootBall.Core.Models;

/// <summary>
/// 源侧快照里的一场（2026-11-14 第七轮加）。
///
/// 为什么需要它：上一版的「预期」取自 `MilkInputMatch`——那是**引擎自己写下的输入凭证**。
/// 引擎在写凭证之前就 `continue` 掉的场次（模型不认识队、样本不足、禁推联赛）两边同时为 0，
/// 核对照样通过、照样发布，而榜面上就是少了一场，谁也不知道。11-14 的 1419319
/// （大宫松鼠RB vs 山形山神）正是这样消失的：`FbMilkInput/ProbSnapshot/Gate` 全 0 行。
///
/// 所以预期场次集合必须来自**源侧**：该轮开始时仍受注、未开赛的竞彩场次（BettingCard）
/// 连同它们的赔率采样时间与档位清单（FbJcOddsHistory）。这两张表都不是引擎的产物，
/// 引擎在场次上少算一场、在档位上少算一档，这里都能独立发现。
///
/// 三条要求（用户 2026-11-14 明确）：
///   ① 场次集合独立于引擎；
///   ② 历史赔率有记录 ≠ 本轮仍有效 —— 必须带**最新采样时刻**，超龄要拒发；
///   ③ 每个源比赛必须留下「输入凭证」或「带证据的拒绝记录」，二者必居其一。
/// </summary>
public sealed class MilkSourceMatch
{
    public long MatchId { get; init; }
    public string? MatchNo { get; init; }
    public string? LeagueName { get; init; }
    public string? HomeTeam { get; init; }
    public string? AwayTeam { get; init; }
    public DateTime? MatchTime { get; init; }
    public DateTime SalesDate { get; set; }

    /// <summary>该场最新一条赔率采样的时刻（各档取最大 CapturedAt）。null = 本轮一条采样都没有。</summary>
    public DateTime? LastSampleAt { get; init; }

    /// <summary>该场在源侧的档位清单，形如 `SPF|3`。口径与 <c>GetLatestAsync</c> 一致（每键取最新、Odds&gt;1）。</summary>
    public IReadOnlyList<string> OptionKeys { get; init; } = Array.Empty<string>();

    public int OptionCount => OptionKeys.Count;

    /// <summary>各玩法档数，形如 `BF=31,BQ=9,JQ=8,NSPF=3,SPF=3`。</summary>
    public string Plays
        => string.Join(",", OptionKeys
            .Select(k => k.Split('|')[0])
            .GroupBy(p => p, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}"));

    public override string ToString()
        => $"{MatchNo ?? MatchId.ToString(CultureInfo.InvariantCulture)} {HomeTeam}vs{AwayTeam}"
           + $"（{OptionCount} 档，采样 {LastSampleAt:MM-dd HH:mm}）";
}

/// <summary>
/// 引擎对某个源比赛给出的**拒绝记录**（2026-11-14 加）。
///
/// 与 <see cref="MilkSlateExemption"/> 的分工：
///   · 豁免回答「这一档为什么没存」（档位级）；
///   · 拒绝回答「这一场为什么没算」（场次级）。
///
/// 上一版只有前者，场次级的 `continue` 全在引擎内存里（`funnel.Skipped`，最多 20 条、不落库），
/// 于是「引擎自己跳过了」既没有凭证也没有证据，事后只能靠重跑模型去猜。
///
/// 规则与豁免同一套：原因必须是固定枚举、必须带可机器核验的证据前缀，
/// **不接受的原因照旧判失败**——「引擎自行跳过」不能因为换了个名字就变成合法。
/// </summary>
public sealed class MilkInputRejection
{
    public long MatchId { get; init; }
    public string? MatchNo { get; init; }
    public string? LeagueName { get; init; }
    public string? HomeTeam { get; init; }
    public string? AwayTeam { get; init; }
    public DateTime SalesDate { get; init; }

    public string Reason { get; init; } = string.Empty;

    /// <summary>证据，形如 `options=0;source=0`、`team=大宫松鼠RB;games=0;min=5`。</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>
    /// 最终是否被接受（由核对器独立判定后回填）。引擎申报时一律先按规则判：
    /// 规则本身不接受的原因（如 not-sale）恒 false；`name-unresolved` 要等核对器
    /// 用**库内真实样本量**判定它到底是「真新队」还是「名字变了」。
    /// </summary>
    public bool Accepted { get; set; }

    // ── 原因枚举 ──
    /// <summary>源侧已停售/已开赛，本不该进推荐：出现即说明上游过滤与本轮不一致。</summary>
    public const string ReasonNotSale = "not-sale";
    /// <summary>联赛在「完全不出注」或「不进串关」名单里（规则决定，与模型状态无关）。</summary>
    public const string ReasonLeagueExcluded = "league-excluded";
    /// <summary>该场进推荐时一条赔率都没有；证据必须与源侧档数一致（source=0）。</summary>
    public const string ReasonNoOdds = "no-odds";
    /// <summary>主客队名缺失，无法定位球队。</summary>
    public const string ReasonNameMissing = "name-missing";
    /// <summary>队名解析不出——**默认不接受**，须核对器用库内样本证明是真新队。</summary>
    public const string ReasonNameUnresolved = "name-unresolved";
    /// <summary>模型认得队，但该队在本联赛样本不足（真样本不足，不是名字问题）。</summary>
    public const string ReasonThinSample = "thin-sample";
    /// <summary>比分分布算出空网格/零质量，无法给任何一档概率。</summary>
    public const string ReasonNoDistribution = "no-distribution";
    /// <summary>比分模型没就绪（样本 &lt; 30 场）：整轮没有可用的概率，不是「这场有问题」。</summary>
    public const string ReasonModelNotReady = "model-not-ready";

    /// <summary>真新队（库内确无样本），由核对器把 name-unresolved 升级而来。</summary>
    public const string ReasonNewTeam = "new-team";

    public sealed class Rule
    {
        public string Reason { get; init; } = string.Empty;
        public string EvidencePrefix { get; init; } = string.Empty;
        public string Condition { get; init; } = string.Empty;
        public bool Accepted { get; init; } = true;
        public string? RejectNote { get; init; }
    }

    public static readonly Rule[] Rules =
    {
        new()
        {
            Reason = ReasonNotSale,
            EvidencePrefix = "sale=",
            Accepted = false,
            Condition = "无正当条件",
            RejectNote = "not-sale 不接受：源侧已停售/已开赛的场次不该出现在本轮推荐里，"
                         + "出现即说明受注过滤与本轮不一致，必须查上游而不是放行"
        },
        new()
        {
            Reason = ReasonLeagueExcluded,
            EvidencePrefix = "list=",
            Condition = "联赛命中 NoPickLeagues / NoParlayLeagues（同一份名单，不另写）"
        },
        new()
        {
            Reason = ReasonNoOdds,
            EvidencePrefix = "options=0",
            Condition = "该场进推荐时源侧档数为 0；证据必须写 `options=0;source=<源侧档数>`，"
                        + "源侧档数与 0 不符即判证据不成立（赔率有记录不等于本轮没价）"
        },
        new()
        {
            Reason = ReasonNameMissing,
            EvidencePrefix = "team=empty",
            Condition = "主队或客队名为空"
        },
        new()
        {
            Reason = ReasonNameUnresolved,
            EvidencePrefix = "team=",
            Accepted = false,
            Condition = "无正当条件（引擎自己认不出不等于该队没有样本）",
            RejectNote = "name-unresolved 不接受：须由核对器用独立库检证明该队在近 18 个月确无样本，"
                         + "否则这是队名归一化缺陷（1419319 即此形态）"
        },
        new()
        {
            Reason = ReasonNewTeam,
            EvidencePrefix = "team=",
            Condition = "核对器独立库检确认该队在库内近 18 个月样本 < 出注门槛（真新队/首次出现）"
        },
        new()
        {
            Reason = ReasonThinSample,
            EvidencePrefix = "games=",
            Condition = "模型认得这支队，但本联赛样本低于门槛；证据 `games=主/客;min=门槛`"
        },
        new()
        {
            Reason = ReasonNoDistribution,
            EvidencePrefix = "grid=",
            Condition = "比分分布为空或总质量 ≤ 0；证据 `grid=<格数>;mass=<质量>`"
        },
        new()
        {
            Reason = ReasonModelNotReady,
            EvidencePrefix = "ready=false",
            Accepted = false,
            Condition = "无正当条件",
            RejectNote = "model-not-ready 不接受：模型没就绪时整轮概率都不可用，"
                         + "此时发布等于用空榜覆盖上一轮"
        }
    };

    public Rule? Matched => Array.Find(Rules, r => string.Equals(r.Reason, Reason, StringComparison.Ordinal));
    public bool IsKnown => Matched is not null;
    public bool HasEvidence => !string.IsNullOrWhiteSpace(Evidence);
    public bool EvidenceOk
        => Matched is { } r && HasEvidence && Evidence.StartsWith(r.EvidencePrefix, StringComparison.Ordinal);

    /// <summary>按规则判定（不含「真新队」升级——那一步要独立库检）。</summary>
    public bool AcceptedByRule => Matched is { Accepted: true } && EvidenceOk;

    public string Defect
    {
        get
        {
            var r = Matched;
            if (r is null) return "原因不在枚举内";
            if (!r.Accepted) return r.RejectNote ?? "该原因不接受";
            if (!HasEvidence) return $"缺证据（应为 {r.EvidencePrefix}…）";
            if (!EvidenceOk) return $"证据与原因不符（应为 {r.EvidencePrefix}…，实际 {Trim(Evidence)}）";
            return string.Empty;
        }
    }

    private static string Trim(string s) => s.Length <= 48 ? s : s[..48] + "…";

    public string Key => MatchId.ToString(CultureInfo.InvariantCulture);

    public override string ToString()
        => $"{MatchNo ?? MatchId.ToString(CultureInfo.InvariantCulture)} {HomeTeam}vs{AwayTeam}：{Reason}({Evidence})";
}

/// <summary>源侧核对结论。</summary>
public sealed class MilkSourceReport
{
    public IReadOnlyList<MilkLedgerIssue> Issues { get; init; } = Array.Empty<MilkLedgerIssue>();

    /// <summary>源侧场次（本轮的「应该有」）。</summary>
    public IReadOnlyList<MilkSourceMatch> Source { get; init; } = Array.Empty<MilkSourceMatch>();

    /// <summary>本轮的拒绝记录（已回填 Accepted）。落库用。</summary>
    public IReadOnlyList<MilkInputRejection> Rejections { get; init; } = Array.Empty<MilkInputRejection>();

    public bool Ok => Issues.Count == 0;

    public int ComputedCount { get; init; }
    public int RejectedCount { get; init; }

    /// <summary>形如 `源 6 场（算 5 / 拒 1）；采样最新 08:38；拒绝 thin-sample 1`。</summary>
    public string Summary
    {
        get
        {
            var byReason = Rejections
                .GroupBy(r => r.Reason, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} {g.Count()}");
            var tail = Rejections.Count == 0 ? "无" : string.Join("、", byReason);
            return $"源 {Source.Count} 场（算 {ComputedCount} / 拒 {RejectedCount}）；拒绝 {tail}";
        }
    }

    public string Detail
    {
        get
        {
            if (Issues.Count == 0) return string.Empty;
            var head = Issues.Take(8).Select(i => i.ToString());
            var more = Issues.Count > 8 ? $"；另有 {Issues.Count - 8} 条" : string.Empty;
            return string.Join("；", head) + more;
        }
    }
}

/// <summary>
/// 源侧账本（2026-11-14 加）：把「源快照」与「引擎的输入凭证 + 拒绝记录」对齐。
///
/// 判据（每条都能机器核验）：
///   ① 源里每一场，必须要么有输入凭证、要么有拒绝记录；两者都有或都没有都判失败；
///   ② 拒绝记录必须是枚举内的原因 + 证据前缀相符；
///   ③ `name-unresolved` 只有在该队近 18 个月样本 **低于出注门槛** 时才算真新队
///      （由调用方用独立查询判定，不看引擎结论）；
///   ④ 算过的场次，其最新赔率采样不得超龄（默认 30 分钟）；超龄即「历史赔率不等于本轮有效」；
///   ⑤ 算过的场次，源侧每一个档位都必须出现在输入凭证的档位清单里——
///      少一档同样是「引擎少算」，不能靠「预期=输入」自动通过；
///   ⑥ **源侧自己少了档**也要发现（2026-11-14 第八轮）：每个已启售玩法必须是
///      <see cref="JcPlayDomain"/> 的完整档位域，少一档即 `source-option-gap`。
///
/// 为什么 ⑥ 不能省：⑤ 只查「源有档 → 引擎没算」，源侧自己少采一档时预期和输入一起少，
/// 两边同时为「对」——与 1419319 同一种病因（预期取自被检验的一方）。
/// 也正因为如此，「采集里没有某玩法的赔率」**不得**被当成「该玩法未启售」：
/// 未启售需要源侧落库的启售清单才能证明，本轮没有这个字段，所以一律按缺档阻断。
/// </summary>
public static class MilkSourceSlate
{
    /// <summary>赔率采样允许的最大年龄（分钟）。超龄不是「数据旧」，是「本轮不该拿它出票」。</summary>
    public const int MaxSampleAgeMinutes = 30;

    /// <summary>判「真新队」时与出注门槛同源（<c>MilkPickEngine.MinTeamGames</c>），由调用方传入。</summary>
    public const int DefaultMinTeamGames = 5;

    /// <param name="teamGames">
    /// 独立库检：给定队名，返回该队近 18 个月的完赛场次。空/为 null 时视为**无法证明**，
    /// `name-unresolved` 一律不接受（宁可少发一轮，也不把「认不出」当「新队」）。
    /// </param>
    public static MilkSourceReport Verify(
        IReadOnlyList<MilkSourceMatch>? source,
        IReadOnlyList<MilkInputMatch>? inputs,
        IReadOnlyList<MilkInputRejection>? rejections,
        DateTime runAt,
        Func<string?, int>? teamGames = null,
        int minTeamGames = DefaultMinTeamGames,
        TimeSpan? maxSampleAge = null)
    {
        var src = (source ?? Array.Empty<MilkSourceMatch>()).Where(m => m.MatchId > 0).ToList();
        var ins = (inputs ?? Array.Empty<MilkInputMatch>()).Where(m => m.MatchId > 0).ToList();
        var rej = (rejections ?? Array.Empty<MilkInputRejection>()).Where(r => r.MatchId > 0).ToList();

        var issues = new List<MilkLedgerIssue>();
        var srcIds = new HashSet<long>(src.Select(m => m.MatchId));
        var inIds = new HashSet<long>(ins.Select(m => m.MatchId));
        var rejById = new Dictionary<long, MilkInputRejection>();
        foreach (var r in rej) rejById[r.MatchId] = r;   // 同场多条：后写的覆盖，重复本身下面会报

        foreach (var dup in rej.GroupBy(r => r.MatchId).Where(g => g.Count() > 1))
            issues.Add(new MilkLedgerIssue
            {
                Table = "source", Kind = "duplicate", Key = dup.Key.ToString(CultureInfo.InvariantCulture),
                Expected = 1, Actual = dup.Count()
            });

        foreach (var m in src)
        {
            var hasInput = inIds.Contains(m.MatchId);
            var hasReject = rejById.ContainsKey(m.MatchId);
            var key = m.MatchId.ToString(CultureInfo.InvariantCulture);

            if (hasInput && hasReject)
            {
                issues.Add(new MilkLedgerIssue
                {
                    Table = "source", Kind = "duplicate-input", Key = key, Expected = 1, Actual = 2
                });
            }
            else if (!hasInput && !hasReject)
            {
                // 这就是 1419319 的形态：引擎在写凭证之前就 continue 了，两边都为空。
                issues.Add(new MilkLedgerIssue
                {
                    Table = "source", Kind = "missing-input", Key = $"{key}|{m.MatchNo}|{m.HomeTeam}vs{m.AwayTeam}",
                    Expected = 1, Actual = 0
                });
            }
        }

        // 引擎算了源里没有的场次：源快照不完整，或引擎拿了别的输入——两种都不能发布。
        foreach (var m in ins.Where(m => !srcIds.Contains(m.MatchId)))
            issues.Add(new MilkLedgerIssue
            {
                Table = "source", Kind = "unexpected-input",
                Key = $"{m.MatchId}|{m.MatchNo}|{m.HomeTeam}vs{m.AwayTeam}", Expected = 0, Actual = 1
            });

        foreach (var r in rej.Where(r => !srcIds.Contains(r.MatchId)))
            issues.Add(new MilkLedgerIssue
            {
                Table = "source", Kind = "unexpected-rejection",
                Key = $"{r.MatchId}|{r.MatchNo}|{r.Reason}", Expected = 0, Actual = 1
            });

        // ── 拒绝记录本身成不成立 ──
        var final = new List<MilkInputRejection>(rej.Count);
        foreach (var r in rej)
        {
            var defect = r.Defect;
            var games = -1;

            // `name-unresolved` 不能靠引擎自证：名称认不出恰恰说明引擎那套名字解析在这个名字上失效了。
            // 唯一独立的问题是「库里到底有没有这支队」，所以这里只认独立库检的场次。
            //
            // 注意判据顺序：这条原因本身是 `Accepted=false`（默认不放行），
            // 所以不能先看 `Defect`——那会直接拿到「不接受」的说明，永远走不到库检。
            // 只有**证据格式成立**时才进入独立判定，否则照样按格式缺陷报。
            if (string.Equals(r.Reason, MilkInputRejection.ReasonNameUnresolved, StringComparison.Ordinal)
                && r.EvidenceOk)
            {
                games = teamGames?.Invoke(r.HomeTeam) ?? -1;
                var awayGames = teamGames?.Invoke(r.AwayTeam) ?? -1;
                var known = Math.Max(games, awayGames);
                if (known < 0)
                    defect = "无法证明是真新队（独立库检不可用）";
                else if (known >= minTeamGames)
                    defect = $"队名存疑：库内该队近 18 个月有 {known} 场（门槛 {minTeamGames}），"
                             + "属于队名归一化缺陷，不是新队";
                else
                {
                    // 真新队：升级原因为 new-team，并把库检结果写进证据，事后可复核。
                    final.Add(new MilkInputRejection
                    {
                        MatchId = r.MatchId, MatchNo = r.MatchNo, LeagueName = r.LeagueName,
                        HomeTeam = r.HomeTeam, AwayTeam = r.AwayTeam, SalesDate = r.SalesDate,
                        Reason = MilkInputRejection.ReasonNewTeam,
                        Evidence = $"{r.Evidence};dbGames={Math.Max(known, 0)};min={minTeamGames}",
                        Accepted = true
                    });
                    continue;
                }
            }

            if (defect.Length > 0)
                issues.Add(new MilkLedgerIssue
                {
                    Table = "source", Kind = "rejected-rejection",
                    Key = $"{r.MatchId}|{r.MatchNo}|{r.Reason}|{defect}", Expected = 0, Actual = 0
                });

            final.Add(new MilkInputRejection
            {
                MatchId = r.MatchId, MatchNo = r.MatchNo, LeagueName = r.LeagueName,
                HomeTeam = r.HomeTeam, AwayTeam = r.AwayTeam, SalesDate = r.SalesDate,
                Reason = r.Reason, Evidence = r.Evidence,
                Accepted = defect.Length == 0 && r.AcceptedByRule
            });
        }

        // ── 源侧档位域：每个已启售玩法必须是完整域（2026-11-14 第八轮） ──
        //
        // 判据逐玩法、逐档位代码比对，**不硬套 54**：
        //   · 少一个 `BF|1:0` 与少一个 `BQ|3-3` 是两件事，报出来的玩法与缺档都不同；
        //   · 域外的脏代码（生产中 BQ 出现过 `平/胜`/`平/负`/`平/平`）不算「有档」，
        //     也不因为「多出来」而判失败——多档是数据卫生问题，缺档才是不覆盖。
        //
        // 对**所有**源场次都查，不只查算过的：一场被 no-odds 拒绝、源侧却只采到三个玩法时，
        // 若只查算过的场次，这个拒绝就永远无从反驳——正是「历史有记录 ≠ 本轮有效」的反面。
        foreach (var m in src)
        {
            foreach (var play in JcPlayDomain.Plays)
            {
                var domain = JcPlayDomain.Of(play);
                if (domain is null || domain.Count == 0) continue;

                var have = new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in m.OptionKeys)
                {
                    var bar = k.IndexOf('|');
                    if (bar <= 0) continue;
                    if (!string.Equals(k[..bar], play, StringComparison.Ordinal)) continue;
                    have.Add(k[(bar + 1)..]);
                }

                var missingTotal = 0;
                var sample = new List<string>(4);
                foreach (var code in domain)
                {
                    if (have.Contains(code)) continue;
                    missingTotal++;
                    if (sample.Count < 4) sample.Add(code);
                }
                if (missingTotal == 0) continue;

                issues.Add(new MilkLedgerIssue
                {
                    Table = "source", Kind = "source-option-gap",
                    Key = $"{m.MatchId}|{m.MatchNo}|{play}|缺 {missingTotal}/{domain.Count}"
                          + $"|{string.Join(",", sample)}{(missingTotal > sample.Count ? ",…" : "")}",
                    Expected = domain.Count, Actual = domain.Count - missingTotal
                });
            }
        }

        // ── 算过的场次：采样新鲜度 + 档位覆盖 ──
        var age = maxSampleAge ?? TimeSpan.FromMinutes(MaxSampleAgeMinutes);
        var srcById = src.ToDictionary(m => m.MatchId);
        foreach (var m in ins)
        {
            if (!srcById.TryGetValue(m.MatchId, out var s)) continue;

            if (s.OptionCount > 0)
            {
                if (s.LastSampleAt is not DateTime at)
                    issues.Add(new MilkLedgerIssue
                    {
                        Table = "source", Kind = "stale-sample",
                        Key = $"{m.MatchId}|{m.MatchNo}|no-sample", Expected = 1, Actual = 0
                    });
                else if (runAt - at > age)
                    issues.Add(new MilkLedgerIssue
                    {
                        Table = "source", Kind = "stale-sample",
                        Key = $"{m.MatchId}|{m.MatchNo}|age={(int)(runAt - at).TotalMinutes}min>{age.TotalMinutes:0}min",
                        Expected = 1, Actual = 0
                    });
            }

            // 源侧有档、输入凭证里没有 = 引擎在档位层面少算。之前这一层没人查：
            // 预期是从输入推出的，输入少一档，预期也跟着少一档，两边同时「对」。
            //
            // 只查**域内**档位（2026-11-14 第八轮）：源侧混进来的脏代码（生产中 BQ 出现过
            // `平/胜`）引擎本来就不该算，把它报成「引擎少算」会让一条脏数据卡住整轮发布，
            // 而它其实只是数据卫生问题。
            var have = new HashSet<string>(
                (m.Options ?? Array.Empty<MilkInputOption>())
                    .Select(o => $"{o.PlayType}|{o.OptionCode}"), StringComparer.Ordinal);
            foreach (var k in s.OptionKeys.Where(k => InDomain(k) && !have.Contains(k)))
                issues.Add(new MilkLedgerIssue
                {
                    Table = "source", Kind = "missing-option",
                    Key = $"{m.MatchId}|{m.MatchNo}|{k}", Expected = 1, Actual = 0
                });
        }

        // 引擎说「这场没有赔率」，源侧却记着有档 —— 两个源同时为真是不可能的。
        // 这正是「历史赔率有记录不等于本轮仍有效」的反向：**源侧有价**时，
        // 引擎拿不到价只可能是它自己取了空集（如查询参数错、快照未刷新），不能算合法拒绝。
        //
        // 被推翻的豁免要把 `Accepted` 也改回 false：否则落库的拒绝记录会带着「合法」标记，
        // 事后分不清「真没价」和「没取到价」——账面上两者长得一样，正是要避免的那种含糊。
        foreach (var r in final.Where(x => x.Accepted
                                          && string.Equals(x.Reason, MilkInputRejection.ReasonNoOdds, StringComparison.Ordinal)))
        {
            if (!srcById.TryGetValue(r.MatchId, out var s) || s.OptionCount == 0) continue;
            issues.Add(new MilkLedgerIssue
            {
                Table = "source", Kind = "contradicted-rejection",
                Key = $"{r.MatchId}|no-odds|source={s.OptionCount}档|sample={s.LastSampleAt:MM-dd HH:mm}",
                Expected = 0, Actual = s.OptionCount
            });
            r.Accepted = false;
        }

        return new MilkSourceReport
        {
            Issues = issues,
            Source = src,
            Rejections = final,
            ComputedCount = ins.Count(m => srcIds.Contains(m.MatchId)),
            RejectedCount = final.Count(r => r.Accepted)
        };
    }

    /// <summary>该键是否属于玩法域（`BF|1:0` 属域；`BQ|平/胜` 不属域）。</summary>
    private static bool InDomain(string key)
    {
        var bar = key.IndexOf('|');
        if (bar <= 0) return false;
        var domain = JcPlayDomain.Of(key[..bar]);
        if (domain is null) return false;
        var code = key[(bar + 1)..];
        foreach (var c in domain)
            if (string.Equals(c, code, StringComparison.Ordinal)) return true;
        return false;
    }
}
