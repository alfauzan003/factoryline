using System.Net.Http.Json;
using FactoryLine.Data;
using FactoryLine.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryLine.Tests;

/// <summary>
/// Mini-MES work-order lifecycle: created → WAIT_MATERIAL → RUN → COMPLETED,
/// with the gate released by the arrival callback and a next-movement request
/// emitted on completion. All assertions are at the public HTTP seam. A fresh
/// app factory (own InMemory DB) is used per test so work orders never leak
/// between tests.
/// </summary>
public class WorkOrderApiTests
{
    private HttpClient CreateClient()
    {
        return new FactoryLineAppFactory().CreateClient();
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<T>();
    }

    [Fact]
    public async Task CreateWorkOrder_StartsWaitingForMaterial_AndEquipmentDoesNotRun()
    {
        using var client = CreateClient();

        var create = await client.PostAsJsonAsync("/api/workorders", new
        {
            productCode = "BAT-100",
            requiredMaterialId = "CELL-X",
            equipmentId = "EQ-01"
        });

        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);
        var workOrder = await ReadJsonAsync<WorkOrderRow>(create);
        Assert.NotNull(workOrder);
        Assert.Equal(WorkOrderState.WaitingForMaterial.ToString(), workOrder.State);
        Assert.Equal("EQ-01", workOrder.EquipmentId);
    }

    [Fact]
    public async Task ArrivalCallback_ReleasesGate_AndWorkOrderRuns()
    {
        using var client = CreateClient();

        var create = await client.PostAsJsonAsync("/api/workorders", new
        {
            productCode = "BAT-100",
            requiredMaterialId = "CELL-X",
            equipmentId = "EQ-01"
        });
        var workOrder = await ReadJsonAsync<WorkOrderRow>(create);

        var callback = await client.PostAsJsonAsync("/api/arrivals", new
        {
            movementId = Guid.NewGuid(),
            destinationPoint = "EQ-01",
            materialId = "CELL-X",
            arrivedAt = DateTimeOffset.UtcNow
        });

        callback.EnsureSuccessStatusCode();
        var result = await ReadJsonAsync<WorkOrderArrivalResult>(callback);
        Assert.NotNull(result);
        Assert.True(result.released);

        var list = await client.GetFromJsonAsync<List<WorkOrderRow>>("/api/workorders");
        var stored = Assert.Single(list!);
        Assert.Equal(WorkOrderState.Running.ToString(), stored.State);
        Assert.NotNull(stored.ReleasedByMovementId);
    }

    [Fact]
    public async Task DuplicateArrivalCallback_IsIdempotent()
    {
        using var client = CreateClient();

        await client.PostAsJsonAsync("/api/workorders", new
        {
            productCode = "BAT-100",
            requiredMaterialId = "CELL-X",
            equipmentId = "EQ-01"
        });

        var movementId = Guid.NewGuid();
        var payload = new
        {
            movementId,
            destinationPoint = "EQ-01",
            materialId = "CELL-X",
            arrivedAt = DateTimeOffset.UtcNow
        };

        var first = await client.PostAsJsonAsync("/api/arrivals", payload);
        var second = await client.PostAsJsonAsync("/api/arrivals", payload);

        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);

        var list = await client.GetFromJsonAsync<List<WorkOrderRow>>("/api/workorders");
        var running = list!.Where(w => w.State == WorkOrderState.Running.ToString()).ToList();
        Assert.Single(running);
        Assert.All(running, w => Assert.Equal(movementId, w.ReleasedByMovementId));
    }

    [Fact]
    public async Task ArrivalCallback_WithMismatchedMaterial_DoesNotRelease()
    {
        using var client = CreateClient();

        var create = await client.PostAsJsonAsync("/api/workorders", new
        {
            productCode = "BAT-100",
            requiredMaterialId = "CELL-X",
            equipmentId = "EQ-01"
        });
        var workOrder = await ReadJsonAsync<WorkOrderRow>(create);

        var callback = await client.PostAsJsonAsync("/api/arrivals", new
        {
            movementId = Guid.NewGuid(),
            destinationPoint = "EQ-01",
            materialId = "CELL-Y",
            arrivedAt = DateTimeOffset.UtcNow
        });

        callback.EnsureSuccessStatusCode();
        var result = await ReadJsonAsync<WorkOrderArrivalResult>(callback);
        Assert.False(result!.released);

        var list = await client.GetFromJsonAsync<List<WorkOrderRow>>("/api/workorders");
        var stored = Assert.Single(list!);
        Assert.Equal(WorkOrderState.WaitingForMaterial.ToString(), stored.State);
    }

    [Fact]
    public async Task CompletedWorkOrder_EmitsNextMovementRequest()
    {
        using var client = CreateClient();

        await client.PostAsJsonAsync("/api/workorders", new
        {
            productCode = "BAT-100",
            requiredMaterialId = "CELL-X",
            equipmentId = "EQ-01"
        });

        var callback = await client.PostAsJsonAsync("/api/arrivals", new
        {
            movementId = Guid.NewGuid(),
            destinationPoint = "EQ-01",
            materialId = "CELL-X",
            arrivedAt = DateTimeOffset.UtcNow
        });
        callback.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var completed = false;
        while (DateTime.UtcNow < deadline)
        {
            var workOrders = await client.GetFromJsonAsync<List<WorkOrderRow>>("/api/workorders");
            if (workOrders is not null && workOrders.All(w => w.State == WorkOrderState.Completed.ToString()))
            {
                var movementRequests = await client.GetFromJsonAsync<List<NextMovementRequestRow>>("/api/movements/pending");
                Assert.NotNull(movementRequests);
                Assert.NotEmpty(movementRequests);
                Assert.Equal("BAT-100", movementRequests[0].MaterialCode);
                completed = true;
                break;
            }

            await Task.Delay(100);
        }

        Assert.True(completed, "The work order did not reach COMPLETED within the timeout.");
    }
}

public sealed record WorkOrderArrivalResult(bool released, string? message, string? workOrderId);
