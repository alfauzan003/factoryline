namespace FactoryLine.Domain;

/// <summary>
/// The seam between the equipment world and the host. The simulator and the
/// OPC-UA adapter are two implementations of the same contract, so the bridge
/// and dashboard never depend on which source is driving them.
/// </summary>
public interface IEquipmentSource
{
    bool IsRunning { get; }

    event EventHandler<EquipmentStateChange>? StateChanged;

    Task<IReadOnlyList<EquipmentStateSnapshot>> GetCurrentStatesAsync(CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
