using Undertow.Runtime;

namespace Undertow.Server;

/// <summary>Hosts the coordinator liveness sweep.</summary>
public sealed class SocketSweeperService(SocketSweeper sweeper) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => sweeper.RunAsync(stoppingToken);
}

/// <summary>Hosts the idle-document eviction sweep.</summary>
public sealed class DocumentIdleSweeperService(DocumentIdleSweeper sweeper) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => sweeper.RunAsync(stoppingToken);
}
