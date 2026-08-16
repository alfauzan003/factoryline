namespace FactoryLine.Domain;

public sealed class LineSimulatorOptions
{
    public const string SectionName = "LineSimulator";

    public int EquipmentCount { get; set; } = 3;

    public int TickMilliseconds { get; set; } = 5000;
}
