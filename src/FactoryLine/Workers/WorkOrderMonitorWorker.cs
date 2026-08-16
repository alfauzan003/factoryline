using FactoryLine.Domain;
using FactoryLine.Services;

namespace FactoryLine.Workers;

/// <summary>
/// Observes equipment state changes and drives the mini-MES: when an equipment
/// that is running a work order reaches Completed, the work order completes and
/// a next-movement request is emitted. The bridge worker owns the source
/// lifecycle; this worker only subscribes to its events.
/// </summary>
public sealed class WorkOrderMonitorWorker : BackgroundService
{
    private readonly IEquipmentSource _source;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkOrderMonitorWorker> _logger;

    public WorkOrderMonitorWorker(
        IEquipmentSource source,
        IServiceScopeFactory scopeFactory,
        ILogger<WorkOrderMonitorWorker> logger)
    {
        _source = source;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _source.StateChanged += OnStateChanged;

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _source.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(object? sender, EquipmentStateChange change)
    {
        _ = HandleAsync(change);
    }

    private async Task HandleAsync(EquipmentStateChange change)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<MiniMesService>();
            await service.OnEquipmentCompletedAsync(change).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process work order completion for {EquipmentId}.", change.EquipmentId);
        }
    }
}
