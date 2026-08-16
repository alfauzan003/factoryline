using FactoryLine.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FactoryLine.Tests;

public class LineSimulatorTests
{
    private static LineSimulator CreateSimulator(int count = 2, int tickMs = 20)
    {
        var options = Options.Create(new LineSimulatorOptions
        {
            EquipmentCount = count,
            TickMilliseconds = tickMs,
        });

        return new LineSimulator(options, NullLogger<LineSimulator>.Instance);
    }

    [Fact]
    public async Task Starting_EmitsStateChanges_OnASchedule()
    {
        await using var simulator = CreateSimulator(count: 2, tickMs: 20);

        var changes = new List<EquipmentStateChange>();
        simulator.StateChanged += (_, change) => changes.Add(change);

        await simulator.StartAsync();
        await Task.Delay(250);

        Assert.True(simulator.IsRunning);
        Assert.NotEmpty(changes);
        Assert.Contains(changes, c => c.State == EquipmentState.Running);
        Assert.Contains(changes, c => c.EquipmentId == "EQ-01");
        Assert.Contains(changes, c => c.EquipmentId == "EQ-02");
    }

    [Fact]
    public async Task Stop_StopsEmitting_AndMarksNotRunning()
    {
        await using var simulator = CreateSimulator(tickMs: 10);

        await simulator.StartAsync();
        await Task.Delay(50);

        var countAfterStart = 0;
        simulator.StateChanged += (_, _) => Interlocked.Increment(ref countAfterStart);

        await simulator.StopAsync();

        var countAfterStop = countAfterStart;
        await Task.Delay(100);

        Assert.False(simulator.IsRunning);
        Assert.Equal(countAfterStop, countAfterStart);
    }

    [Fact]
    public async Task CurrentStates_ReturnsOneSnapshotPerEquipment()
    {
        await using var simulator = CreateSimulator(count: 3);

        await simulator.StartAsync();
        await Task.Delay(50);

        var states = await simulator.GetCurrentStatesAsync();

        Assert.Equal(3, states.Count);
        Assert.Equal(new[] { "EQ-01", "EQ-02", "EQ-03" }, states.Select(s => s.EquipmentId).OrderBy(id => id));
    }
}
