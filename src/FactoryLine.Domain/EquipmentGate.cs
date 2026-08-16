using System.Collections.Concurrent;

namespace FactoryLine.Domain;

/// <summary>
/// Lets the mini-MES hold equipment while a work order waits for material.
/// A held equipment stops advancing its state machine (WAIT_MATERIAL) until
/// the arrival callback releases the gate.
/// </summary>
public interface IEquipmentGate
{
    bool IsHeld(string equipmentId);

    void Hold(string equipmentId);

    void Release(string equipmentId);
}

public sealed class InMemoryEquipmentGate : IEquipmentGate
{
    private readonly ConcurrentDictionary<string, byte> _held = new();

    public bool IsHeld(string equipmentId) => _held.ContainsKey(equipmentId);

    public void Hold(string equipmentId) => _held.TryAdd(equipmentId, 0);

    public void Release(string equipmentId) => _held.TryRemove(equipmentId, out _);
}
