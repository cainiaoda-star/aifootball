using AiFootBall.Data;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 净胜球诊断的展示层解析（2026-11-05 加）。
///
/// 库里的 DistText 是 "gd:千分比" 逗号分隔的紧凑串，形如 "-4:14,-3:50,0:225"，
/// 前端要的是 [{gd:-4,pct:1.4}, ...]。这段解析没有数据库依赖，必须钉死：
/// 负数净胜球的负号、千分比转百分比、脏数据跳过，任何一条错了图就是错的。
///
/// 注意这里只测解析，不测概率口径——口径在 SQL 里，和 db/20_score_diag.sql 绑死，
/// 466 场回测 MAE 1.32 是它的验收标准，不是单测能覆盖的。
/// </summary>
public class ScoreDiagTests
{
    [Fact]
    public void 解析完整分布_负号与千分比都正确()
    {
        var row = new ScoreDiagRow { DistText = "-4:14,-3:50,0:225,1:211,3:79" };
        var dist = row.Dist;

        Assert.Equal(5, dist.Count);
        Assert.Equal(-4, dist[0].Gd);
        Assert.Equal(1.4m, dist[0].Pct);
        Assert.Equal(0, dist[2].Gd);
        Assert.Equal(22.5m, dist[2].Pct);
        Assert.Equal(3, dist[4].Gd);
        Assert.Equal(7.9m, dist[4].Pct);
    }

    [Fact]
    public void 千分比四舍五入到一位小数()
    {
        var row = new ScoreDiagRow { DistText = "0:225,1:7,2:1000" };
        var dist = row.Dist;

        Assert.Equal(22.5m, dist[0].Pct);
        Assert.Equal(0.7m, dist[1].Pct);
        Assert.Equal(100m, dist[2].Pct);
    }

    [Fact]
    public void 空串与null返回空列表_不抛异常()
    {
        Assert.Empty(new ScoreDiagRow { DistText = null }.Dist);
        Assert.Empty(new ScoreDiagRow { DistText = "" }.Dist);
        Assert.Empty(new ScoreDiagRow { DistText = "   " }.Dist);
    }

    [Fact]
    public void 脏片段跳过_不影响其余档()
    {
        var row = new ScoreDiagRow { DistText = "0:225,garbage,-3:50,:30,4:" };
        var dist = row.Dist;

        Assert.Equal(2, dist.Count);
        Assert.Equal(0, dist[0].Gd);
        Assert.Equal(-3, dist[1].Gd);
    }

    [Fact]
    public void 分布合计接近100_拿朗斯场真实数据验算()
    {
        // 朗斯 1:9 那场的封盘分布（FbScoreDiag 实际值，去掉尾部极小的档）
        var row = new ScoreDiagRow
        {
            DistText = "-5:36,-4:31,-3:82,-2:146,-1:217,0:225,1:140,2:73,3:27,4:9,5:14"
        };
        var sum = row.Dist.Sum(x => x.Pct);

        Assert.Equal(100m, sum);
        // 客队净胜 ≥3 的概率 = 3.6+3.1+8.2 = 14.9%
        Assert.Equal(14.9m, row.Dist.Where(x => x.Gd <= -3).Sum(x => x.Pct));
    }
}
