namespace FactoryLine.Domain;

public sealed record ArrivalCallback(Guid MovementId, string DestinationPoint, string MaterialId, DateTimeOffset ArrivedAt);
