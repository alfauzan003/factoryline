namespace FactoryLine.Domain;

public sealed record EquipmentStateChange(string EquipmentId, EquipmentState State, DateTimeOffset Timestamp);

public sealed record EquipmentStateSnapshot(string EquipmentId, EquipmentState State, DateTimeOffset ChangedAt);
