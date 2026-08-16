using System.Threading.Channels;
using FactoryLine.Data;
using FactoryLine.Domain;
using FactoryLine.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FactoryLine.Workers;

/// <summary>
/// The equipment bridge: subscribes to an <see cref="IEquipmentSource"/>,
/// normalizes each state change, persists it to SQL Server, and broadcasts it
/// to dashboard clients over SignalR. The source is started/stopped with the
/// host so the demo line runs as soon as the app is up.
/// </summary>
public sealed class EquipmentBridgeWorker : BackgroundService
{
    private readonly IEquipmentSource _source;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<EquipmentHub> _hub;
    private readonly ILogger<EquipmentBridgeWorker> _logger;
    private readonly Channel<EquipmentStateChange> _changes = Channel.CreateUnbounded<EquipmentStateChange>();

    public EquipmentBridgeWorker(
        IEquipmentSource source,
        IServiceScopeFactory scopeFactory,
        IHubContext<EquipmentHub> hub,
        ILogger<EquipmentBridgeWorker> logger)
    {
        _source = source;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _source.StateChanged += OnStateChanged;

        await _source.StartAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await foreach (var change in _changes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await NormalizeAndPersistAsync(change, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist state change for {EquipmentId}; dashboard update still broadcast.", change.EquipmentId);
                }

                await _hub.Clients.All.SendAsync("EquipmentStateChanged", change, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _source.StateChanged -= OnStateChanged;
            await _source.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnStateChanged(object? sender, EquipmentStateChange change)
    {
        _changes.Writer.TryWrite(change);
    }

    private async Task NormalizeAndPersistAsync(EquipmentStateChange change, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryLineDbContext>();

        var row = new EquipmentStateRow
        {
            EquipmentId = change.EquipmentId,
            State = change.State.ToString(),
            ChangedAt = change.Timestamp,
        };

        db.EquipmentStates.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Persisted {EquipmentId} -> {State}", change.EquipmentId, change.State);
    }
}
