namespace FactoryLine.Data;

public sealed class WorkOrderRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ProductCode { get; set; } = string.Empty;

    public string RequiredMaterialId { get; set; } = string.Empty;

    public string EquipmentId { get; set; } = string.Empty;

    public string State { get; set; } = "Created";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? ReleasedByMovementId { get; set; }
}
