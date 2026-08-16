namespace FactoryLine.Data;

public sealed class NextMovementRequestRow
{
    public int Id { get; set; }

    public Guid MovementId { get; set; }

    public string MaterialCode { get; set; } = string.Empty;

    public string FromLocation { get; set; } = string.Empty;

    public string ToLocation { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
}
