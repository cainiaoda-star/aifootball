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
/// 一条「合法缺项」：某一档按规则不该出现，或引擎有明确理由没生成。
///
/// 为什么必须显式登记（2026-11-13 用户要求「合法缺项明确记录原因」）：
/// 不登记的话，核对只能二选一——要么按引擎输出当预期（那引擎漏了就永远发现不了），
/// 要么严格按输入当预期（那脏档位、拿不到联合分布这类正常情况天天报警）。
/// 登记之后两条路都成立：**预期来自输入，每一个不写的原因都必须报出来**，
/// 而且原因只能是下面这几个固定值，不接受自由文本（否则「缺项」会变成万能挡箭牌）。
/// </summary>
public sealed class MilkSlateExemption
{
    public long MatchId { get; init; }
    public string? MatchNo { get; init; }
    public string PlayType { get; init; } = string.Empty;

    /// <summary>具体档位；整场级的原因（如完全没有赔率）用 `*`。</summary>
    public string OptionCode { get; init; } = "*";

    public string Reason { get; init; } = string.Empty;

    // ── 原因枚举（只认这几个） ──
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
    /// <summary>引擎整体没有跑（输入为空/赔率为空，`BuildDetailed` 提前返回）。</summary>
    public const string ReasonEngineSkipped = "engine-skipped";

    public static readonly string[] KnownReasons =
    {
        ReasonNoOdds, ReasonCodeOutsideDomain, ReasonUnknownPlay,
        ReasonDirtyHandicapOption, ReasonHalfFullJointMissing, ReasonEngineSkipped
    };

    /// <summary>原因必须是已知值；未知原因本身就是缺陷（会被判失败，不当缺项放过）。</summary>
    public bool IsKnown => Array.IndexOf(KnownReasons, Reason) >= 0;

    /// <summary>与 <see cref="MilkRunLedger.Keys.Snapshot"/> 同一口径，用于从预期里剔掉这一档。</summary>
    public string Key => MilkRunLedger.Keys.Snapshot(MatchId, PlayType, OptionCode);

    public override string ToString()
        => $"{MatchNo ?? MatchId.ToString(CultureInfo.InvariantCulture)} {PlayType} {OptionCode}：{Reason}";
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

    /// <summary>未知原因的豁免（=缺陷，调用方必须判失败）。</summary>
    public IReadOnlyList<MilkSlateExemption> UnknownReasons
        => Exemptions.Where(e => !e.IsKnown).ToList();

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
        var ignore = new HashSet<string>(StringComparer.Ordinal);

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
                    PlayType = "*", OptionCode = "*", Reason = MilkSlateExemption.ReasonNoOdds
                };
                exempts.Add(e);
                ignore.Add(e.Key);
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
                        Reason = MilkSlateExemption.ReasonUnknownPlay
                    });
                    ignore.Add(key);
                    continue;
                }

                if (!Domains.Contains(play, code))
                {
                    exempts.Add(new MilkSlateExemption
                    {
                        MatchId = m.MatchId, MatchNo = m.MatchNo,
                        PlayType = play, OptionCode = code,
                        Reason = MilkSlateExemption.ReasonCodeOutsideDomain
                    });
                    ignore.Add(key);
                    continue;
                }

                rows.Add(new MilkLedgerRow { Key = key });
            }
        }

        foreach (var e in engineExempts ?? Array.Empty<MilkSlateExemption>())
        {
            if (e.MatchId <= 0) continue;
            exempts.Add(e);
            ignore.Add(e.Key);
        }

        var kept = rows.Where(r => !ignore.Contains(r.Key)).ToList();
        return new MilkSlateBuild
        {
            Rows = kept,
            Exemptions = exempts,
            IgnoreKeys = ignore.ToList()
        };
    }
}
