namespace FactoryLine.Domain;

public enum WorkOrderState
{
    Created,
    WaitingForMaterial,
    Running,
    Completed
}

public sealed record WorkOrder(string Id, string ProductCode, string RequiredMaterialId, string EquipmentId, WorkOrderState State, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, Guid? ReleasedByMovementId);
