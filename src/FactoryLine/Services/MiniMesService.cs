using FactoryLine.Data;
using FactoryLine.Domain;
using Microsoft.EntityFrameworkCore;

namespace FactoryLine.Services;

public record CreateWorkOrderRequest(string ProductCode, string RequiredMaterialId, string EquipmentId);

public sealed class MiniMesService
{
    private readonly FactoryLineDbContext _db;
    private readonly IEquipmentGate _gate;
    private readonly ILogger<MiniMesService> _logger;

    public MiniMesService(FactoryLineDbContext db, IEquipmentGate gate, ILogger<MiniMesService> logger)
    {
        _db = db;
        _gate = gate;
        _logger = logger;
    }

    public async Task<WorkOrderRow> CreateWorkOrderAsync(CreateWorkOrderRequest request, CancellationToken ct = default)
    {
        var workOrder = new WorkOrderRow
        {
            ProductCode = request.ProductCode,
            RequiredMaterialId = request.RequiredMaterialId,
            EquipmentId = request.EquipmentId,
            State = WorkOrderState.WaitingForMaterial.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.WorkOrders.Add(workOrder);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _gate.Hold(workOrder.EquipmentId);
        _logger.LogInformation("Work order {WorkOrderId} created for {EquipmentId}, waiting for material {MaterialId}",
            workOrder.Id, workOrder.EquipmentId, workOrder.RequiredMaterialId);

        return workOrder;
    }

    public async Task<WorkOrderRow?> OnArrivalAsync(ArrivalCallback callback, CancellationToken ct = default)
    {
        var workOrder = await _db.WorkOrders
            .Where(w => w.State == WorkOrderState.WaitingForMaterial.ToString())
            .Where(w => w.EquipmentId == callback.DestinationPoint)
            .Where(w => w.RequiredMaterialId == callback.MaterialId)
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (workOrder is null)
        {
            _logger.LogInformation("Arrival callback for {MaterialId} at {DestinationPoint} matched no waiting work order; ignored.",
                callback.MaterialId, callback.DestinationPoint);
            return null;
        }

        if (workOrder.ReleasedByMovementId == callback.MovementId)
        {
            _logger.LogInformation("Arrival callback {MovementId} already released work order {WorkOrderId}; duplicate ignored.",
                callback.MovementId, workOrder.Id);
            return workOrder;
        }

        workOrder.State = WorkOrderState.Running.ToString();
        workOrder.ReleasedByMovementId = callback.MovementId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _gate.Release(workOrder.EquipmentId);
        _logger.LogInformation("Arrival {MovementId} released the gate; work order {WorkOrderId} is now running.",
            callback.MovementId, workOrder.Id);

        return workOrder;
    }

    public async Task OnEquipmentCompletedAsync(EquipmentStateChange change, CancellationToken ct = default)
    {
        if (change.State != EquipmentState.Completed)
        {
            return;
        }

        var workOrder = await _db.WorkOrders
            .Where(w => w.State == WorkOrderState.Running.ToString())
            .Where(w => w.EquipmentId == change.EquipmentId)
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (workOrder is null)
        {
            return;
        }

        workOrder.State = WorkOrderState.Completed.ToString();
        workOrder.CompletedAt = DateTimeOffset.UtcNow;
        _db.NextMovementRequests.Add(new NextMovementRequestRow
        {
            MovementId = Guid.NewGuid(),
            MaterialCode = workOrder.ProductCode,
            FromLocation = workOrder.EquipmentId,
            ToLocation = "DISPATCH",
            Quantity = 1,
            RequestedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _gate.Release(workOrder.EquipmentId);
        _logger.LogInformation("Work order {WorkOrderId} completed; next movement request emitted for {ProductCode}.",
            workOrder.Id, workOrder.ProductCode);
    }

    public Task<List<WorkOrderRow>> GetWorkOrdersAsync(CancellationToken ct = default)
    {
        return _db.WorkOrders.AsNoTracking().OrderBy(w => w.CreatedAt).ToListAsync(ct);
    }

    public Task<List<NextMovementRequestRow>> GetPendingMovementRequestsAsync(CancellationToken ct = default)
    {
        return _db.NextMovementRequests.AsNoTracking().OrderBy(n => n.RequestedAt).ToListAsync(ct);
    }
}
