using System.Net.Http;
using FactoryLine.Data;
using FactoryLine.Domain;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryLine.Tests;

public class EquipmentBridgeTests : IClassFixture<FactoryLineAppFactory>
{
    private readonly FactoryLineAppFactory _factory;

    public EquipmentBridgeTests(FactoryLineAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Bridge_PersistsNormalizedStates_ToTheDatabase()
    {
        using var client = _factory.CreateClient();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        var sawRunning = false;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FactoryLineDbContext>();
            var rows = await db.EquipmentStates.ToListAsync();

            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.True(Enum.TryParse<EquipmentState>(row.State, out _), $"invalid persisted state '{row.State}'"));

            if (rows.Any(r => r.State == EquipmentState.Running.ToString()))
            {
                sawRunning = true;
                break;
            }

            await Task.Delay(100);
        }

        Assert.True(sawRunning, "The bridge did not persist a progressed (Running) state within the timeout.");
    }

    [Fact]
    public async Task Bridge_BroadcastsStateChanges_ToTheHub()
    {
        using var server = _factory.Server;
        var tcs = new TaskCompletionSource<EquipmentStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = new HubConnectionBuilder()
            .WithUrl($"{server.BaseAddress}equipmenthub", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();

        connection.On<EquipmentStateChange>("EquipmentStateChanged", change =>
        {
            tcs.TrySetResult(change);
        });

        await connection.StartAsync();

        var change = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(change);
        Assert.StartsWith("EQ-", change.EquipmentId);
        Assert.True(Enum.IsDefined(change.State), $"invalid state '{change.State}' broadcast over the hub");

        await connection.DisposeAsync();
    }
}
