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
    /// 规则本身不接受的原因（如 not-sale）恒 false。
    ///
    /// `name-unresolved` 自 2026-11-14 第十二轮起**恒不接受**：库检只能回答
    /// 「用这个名字查到几场」，回答不了「这支队是不是新的」（见 <c>Verify</c> 内注释）。
    /// </summary>
    public bool Accepted { get; set; }

    // ── 原因枚举 ──
    /// <summary>源侧已停售/已开赛，本不该进推荐：出现即说明上游过滤与本轮不一致。</summary>
    public const string ReasonNotSale = "not-sale";
    /// <summary>联赛在「完全不出注」或「不进串关」名单里（规则决定，与模型状态无关）。</summary>
    public const string ReasonLeagueExcluded = "league-excluded";
    /// <summary>该场进推荐时一条赔率都没有；证据必须与源侧档数一致（source=0）。</summary>
    public const string ReasonNoOdds = "no-odds";
    /// <summary>
    /// 这场已经开出，但赔率簿不是完整域。只拒绝这一场，不拿它阻断整轮。
    /// 证据是 <see cref="JcPlayDomain.GapEvidence"/>，并且必须和源侧缺档一致。
    /// </summary>
    public const string ReasonIncompleteOdds = "incomplete-odds";
    /// <summary>主客队名缺失，无法定位球队。</summary>
    public const string ReasonNameMissing = "name-missing";
    /// <summary>队名解析不出——**恒不接受**（2026-11-14 第十二轮），须补别名或人工确认。</summary>
    public const string ReasonNameUnresolved = "name-unresolved";
    /// <summary>模型认得队，但该队在本联赛样本不足（**身份已确认**，不是名字问题）。</summary>
    public const string ReasonThinSample = "thin-sample";
    /// <summary>比分分布算出空网格/零质量，无法给任何一档概率。</summary>
    public const string ReasonNoDistribution = "no-distribution";
    /// <summary>比分模型没就绪（样本 &lt; 30 场）：整轮没有可用的概率，不是「这场有问题」。</summary>
    public const string ReasonModelNotReady = "model-not-ready";

    /// <summary>
    /// 历史原因码：2026-11-14 第十一轮前由核对器把 `name-unresolved` 升级而来。
    /// **第十二轮起不再产生**——计数小不能证明是新队，身份未确认就是未确认。
    /// 保留常量只为读懂库里/日志里的旧行，规则恒为不接受。
    /// </summary>
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
            Condition = "历史拒绝证据仍按 NoPickLeagues / NoParlayLeagues 核对。2026-11-16 起竞彩大奶不再写出这类整场拒绝"
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
            Reason = ReasonIncompleteOdds,
            EvidencePrefix = "sale-basis=unavailable",
            Condition = "这场赔率簿不完整。证据必须等于源侧按标准域算出的缺档，"
                        + "只停这一场；赔率簿其实完整时这条拒绝不成立"
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
            Condition = "无正当条件（引擎自己认不出不等于该队没有样本，也不等于它是新队）",
            RejectNote = "name-unresolved 不接受：库检只能说明「用这个名字查到几场」，"
                         + "零计数既可能是真新队、也可能是同一支队换了个写法，二者在计数上不可分。"
                         + "只能补别名或人工确认（2026-11-14 第十二轮取消「计数 < 门槛即升级 new-team」）"
        },
        new()
        {
            Reason = ReasonNewTeam,
            EvidencePrefix = "team=",
            // ★ 这个原因码自第十二轮起**不再产生**：它原来由核对器把「计数 < 门槛」的
            // name-unresolved 升级而来，等于用计数证明身份，而计数恰恰分不清「新队」与「换写法」。
            // 规则恒不接受，只为读懂旧行/旧日志；调用方自报也按 name-unresolved 重走同一套判定。
            Accepted = false,
            Condition = "已停用（2026-11-14 第十二轮）：不再有产生它的路径",
            RejectNote = "new-team 不接受：该原因已停用。计数小不能证明是真新队，"
                         + "自报或历史轮次里的 new-team 一律按 name-unresolved 重判"
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

    /// <summary>按规则判定（不含任何「升级为接受」的路径——第十二轮起不存在这种路径）。</summary>
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

    public bool Ok => !Issues.Any(i => i.BlocksRound);

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
            var parked = Issues.Count(i => i.Kind == "source-option-gap" && !i.BlocksRound);
            var parkedText = parked == 0 ? "" : $"；缺档记在场上 {parked}";
            return $"源 {Source.Count} 场（算 {ComputedCount} / 拒 {RejectedCount}）；拒绝 {tail}{parkedText}";
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
///   ③ `name-unresolved` **恒不接受**（2026-11-14 第十二轮）：独立库检只能回答
///      「用这个名字查到几场」，回答不了「这支队是不是新的」——「谢周三」0 场与
///      「谢菲尔德星期三」74 场在计数上不可分，零计数既可能是真新队、也可能是换了个写法。
///      **样本不足另有原因 `thin-sample`（模型认出了这支队、身份已确认）**，两者不能混用；
///      身份未确认时只能补别名或人工确认，门槛值不动（仍是 3）；
///   ④ 算过的场次，其最新赔率采样不得超龄（默认 30 分钟）；超龄即「历史赔率不等于本轮有效」；
///   ⑤ 算过的场次，源侧每一个档位都必须出现在输入凭证的档位清单里——
///      少一档同样是「引擎少算」，不能靠「预期=输入」自动通过；
///   ⑥ **源侧自己少了档**也要发现（2026-11-14 第八轮）：每个已启售玩法必须是
///      <see cref="JcPlayDomain"/> 的完整档位域，少一档即 `source-option-gap`。
///      2026-11-16：缺档只停在这一场。这场没有输入凭证、并且已有一条被接受的拒绝时，
///      缺档仍写进问题，但 <see cref="MilkLedgerIssue.BlocksRound"/> 为 false，不让整轮失败。
///      这场已经算过（有输入凭证）时，缺档仍阻断整轮。
///      历史联赛排除仍只少报这一场的缺档，不删源场次、不删拒绝记录、不消其它问题。
///
/// 为什么 ⑥ 不能省：⑤ 只查「源有档 → 引擎没算」，源侧自己少采一档时预期和输入一起少，
/// 两边同时为「对」——与 1419319 同一种病因（预期取自被检验的一方）。
/// 也正因为如此，「采集里没有某玩法的赔率」**不得**被当成「该玩法未启售」：
/// 未启售需要源侧落库的启售清单才能证明，而库内没有这个字段（见 <see cref="SaleBasis"/>），
/// 所以仍按缺档记在该场。这是**无法判定时的保守记录**，不是「54 档齐全即合格」的验收标准，
/// 也不再拿一场缺档阻断整轮。问题键仍写 `sale-basis=unavailable`。
/// </summary>
public static class MilkSourceSlate
{
    /// <summary>赔率采样允许的最大年龄（分钟）。超龄不是「数据旧」，是「本轮不该拿它出票」。</summary>
    public const int MaxSampleAgeMinutes = MilkThresholds.MaxSampleAgeMinutes;

    /// <summary>
    /// 核对器用来判断「队名与样本是否自相矛盾」的门槛：库内 ≥ 这个数就说明
    /// **这个名字**确实有队，认不出只能是队名归一化缺陷。
    /// **与出注门槛同一个常量**（2026-11-14 第九轮）：这里原来是 5、引擎是 3，
    /// 一支 4 场样本的队会同时被引擎算进来、被核对器当新队放行。
    ///
    /// 注意它只用来**加严**（≥ 门槛 → 判队名存疑），**不用来放行**：
    /// &lt; 门槛 既可能是新队、也可能是换写法，第十二轮起一律阻断，门槛值本身不动。
    /// </summary>
    public const int DefaultMinTeamGames = MilkThresholds.MinTeamGames;

    /// <summary>
    /// 玩法启售依据（2026-11-14 第九轮核查结论）：**无法判定**。
    ///
    /// 查过的地方：`BettingCard` 只有 `SpfHome/Draw/Away` 三列（胜平负水位），
    /// 全库没有任何逐玩法的「本场启售/未启售」字段；采集入口 `FetchJcAllPlaysAsync`
    /// 是从 500 的四个页面各抓一张选项表，某玩法页没抓到（限流、改版、解析失败）
    /// 与「该场不卖这个玩法」在返回值上完全同形——都是空列表。
    ///
    /// 所以：**不得**从「赔率缺失」反推「未启售」，也**不得**把「一律要求满 54 档」
    /// 说成验收标准（那是「无法判定时保守阻断」的兜底，不是口径正确性的证明）。
    /// 要打开豁免口子，必须先让采集侧落库逐玩法启售清单，另行报批。
    /// </summary>
    public const string SaleBasis = "unavailable";

    /// <param name="teamGames">
    /// 独立库检：给定队名，返回该队近 18 个月的完赛场次。空/为 null 时视为**无法证明**。
    /// 返回值**只用于说明拒绝理由**（≥ 门槛 → 队名存疑），不构成放行依据：
    /// &lt; 门槛（含 0）自 2026-11-14 第十二轮起一律阻断。
    /// </param>
    /// <param name="nameVariants">
    /// 第二条独立事实（2026-11-14 第十一轮）：给定队名，返回库内**同一身份、名字相近**的写法
    /// （按 <see cref="TeamNames.SameIdentity"/> 过滤，梯队不算）。返回 null = 这项探查没做/查不到，
    /// 与「查到 0 条」是两件事。
    ///
    /// 第十二轮起它**只作诊断**：查到相近写法就把「这是队名解析缺陷」说清楚，查不到也不放行
    /// （计数小既可能是新队、也可能是换写法，这一项证明不了是前者）。
    /// </param>
    public static MilkSourceReport Verify(
        IReadOnlyList<MilkSourceMatch>? source,
        IReadOnlyList<MilkInputMatch>? inputs,
        IReadOnlyList<MilkInputRejection>? rejections,
        DateTime runAt,
        Func<string?, int>? teamGames = null,
        int minTeamGames = DefaultMinTeamGames,
        TimeSpan? maxSampleAge = null,
        Func<string?, IReadOnlyList<string>?>? nameVariants = null)
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

            // league-excluded 的前缀 `list=` 只证明「写成了这个原因」，不证明联赛真在名单里。
            // 证据必须拆成 list、league 各一次，再与源场次联赛名精确比对，并用现有名单复判。
            // 对不上就维持拒绝（rejected-rejection）；对得上也不在这里删其它问题。
            if (string.Equals(r.Reason, MilkInputRejection.ReasonLeagueExcluded, StringComparison.Ordinal)
                && !IsConfirmedLeagueExclusion(r, src.FirstOrDefault(m => m.MatchId == r.MatchId), out var leagueDefect))
                defect = leagueDefect;

            if (defect.Length == 0
                && string.Equals(r.Reason, MilkInputRejection.ReasonIncompleteOdds, StringComparison.Ordinal)
                && r.EvidenceOk)
            {
                var booked = src.FirstOrDefault(m => m.MatchId == r.MatchId);
                var expected = JcPlayDomain.GapEvidence(booked?.OptionKeys);
                if (booked is null || JcPlayDomain.Gaps(booked.OptionKeys).Count == 0)
                    defect = "赔率簿完整";
                else if (!string.Equals(r.Evidence, expected, StringComparison.Ordinal))
                    defect = "缺档证据与源侧不符";
            }

            // `name-unresolved` 不能靠引擎自证：名称认不出恰恰说明引擎那套名字解析在这个名字上失效了。
            // 但「库里查得到几场」也**证明不了它是新队**——所以下面两次独立查询（场次、相近写法）
            // 从第十二轮起只用来**说清拒绝的理由**，没有任何一条构成放行依据。
            //
            // 注意判据顺序：这条原因本身是 `Accepted=false`（默认不放行），
            // 所以不能先看 `Defect`——那会直接拿到「不接受」的说明，永远走不到库检。
            // 只有**证据格式成立**时才进入独立判定，否则照样按格式缺陷报。
            if ((string.Equals(r.Reason, MilkInputRejection.ReasonNameUnresolved, StringComparison.Ordinal)
                 || string.Equals(r.Reason, MilkInputRejection.ReasonNewTeam, StringComparison.Ordinal))
                && r.EvidenceOk)
            {
                var homeGames = teamGames?.Invoke(r.HomeTeam) ?? -1;
                var awayGames = teamGames?.Invoke(r.AwayTeam) ?? -1;
                var known = Math.Max(homeGames, awayGames);
                if (known < 0)
                    defect = "无法证明身份（独立库检不可用）：name-unresolved 保持拒绝";
                else if (known >= minTeamGames)
                    defect = $"队名存疑：库内该队近 18 个月有 {known} 场（门槛 {minTeamGames}），"
                             + "属于队名归一化缺陷，不是新队";
                else
                {
                    // ★ 取消「场次 < 门槛即升级 new-team」（2026-11-14 第十二轮，用户点名）。
                    //
                    // 计数全是「用**这个名字**查到几场」。而 name-unresolved 的成因恰恰是名字写法不同：
                    // 「谢周三」按本名 0 场 / 「谢菲尔德星期三」74 场；「不来梅」0 场 / 「云达不莱梅」58 场。
                    // 同一个零既可能是「真新队」，也可能是「同一支队换了个写法」——两者在计数上不可分，
                    // 所以计数（0 或 1~2 都一样）不能作为身份证据。
                    //
                    // 样本不足有专门的原因 `thin-sample`：那是**身份已确认**（模型认出了这支队）
                    // 只是本联赛样本不够。这里身份**没确认**，不能借用它的语义放行，门槛值也不动。
                    //
                    // 变体探查仍然跑，但只作诊断：查到相近写法就把「这是队名解析缺陷」说清楚；
                    // 查不到也不放行——「附近没有相似写法」不等于「这支队是新的」。
                    var similar = new List<string>();
                    var probed = nameVariants is not null;
                    foreach (var (name, gamesOfSide) in new[] { (r.HomeTeam, homeGames), (r.AwayTeam, awayGames) })
                    {
                        // 这一侧样本已够门槛（>= 门槛），上面已判死；-1 同理，不必再纠结名字。
                        if (gamesOfSide < 0 || gamesOfSide >= minTeamGames) continue;
                        var found = nameVariants?.Invoke(name);
                        if (found is null) { probed = false; continue; }
                        foreach (var v in found)
                            if (!string.IsNullOrWhiteSpace(v)) similar.Add($"{name}≈{v}");
                    }

                    var count = known == 0 ? "按本名查得 0 场" : $"按本名只查到 {known} 场";
                    var hint = similar.Count > 0
                        ? $"；库内存在同一身份的相近写法（{string.Join("、", similar.Take(3))}），属队名解析缺陷"
                        : probed
                            ? "；库内没有同一身份的其它写法，但这不能证明该队是新的"
                            : "；变体探查未完成（不足以说明任何一侧）";
                    defect = $"队名无法确认（{count}，门槛 {minTeamGames}）{hint}。"
                             + "解析失败时零计数与低计数都不能作为新队证明，本轮阻断，"
                             + "须补别名或人工确认";
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
        //
        // 历史联赛排除仍整段跳过缺档（旧证据，2026-11-15）。
        // 2026-11-16：其它已经停在该场的拒绝也把缺档留在该场，不再阻断整轮。
        // 这场有输入凭证，说明已经拿不完整的赔率簿算过，缺档仍阻断。
        foreach (var m in src)
        {
            if (ExemptSourceOptionGap(m, rej, inIds)) continue;
            var parked = GapStaysOnMatch(m, final, inIds);
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
                    have.Add(JcPlayDomain.Normalize(k[(bar + 1)..]));
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
                          + $"|{string.Join(",", sample)}{(missingTotal > sample.Count ? ",…" : "")}"
                          + $"|sale-basis={SaleBasis}",
                    Expected = domain.Count, Actual = domain.Count - missingTotal,
                    BlocksRound = !parked
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

    /// <summary>
    /// 这一场的 <c>source-option-gap</c> 可否不报。
    /// 有输入凭证一律不可。同场拒绝不是恰好一条、或该条不是已确认的联赛排除，也不可。
    /// </summary>
    private static bool ExemptSourceOptionGap(
        MilkSourceMatch match,
        IReadOnlyList<MilkInputRejection> rejections,
        IReadOnlySet<long> inputIds)
    {
        if (inputIds.Contains(match.MatchId)) return false;

        MilkInputRejection? only = null;
        var count = 0;
        foreach (var r in rejections)
        {
            if (r.MatchId != match.MatchId) continue;
            count++;
            only = r;
            if (count > 1) return false;
        }

        return count == 1 && only is not null && IsConfirmedLeagueExclusion(only, match, out _);
    }

    /// <summary>
    /// 这场没有进入计算，并且恰好有一条已被接受的拒绝。
    /// 缺档就记在这条拒绝旁边，不拿去阻断整轮。
    /// </summary>
    private static bool GapStaysOnMatch(
        MilkSourceMatch match,
        IReadOnlyList<MilkInputRejection> rejections,
        IReadOnlySet<long> inputIds)
    {
        if (inputIds.Contains(match.MatchId)) return false;

        MilkInputRejection? only = null;
        var count = 0;
        foreach (var rejection in rejections)
        {
            if (rejection.MatchId != match.MatchId) continue;
            count++;
            only = rejection;
            if (count > 1) return false;
        }

        return count == 1 && only is { Accepted: true };
    }

    /// <summary>
    /// 源侧赔率簿不完整的场次退出本轮计算：删掉它的注、凭证和快照，改记一条拒绝。
    /// 一条赔率都没有用 <see cref="MilkInputRejection.ReasonNoOdds"/>，
    /// 有残缺用 <see cref="MilkInputRejection.ReasonIncompleteOdds"/>。
    /// 其它场次的注单不动。
    /// </summary>
    public static void ParkIncompleteBooks(MilkBuildResult build, IReadOnlyList<MilkSourceMatch>? source)
    {
        if (build is null || source is null) return;
        foreach (var match in source)
        {
            if (match.MatchId <= 0 || JcPlayDomain.Gaps(match.OptionKeys).Count == 0) continue;
            var id = match.MatchId;
            build.Picks.RemoveAll(p => p.MatchId == id);
            build.Inputs.RemoveAll(p => p.MatchId == id);
            build.ProbSnapshots.RemoveAll(p => p.MatchId == id);
            build.Gates.RemoveAll(p => p.MatchId == id);
            build.ShowRows.RemoveAll(p => p.MatchId == id);
            build.CounterCandidates.RemoveAll(p => p.MatchId == id);
            build.SlateExemptions.RemoveAll(p => p.MatchId == id);
            build.Rejections.RemoveAll(p => p.MatchId == id);

            var empty = match.OptionCount == 0;
            build.Rejections.Add(new MilkInputRejection
            {
                MatchId = id,
                MatchNo = match.MatchNo,
                LeagueName = match.LeagueName,
                HomeTeam = match.HomeTeam,
                AwayTeam = match.AwayTeam,
                SalesDate = match.SalesDate,
                Reason = empty ? MilkInputRejection.ReasonNoOdds : MilkInputRejection.ReasonIncompleteOdds,
                Evidence = empty
                    ? "options=0;sale-basis=" + SaleBasis
                    : JcPlayDomain.GapEvidence(match.OptionKeys)
            });
        }
    }

    /// <summary>
    /// 历史拒绝证据是否独立构成「明确联赛排除」。
    /// 2026-11-16 起竞彩大奶不再写出整场联赛拒绝；这里只核对已经落库的旧证据。
    /// 只认 <c>list</c>、<c>league</c> 各出现一次；缺字段、重复或冲突、未知 list、
    /// 与源场次联赛名不一致，都返回 false。名单只用 <see cref="NoPickLeagues"/> /
    /// <see cref="NoParlayLeagues"/>，不另建一张。
    /// </summary>
    private static bool IsConfirmedLeagueExclusion(
        MilkInputRejection rejection, MilkSourceMatch? source, out string defect)
    {
        defect = string.Empty;
        if (!string.Equals(rejection.Reason, MilkInputRejection.ReasonLeagueExcluded, StringComparison.Ordinal))
        {
            defect = "不是联赛排除";
            return false;
        }

        if (!rejection.EvidenceOk)
        {
            defect = rejection.Defect;
            return false;
        }

        if (!TryReadLeagueFields(rejection.Evidence, out var listName, out var leagueName, out defect))
            return false;

        if (source is null || !string.Equals(leagueName, source.LeagueName, StringComparison.Ordinal))
        {
            defect = "联赛不一致";
            return false;
        }

        var listed = listName switch
        {
            "NoPickLeagues" => NoPickLeagues.IsExcluded(source.LeagueName),
            "NoParlayLeagues" => NoParlayLeagues.IsExcluded(source.LeagueName),
            _ => false
        };
        if (!listed)
        {
            defect = "联赛不一致";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把证据拆成字段。<c>list</c> 与 <c>league</c> 必须各有一个非空值，且全串每个分号段都是 <c>key=value</c>。
    /// 其它键忽略，但不能缺等号。同一键出现两次即重复；两次的值不同同时算冲突。
    /// </summary>
    private static bool TryReadLeagueFields(
        string evidence, out string listName, out string leagueName, out string defect)
    {
        listName = string.Empty;
        leagueName = string.Empty;
        defect = string.Empty;
        string? list = null;
        string? league = null;
        var listCount = 0;
        var leagueCount = 0;
        var listConflict = false;
        var leagueConflict = false;

        foreach (var raw in evidence.Split(';'))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0)
            {
                defect = "证据字段无法解析";
                return false;
            }

            var key = raw[..eq];
            var val = raw[(eq + 1)..];
            if (key == "list")
            {
                listCount++;
                if (list is null) list = val;
                else if (!string.Equals(list, val, StringComparison.Ordinal)) listConflict = true;
            }
            else if (key == "league")
            {
                leagueCount++;
                if (league is null) league = val;
                else if (!string.Equals(league, val, StringComparison.Ordinal)) leagueConflict = true;
            }
        }

        if (listCount == 0 || leagueCount == 0 || string.IsNullOrEmpty(list) || string.IsNullOrEmpty(league))
        {
            defect = "缺字段";
            return false;
        }

        if (listCount > 1 || leagueCount > 1 || listConflict || leagueConflict)
        {
            defect = "重复冲突字段";
            return false;
        }

        if (list != "NoPickLeagues" && list != "NoParlayLeagues")
        {
            defect = "未知list";
            return false;
        }

        listName = list;
        leagueName = league;
        return true;
    }

    /// <summary>该键是否属于玩法域（`BF|1:0` 属域；`BQ|平/胜` 不属域）。</summary>
    private static bool InDomain(string key)
    {
        var bar = key.IndexOf('|');
        if (bar <= 0) return false;
        return JcPlayDomain.Contains(key[..bar], key[(bar + 1)..]);
    }
}
