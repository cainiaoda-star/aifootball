using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiFootBall.Core.Models;

/// <summary>
/// 输入里的一档赔率（2026-11-13 第五轮）。
///
/// 只记「进计算时这一场有哪些档、价格多少」，不记概率——概率是引擎算出来的，
/// 拿它当预期就等于用答案验答案。这个清单才是「该有几档」的独立依据。
/// </summary>
public sealed class MilkInputOption
{
    public string PlayType { get; init; } = string.Empty;
    public string OptionCode { get; init; } = string.Empty;
    public decimal Odds { get; init; }
}

/// <summary>
/// 一条原因规则（2026-11-13 第六轮，用户要求「engine-skipped 不得作为通用放行理由，
/// 必须有明确条件和证据」）。
///
/// 只登记「这是什么原因」是不够的：原因名是个字符串，谁都能填。所以每条原因都绑定
/// ①允许条件（什么时候才算数）②证据前缀（必须拿得出可核对的量）。
/// **没有证据、或证据与该原因不符，一律判失败**，不享受豁免。
/// </summary>
public sealed class MilkSlateReasonRule
{
    public string Reason { get; init; } = string.Empty;

    /// <summary>允许条件，人读；写进发布日志，事后判「这个豁免当时成不成立」。</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>证据必须以它开头；空证据 = 没有证据。</summary>
    public string EvidencePrefix { get; init; } = string.Empty;

    /// <summary>false = 这条原因不被接受，出现即判失败（不是「缺项」，是缺陷）。</summary>
    public bool Accepted { get; init; } = true;

    /// <summary>不接受时的说法，直接进失败原因。</summary>
    public string? RejectNote { get; init; }
}

/// <summary>
/// 一条「合法缺项」：某一档按规则不该出现，或引擎有明确理由没生成。
///
/// 为什么必须显式登记（2026-11-13 用户要求「合法缺项明确记录原因」）：
/// 不登记的话，核对只能二选一——要么按引擎输出当预期（那引擎漏了就永远发现不了），
/// 要么严格按输入当预期（那脏档位、拿不到联合分布这类正常情况天天报警）。
/// 登记之后两条路都成立：**预期来自输入，每一个不写的原因都必须报出来**，
/// 而且原因只能是固定枚举 + 必须带证据（否则「缺项」会变成万能挡箭牌）。
/// </summary>
public sealed class MilkSlateExemption
{
    public long MatchId { get; init; }
    public string? MatchNo { get; init; }
    public string PlayType { get; init; } = string.Empty;

    /// <summary>具体档位；整场级的原因（如完全没有赔率）用 `*`。</summary>
    public string OptionCode { get; init; } = "*";

    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// 证据：这条豁免当时「凭什么成立」的可核对量，形如 `options=0`、`code=JQ/8`、`points=9;joint=absent`。
    /// 空证据不被接受——否则写个原因名就能放行，等于把核对关掉。
    /// </summary>
    public string Evidence { get; init; } = string.Empty;

    // ── 原因枚举（只认这几个，且都必须带证据） ──
    /// <summary>该场在输入里一个赔率档都没有：没有可存档的东西，不是漏写。</summary>
    public const string ReasonNoOdds = "no-odds";
    /// <summary>档位代码不在该玩法的官方档位域内（JQ 只到 7+、BQ 九个组合、SPF/NSPF 三向）。</summary>
    public const string ReasonCodeOutsideDomain = "code-outside-domain";
    /// <summary>玩法名不认识（数据侧脏值），无法判定该存几档。</summary>
    public const string ReasonUnknownPlay = "unknown-play";
    /// <summary>让球胜平负有让球数但这档不认识：脏档，按老逻辑跳过（见 CollectSnapshots）。</summary>
    public const string ReasonDirtyHandicapOption = "nspf-dirty-option";
    /// <summary>半全场算不出 3×3 联合分布：这一场没有任何半全场概率可存。</summary>
    public const string ReasonHalfFullJointMissing = "bq-joint-unavailable";

    /// <summary>
    /// 引擎整体跳过的「理由」（2026-11-13 第六轮：**明确不接受**）。
    ///
    /// 上一版把它放进枚举，等于给了一条「整场/整轮什么都可以不写」的通用放行口子：
    /// 它既没有触发条件，也没有证据要求，谁都能用。现在的处置：
    ///   · 整轮没有输入 → 预期与实际两边都是 0 行，本来就通过，不需要豁免；
    ///   · 单场没有赔率 → 必须逐场按 `no-odds` 申报，证据 `options=0`，且该场不得出票。
    /// 所以它没有存在的必要，出现即判失败（见 <see cref="Rules"/> 里的 Accepted=false）。
    /// </summary>
    public const string ReasonEngineSkipped = "engine-skipped";

    /// <summary>原因规则表。顺序即文档顺序。</summary>
    public static readonly MilkSlateReasonRule[] Rules =
    {
        new()
        {
            Reason = ReasonNoOdds,
            EvidencePrefix = "options=0",
            Condition = "该场进计算时赔率档清单为空；同时该场不得出现在注单里（出票即判失败，"
                        + "见 MilkExpectedSlate.CrossCheck——赔率缺失不允许借豁免继续推荐）"
        },
        new()
        {
            Reason = ReasonCodeOutsideDomain,
            EvidencePrefix = "code=",
            Condition = "档位代码不在该打法官方档位域内（SPF/NSPF 三向、BQ 九组合、JQ 0~7）"
        },
        new()
        {
            Reason = ReasonUnknownPlay,
            EvidencePrefix = "play=",
            Condition = "玩法名不在 SPF/NSPF/BQ/JQ/BF 之内（数据侧脏值）"
        },
        new()
        {
            Reason = ReasonDirtyHandicapOption,
            EvidencePrefix = "handicap=",
            Condition = "有让球数，但档位代码不是 3/1/0"
        },
        new()
        {
            Reason = ReasonHalfFullJointMissing,
            EvidencePrefix = "points=",
            Condition = "该场 3×3 联合分布算不出（grid/joint 缺失），整类半全场没有可存概率；"
                        + "证据须给出该场半全场档数"
        },
        new()
        {
            Reason = ReasonEngineSkipped,
            EvidencePrefix = "(不接受)",
            Accepted = false,
            Condition = "无正当条件",
            RejectNote = "engine-skipped 不接受：整轮无输入时账本两边都是 0 不需豁免；"
                         + "单场无赔率必须逐场按 no-odds 申报并带 options=0 证据"
        }
    };

    public MilkSlateReasonRule? Rule
        => Array.Find(Rules, r => string.Equals(r.Reason, Reason, StringComparison.Ordinal));

    /// <summary>原因必须是已知值；未知原因本身就是缺陷（会被判失败，不当缺项放过）。</summary>
    public bool IsKnown => Rule is not null;

    public bool HasEvidence => !string.IsNullOrWhiteSpace(Evidence);

    /// <summary>证据是否与原因相符（前缀匹配）。</summary>
    public bool EvidenceOk
    {
        get
        {
            var rule = Rule;
            return rule is not null && HasEvidence
                   && Evidence.StartsWith(rule.EvidencePrefix, StringComparison.Ordinal);
        }
    }

    /// <summary>被接受的豁免：原因在枚举内、规则允许、证据齐且对得上。缺一不可。</summary>
    public bool Accepted => Rule is { Accepted: true } && EvidenceOk;

    /// <summary>不被接受的原因（空串 = 没问题）。直接进失败原因，说清是哪一环不成立。</summary>
    public string Defect
    {
        get
        {
            var rule = Rule;
            if (rule is null) return "原因不在枚举内";
            // 先判「这条原因本身不接受」：它无论证据多齐全都站不住，
            // 报成「证据不符」会把读者引去补证据，方向就错了。
            if (!rule.Accepted) return rule.RejectNote ?? "该原因不接受";
            if (!HasEvidence) return $"缺证据（应为 {rule.EvidencePrefix}…）";
            if (!EvidenceOk) return $"证据与原因不符（应为 {rule.EvidencePrefix}…，实际 {Trim(Evidence)}）";
            return string.Empty;
        }
    }

    private static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>与 <see cref="MilkRunLedger.Keys.Snapshot"/> 同一口径，用于从预期里剔掉这一档。</summary>
    public string Key => MilkRunLedger.Keys.Snapshot(MatchId, PlayType, OptionCode);

    public override string ToString()
        => $"{MatchNo ?? MatchId.ToString(CultureInfo.InvariantCulture)} {PlayType} {OptionCode}："
           + $"{Reason}({Evidence})";
}

/// <summary>独立预期账本 + 它自己的豁免清单。</summary>
public sealed class MilkSlateBuild
{
    /// <summary>按「输入 × 玩法规则」推出的应存档位（已剔除豁免项、已按档去重）。</summary>
    public IReadOnlyList<MilkLedgerRow> Rows { get; init; } = Array.Empty<MilkLedgerRow>();

    /// <summary>所有豁免（自动识别 + 引擎申报），按出现顺序。</summary>
    public IReadOnlyList<MilkSlateExemption> Exemptions { get; init; } = Array.Empty<MilkSlateExemption>();

    /// <summary>不参与判定的键（豁免项）。既不出现在预期里，也不会被当成「多写」。</summary>
    public IReadOnlyCollection<string> IgnoreKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 不被接受的豁免（=缺陷，调用方必须判失败）：
    /// 原因不在枚举内 / 缺证据 / 证据与原因不符 / 该原因本身不接受（engine-skipped）。
    /// </summary>
    public IReadOnlyList<MilkSlateExemption> Rejected
        => Exemptions.Where(e => !e.Accepted).ToList();

    /// <summary>形如 `预期存档 96 档；合法缺项 3 条（code-outside-domain 2、no-odds 1）`。</summary>
    public string Summary
    {
        get
        {
            var byReason = Exemptions
                .GroupBy(e => e.Reason)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} {g.Count()}");
            var tail = Exemptions.Count == 0 ? "无" : string.Join("、", byReason);
            return $"预期存档 {Rows.Count} 档；合法缺项 {Exemptions.Count} 条（{tail}）";
        }
    }

    /// <summary>
    /// 豁免明细（最多 8 条，带证据），写发布日志。
    /// 只报数不报证据的话，日志里只会留下「有 3 条豁免」，事后无法判断成不成立。
    /// </summary>
    public string Detail
    {
        get
        {
            if (Exemptions.Count == 0) return "无豁免";
            var head = Exemptions.Take(8).Select(e => e.ToString());
            var more = Exemptions.Count > 8 ? $"；另有 {Exemptions.Count - 8} 条" : string.Empty;
            return string.Join("；", head) + more;
        }
    }
}

/// <summary>
/// 独立预期档位生成器（2026-11-13 第五轮，用户明确要求）。
///
/// 为什么上一版不够：那一版的「预期」是 `build.ProbSnapshots`——引擎自己的输出。
/// 拿输出当预期，只能证明「算出来的都写进去了」，证明不了「该算的都算了」。
/// 引擎少生成一场、少生成一档，两边同时少，核对照样通过、照样发布，
/// 而榜面上就是少了几注，谁也不知道。
///
/// 这一版把预期挪到**输入侧**：`MilkInputMatch.Options` 记的是「这一场进计算时有哪些档」，
/// 档位域由玩法规则给出（<see cref="Domains"/>），两者相乘即预期。
/// 于是「少生成一场」= 该场全部档 missing，「少生成一档」= 那一档 missing，都会失败。
///
/// 规则（存档一道闸都不过，这是 11-12 定下的口径）：
///   · 输入里**每一个**在档位域内的赔率档，都应有一行存档；
///   · 不在域内的档（脏代码、还没有的玩法）不存，但必须记原因；
///   · 引擎因内部分布算不出来而整类跳过（半全场联合分布缺失）必须自己申报，不能悄悄少写。
/// </summary>
public static class MilkExpectedSlate
{
    /// <summary>玩法档位域。BF 不设固定域（比分档由源站给，有多少存多少）。</summary>
    public static class Domains
    {
        public static readonly string[] ThreeWay = { "3", "1", "0" };

        /// <summary>半全场 9 个组合，与 <c>MilkPickEngine.HalfFullCombos</c> 同一份（避免两处各写一遍）。</summary>
        public static readonly string[] HalfFull =
        {
            "3-3", "3-1", "3-0",
            "1-3", "1-1", "1-0",
            "0-3", "0-1", "0-0"
        };

        /// <summary>总进球 0~7（7 = 7+）。</summary>
        public const int JqMax = 7;

        public static bool Contains(string? playType, string? code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            return playType switch
            {
                JcPlayTypes.Spf or JcPlayTypes.Nspf => ThreeWay.Contains(code, StringComparer.Ordinal),
                JcPlayTypes.Bq => HalfFull.Contains(code, StringComparer.Ordinal),
                JcPlayTypes.Jq => int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                                  && n >= 0 && n <= JqMax,
                // 比分：源站给几档存几档，档位代码不在本地维护（"胜其他"/"其它"这类也在里面）。
                JcPlayTypes.Bf => true,
                _ => false
            };
        }

        public static bool IsKnownPlay(string? playType)
            => playType is JcPlayTypes.Spf or JcPlayTypes.Nspf or JcPlayTypes.Bq
                       or JcPlayTypes.Jq or JcPlayTypes.Bf;
    }

    /// <summary>
    /// 构建预期。`engineExempts` 是引擎自己申报的缺项（例如半全场联合分布缺失），
    /// 位置见 `MilkPickEngine.CollectSnapshots`。
    /// </summary>
    public static MilkSlateBuild Build(
        IEnumerable<MilkInputMatch>? inputs,
        IEnumerable<MilkSlateExemption>? engineExempts = null)
    {
        var rows = new List<MilkLedgerRow>();
        var exempts = new List<MilkSlateExemption>();

        foreach (var m in inputs ?? Array.Empty<MilkInputMatch>())
        {
            if (m.MatchId <= 0) continue;
            var options = m.Options ?? Array.Empty<MilkInputOption>();

            if (options.Count == 0)
            {
                // 进了计算、却一个赔率档都没有：没有任何东西应被存档。
                // 这一条必须记，否则「整场漏写」与「本来就没档」在核对里长得一样。
                var e = new MilkSlateExemption
                {
                    MatchId = m.MatchId, MatchNo = m.MatchNo,
                    PlayType = "*", OptionCode = "*", Reason = MilkSlateExemption.ReasonNoOdds,
                    Evidence = "options=0"
                };
                exempts.Add(e);
                continue;
            }

            // 同一档重复出现（采集端偶发）只算一档——存档侧在 CollectSnapshots 里也是按档去重。
            foreach (var grp in options.GroupBy(o => (o.PlayType, o.OptionCode)))
            {
                var play = grp.Key.PlayType ?? string.Empty;
                var code = grp.Key.OptionCode ?? string.Empty;
                var key = MilkRunLedger.Keys.Snapshot(m.MatchId, play, code);

                if (!Domains.IsKnownPlay(play))
                {
                    exempts.Add(new MilkSlateExemption
                    {
                        MatchId = m.MatchId, MatchNo = m.MatchNo,
                        PlayType = play, OptionCode = code,
                        Reason = MilkSlateExemption.ReasonUnknownPlay,
                        Evidence = $"play={play}"
                    });
                    continue;
                }

                if (!Domains.Contains(play, code))
                {
                    exempts.Add(new MilkSlateExemption
                    {
                        MatchId = m.MatchId, MatchNo = m.MatchNo,
                        PlayType = play, OptionCode = code,
                        Reason = MilkSlateExemption.ReasonCodeOutsideDomain,
                        Evidence = $"code={play}/{code}"
                    });
                    continue;
                }

                rows.Add(new MilkLedgerRow { Key = key });
            }
        }

        foreach (var e in engineExempts ?? Array.Empty<MilkSlateExemption>())
        {
            if (e.MatchId <= 0) continue;
            exempts.Add(e);
        }

        // 只有**被接受**的豁免才把对应键从判定里剔掉（2026-11-13 第六轮）。
        // 上一版无条件 ignore：一条被拒的豁免（如 engine-skipped）也会顺手把行抹掉，
        // 于是「拒绝」与「放行」在账本上表现一样，只是多了一行报错。
        // 现在被拒的豁免不产生 ignore，预期仍是「按输入该有的全部档位」，
        // 少写照样报 missing——两道判据互不抵消。
        var ignore = new HashSet<string>(
            exempts.Where(e => e.Accepted).Select(e => e.Key), StringComparer.Ordinal);

        var kept = rows.Where(r => !ignore.Contains(r.Key)).ToList();
        return new MilkSlateBuild
        {
            Rows = kept,
            Exemptions = exempts,
            IgnoreKeys = ignore.ToList()
        };
    }

    /// <summary>
    /// 豁免与注单的交叉核对（2026-11-13 第六轮，用户要求「赔率缺失也不能借豁免绕过推荐限制」）。
    ///
    /// `no-odds` 只说明「这一场没有可存档的赔率档」，它**不能**同时被理解成
    /// 「这一场可以照常出票」（那就是赔率缺失退纯模型那条未修风险换个名字）。
    /// 所以一条 no-odds 豁免必须与该场没有注单同时成立：
    /// 只要该场出现在注单里，就说明推荐是在没有赔率的前提下产生的，本轮直接判失败。
    /// </summary>
    public static List<MilkLedgerIssue> CrossCheck(
        MilkSlateBuild? slate, IEnumerable<MilkPick>? picks)
    {
        var issues = new List<MilkLedgerIssue>();
        if (slate is null) return issues;

        var noOdds = slate.Exemptions
            .Where(e => e.Reason == MilkSlateExemption.ReasonNoOdds)
            .Select(e => e.MatchId)
            .ToHashSet();
        if (noOdds.Count == 0) return issues;

        var picked = (picks ?? Array.Empty<MilkPick>())
            .Where(p => p.MatchId is long mid && noOdds.Contains(mid))
            .GroupBy(p => p.MatchId!.Value);

        foreach (var g in picked)
            issues.Add(new MilkLedgerIssue
            {
                Table = "pick",
                Kind = "exempt-violated",
                Key = $"match={g.Key}|no-odds|picks={g.Count()}",
                Expected = 0,
                Actual = g.Count()
            });

        return issues;
    }
}
