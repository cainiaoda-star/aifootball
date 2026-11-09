using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 否决台账的判定规则。台账的结论直接决定「75% 大热不追」「概率未达线」这些规则
/// 该不该放宽，判错一支整本结论就反了 —— 而它平时只能在比赛打完后才跑一次，
/// 出问题很难被看见。所以把判定抽成纯函数，这里逐条钉死。
/// </summary>
public class VetoJudgeTests
{
    [Fact]
    public void 竞彩_主胜方向对赛果()
    {
        var (hit, hcp) = VetoLog.Judge(Boards.Jc, '3', null, null, '3', 2, 0);
        Assert.True(hit);
        Assert.Null(hcp);   // 竞彩没有让球这回事

        var (miss, _) = VetoLog.Judge(Boards.Jc, '3', null, null, '0', 0, 1);
        Assert.False(miss);
    }

    [Fact]
    public void 竞彩_平局方向也能判()
    {
        var (hit, _) = VetoLog.Judge(Boards.Jc, '1', null, null, '1', 1, 1);
        Assert.True(hit);

        var (miss, _) = VetoLog.Judge(Boards.Jc, '1', null, null, '3', 2, 1);
        Assert.False(miss);
    }

    [Fact]
    public void 北单_用让球结果判而不是胜平负()
    {
        // 主让 1 球，2-0 拿下：让球后仍胜，让球方向判中
        var (hit, hcp) = VetoLog.Judge(Boards.Bd, '3', '3', -1m, '3', 2, 0);
        Assert.True(hit);
        Assert.True(hcp);

        // 主让 1 球，1-0 小胜：胜平负是主胜，但让球后是平 —— 让球方向判错。
        // 这正是「不能拿胜平负糊弄北单」的那一条。
        var (missHit, missHcp) = VetoLog.Judge(Boards.Bd, '3', '3', -1m, '3', 1, 0);
        Assert.False(missHit);
        Assert.False(missHcp);
    }

    [Fact]
    public void 北单_让后平局判中()
    {
        var (hit, hcp) = VetoLog.Judge(Boards.Bd, '1', '1', -1m, '3', 1, 0);
        Assert.True(hit);
        Assert.True(hcp);
    }

    [Fact]
    public void 北单_让球盘但方向判不出时退回胜平负()
    {
        // 有让球方向却没盘口（数据缺失）：让球判不了，退到胜平负方向
        var (hit, hcp) = VetoLog.Judge(Boards.Bd, '3', '3', null, '3', 2, 0);
        Assert.True(hit);
        Assert.Null(hcp);
    }

    [Fact]
    public void 未完场一律不判()
    {
        var (hit, hcp) = VetoLog.Judge(Boards.Jc, '3', null, null, null, null, null);
        Assert.Null(hit);
        Assert.Null(hcp);
    }

    [Fact]
    public void 没有方向时不判命中()
    {
        var (hit, _) = VetoLog.Judge(Boards.Jc, null, null, null, '3', 2, 0);
        Assert.Null(hit);
    }

    [Fact]
    public void 命中率没有样本时是空而不是零()
    {
        // 0% 会被读成「一场都没中」，而实际是「还没得判」。两者混在一起，
        // 台账就会在头一天给出一个看起来像结论的假结论。
        var rows = new List<VetoStatRow>
        {
            new() { ReasonCode = "heat-extreme", Total = 8, Settled = 0, Hits = 0, HitRate = null }
        };
        var report = VetoLog.Report(3, rows);

        Assert.Equal(8, report.Total);
        Assert.Equal(0, report.Settled);
        Assert.Null(report.Rows[0].HitRate);
        Assert.Contains("还没有一场开奖", report.Verdict);
    }

    [Fact]
    public void 样本不足百场时明确劝退而不是给结论()
    {
        var rows = new List<VetoStatRow>
        {
            new() { ReasonCode = "heat-extreme", Total = 30, Settled = 30, Hits = 18, HitRate = 60m, Roi = 1.12m }
        };
        var report = VetoLog.Report(30, rows);

        // 30 场就宣布「该放开」是这次审查要修的病
        Assert.Contains("样本不足 100 场", report.Verdict);
        Assert.DoesNotContain("该放开", report.Verdict);
    }

    [Fact]
    public void 热线样本够了才给该不该放开的结论()
    {
        // 总样本过百才进入结论段；热线自己满 30 场才单独点评
        var rows = new List<VetoStatRow>
        {
            new() { ReasonCode = "heat-extreme", Total = 40, Settled = 40, Hits = 24, HitRate = 60m, Roi = 0.86m },
            new() { ReasonCode = "low-prob", Total = 80, Settled = 80, Hits = 44, HitRate = 55m, Roi = 0.95m }
        };
        var report = VetoLog.Report(90, rows);

        Assert.Contains("挡对了", report.Verdict);
    }

    [Fact]
    public void 复盘表格里无样本显示横杠而不是零()
    {
        var md = VetoLog.ToMarkdown(
            new VetoReport
            {
                Days = 3, Total = 8, Settled = 0, Verdict = "",
                Rows = new List<VetoStatRow>
                {
                    new() { ReasonCode = "heat-extreme", Total = 8, Settled = 0, Hits = 0, HitRate = null }
                }
            },
            new VetoReport { Days = 3, Total = 0 });

        Assert.Contains("| 0 | 0 | - |", md.Replace("  ", " "));
        Assert.DoesNotContain("0.0%", md);
    }
}
