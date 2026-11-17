using AiFootBall.Sources;
using Microsoft.Extensions.Hosting;

namespace AiFootBall.Api;

/// <summary>
/// 停止时先禁止新的模型刷新，再等待已经登记的那一次写完。
/// 宿主令牌取消后不再假装已经排空，也不取消仍在写的任务。
/// </summary>
public sealed class MaintenanceStopService : IHostedService
{
    private readonly MaintenanceGate _gate;

    public MaintenanceStopService(MaintenanceGate gate) => _gate = gate;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _gate.WaitForShutdownAsync(cancellationToken);
}
