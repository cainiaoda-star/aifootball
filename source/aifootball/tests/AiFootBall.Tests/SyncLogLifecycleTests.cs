using AiFootBall.Data;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 作业运行记录的生命周期（2026-11-10 补）。
///
/// 背景：在这之前，一次作业运行只在**结束时**往 SyncLog 插一行。于是所有「跑到一半
/// 就没了」的情形在库里都是空白：
///
///   - 12:21:51 启动的 odds-backfill 在 12:40 随进程重启被掐断 —— SyncLog 零行；
///   - 抛异常的那条路径更彻底，写日志那句在 throw 之后，永远执行不到 —— 库里同样零行。
///
/// 两者的共同后果不是「少一条记录」，而是**看板全绿**：/api/jobs 显示上一次 SUCCESS、
/// 下次明天，而今天这一趟根本没跑完、也不会自动补跑。这正是本项目反复栽的那一类
/// ——监控看到了却不动。
///
/// 改成「开始插 RUNNING、退出 UPDATE、启动时把遗留 RUNNING 扫成 ABORTED」之后，
/// 库里第一次能回答「这次跑完了没有」。这批测试钉住支撑它的几条硬约束。
/// </summary>
public class SyncLogLifecycleTests
{
    /// <summary>
    /// Status 是 varchar(10)。超一个字符不是被截断，而是整条 INSERT 被 2628 拒绝 ——
    /// 也就是说「本次没跑完」这条最关键的记录会静默丢失。所有会被写进去的状态值都得在界内。
    /// </summary>
    [Theory]
    [InlineData(SyncLogRepository.StatusRunning)]
    [InlineData(SyncLogRepository.StatusAborted)]
    [InlineData("SUCCESS")]
    [InlineData("FAILED")]
    [InlineData("TIMEOUT")]
    [InlineData("STUCK")]
    public void Status_fits_the_varchar10_column(string status)
    {
        Assert.True(status.Length <= SyncLogRepository.StatusColumnMax,
            $"状态 {status} 长 {status.Length}，超出 dbo.SyncLog.Status 的 {SyncLogRepository.StatusColumnMax} 字符列宽");
    }

    /// <summary>
    /// 扫尾文案里带开始时刻，长度随异常消息一起涨。Message 是 nvarchar(1000)，
    /// 超长会让 UPDATE 报 2628 —— 恰好在「最需要留下证据」的那一刻失败。
    /// 裁剪必须兜住，不能靠调用方自觉。
    /// </summary>
    [Fact]
    public void Long_abandon_message_is_clipped_not_rejected()
    {
        var msg = "进程在本次执行期间退出（开始于 2026-11-10 12:21:51）" + new string('x', 4000);
        var clipped = SyncLogRepository.ClipMessage(msg);
        Assert.NotNull(clipped);
        Assert.True(clipped!.Length <= 1000, $"裁剪后仍有 {clipped.Length} 字符");
        Assert.EndsWith("…", clipped);
    }

    /// <summary>
    /// 作业名会拼成 "job:xxx" 再落库。TaskName 是 nvarchar(100)，
    /// 拼前缀之后超长同样整条被拒，所以裁剪要作用在拼接结果上。
    /// </summary>
    [Fact]
    public void Task_name_with_job_prefix_stays_within_column()
    {
        var name = "job:" + new string('a', 300);
        var clipped = SyncLogRepository.ClipTask(name);
        Assert.NotNull(clipped);
        Assert.True(clipped!.Length <= 100);
    }

    /// <summary>
    /// 三个状态必须互不相同。若 RUNNING 与 ABORTED 撞成同一个字符串，
    /// 「还开着」和「被打断了」就再也分不开，扫尾逻辑会把自己的活当成别人的。
    /// </summary>
    [Fact]
    public void Running_and_aborted_are_distinct()
    {
        Assert.NotEqual(SyncLogRepository.StatusRunning, SyncLogRepository.StatusAborted);
        Assert.Equal("RUNNING", SyncLogRepository.StatusRunning);
        Assert.Equal("ABORTED", SyncLogRepository.StatusAborted);
    }
}
