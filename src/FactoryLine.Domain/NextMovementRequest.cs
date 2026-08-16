namespace FactoryLine.Domain;

/// <summary>
/// A next-material-movement request emitted when a work order completes, ready
/// for a logistics system (LogiFlow) to consume.
/// </summary>
public sealed record NextMovementRequest(Guid MovementId, string MaterialCode, string FromLocation, string ToLocation, int Quantity, DateTimeOffset RequestedAt);
