using System;
using System.Collections.Generic;

namespace AiFootBall.Data;

/// <summary>
/// 历史战绩口径核对结果（2026-11-13 第四轮，只读）。
/// 生成于 <see cref="MilkPickRepository.GetLedgerAuditAsync"/>。
/// </summary>
public sealed class MilkLedgerAudit
{
    public int Days { get; init; }
    public DateTime From { get; init; }

    /// <summary>库里的原始行数（含各轮副本）。</summary>
    public int AllRows { get; init; }

    /// <summary>按 (受注日, 场次, 玩法, 档位) 去重后的注数。</summary>
    public int DistinctBets { get; init; }

    /// <summary>升级前写入、RunId 为空的行数。</summary>
    public int LegacyRows { get; init; }

    /// <summary>带 RunId 的行数。</summary>
    public int RunRows { get; init; }

    /// <summary>已结算行数（逐行）。</summary>
    public int SettledRows { get; init; }

    /// <summary>撤注行数（逐行）。</summary>
    public int VoidedRows { get; init; }

    /// <summary>升级前已结算的行数。</summary>
    public int LegacySettled { get; init; }

    /// <summary>上述行在**新口径**里存活下来的数量。与 <see cref="LegacySettled"/> 不等即为历史被吞。</summary>
    public int LegacySettledInNew { get; init; }

    public int OldSettled { get; init; }
    public int OldHit { get; init; }
    public int NewSettled { get; init; }
    public int NewHit { get; init; }

    public IReadOnlyList<MilkLedgerAuditRow> Plays { get; init; } = Array.Empty<MilkLedgerAuditRow>();

    /// <summary>多副本造成的重复行数。`AllRows - DistinctBets` 应当全部来自这里。</summary>
    public int DuplicateRows => AllRows - DistinctBets;

    /// <summary>升级前的已结算记录是否全部保留在新口径里。</summary>
    public bool LegacyPreserved => LegacySettled == LegacySettledInNew;

    /// <summary>新口径的注数不该超过去重后的注数（超过说明去重没生效）。</summary>
    public bool NoInflation => NewSettled <= DistinctBets;

    public bool Ok => LegacyPreserved && NoInflation;

    public static double Pct(int hit, int total) => total <= 0 ? 0 : hit * 100.0 / total;

    public double OldHitPct => Pct(OldHit, OldSettled);
    public double NewHitPct => Pct(NewHit, NewSettled);
}

/// <summary>分玩法的新旧口径对照。</summary>
public sealed class MilkLedgerAuditRow
{
    public string PlayType { get; set; } = string.Empty;
    public int OldTotal { get; set; }
    public int OldHit { get; set; }
    public int NewTotal { get; set; }
    public int NewHit { get; set; }
    public int NewLegacyTotal { get; set; }
    public int NewLegacySettled { get; set; }
}
