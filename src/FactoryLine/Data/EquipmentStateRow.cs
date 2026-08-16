namespace FactoryLine.Data;

public sealed class EquipmentStateRow
{
    public int Id { get; set; }

    public string EquipmentId { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; }
}
