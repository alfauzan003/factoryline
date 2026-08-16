using System.Net;
using System.Net.Sockets;
using FactoryLine.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FactoryLine.Tests;

/// <summary>
/// Exercises the OPC-UA <see cref="IEquipmentSource"/> adapter against the SAME
/// behavior contract as the simulator (same seam, same assertions): state
/// changes are raised on a schedule, stop halts emission, and current states
/// are readable per equipment.
/// </summary>
public class OpcUaEquipmentSourceTests : IAsyncLifetime
{
    private SimulatorUaServer? _server;
    private OpcUaEquipmentSource? _source;

    public async Task InitializeAsync()
    {
        var options = Options.Create(new LineSimulatorOptions { EquipmentCount = 2, TickMilliseconds = 50 });
        _server = new SimulatorUaServer(options, NullLogger<LineSimulator>.Instance, NullLogger<SimulatorUaServer>.Instance);
        await _server.StartAsync(GetFreePort());

        var equipmentIds = _server.EquipmentIds;
        _source = new OpcUaEquipmentSource(_server.EndpointUrl, equipmentIds, NullLogger<OpcUaEquipmentSource>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Starting_EmitsStateChanges_OnASchedule()
    {
        Assert.NotNull(_source);

        var changes = new List<EquipmentStateChange>();
        _source.StateChanged += (_, change) => changes.Add(change);

        await _source.StartAsync();
        await Task.Delay(500);

        Assert.True(_source.IsRunning);
        Assert.NotEmpty(changes);
        Assert.Contains(changes, c => c.State == EquipmentState.Running);
        Assert.Contains(changes, c => c.EquipmentId == "EQ-01");
        Assert.Contains(changes, c => c.EquipmentId == "EQ-02");
    }

    [Fact]
    public async Task Stop_StopsEmitting_AndMarksNotRunning()
    {
        Assert.NotNull(_source);

        await _source.StartAsync();
        await Task.Delay(200);

        var countAfterStart = 0;
        _source.StateChanged += (_, _) => Interlocked.Increment(ref countAfterStart);

        await _source.StopAsync();

        var countAfterStop = countAfterStart;
        await Task.Delay(300);

        Assert.False(_source.IsRunning);
        Assert.Equal(countAfterStop, countAfterStart);
    }

    [Fact]
    public async Task CurrentStates_ReturnsOneSnapshotPerEquipment()
    {
        Assert.NotNull(_source);

        await _source.StartAsync();
        await Task.Delay(200);

        var states = await _source.GetCurrentStatesAsync();

        Assert.Equal(2, states.Count);
        Assert.Equal(new[] { "EQ-01", "EQ-02" }, states.Select(s => s.EquipmentId).OrderBy(id => id));
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
