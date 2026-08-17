using FactoryLine.Components;
using FactoryLine.Data;
using FactoryLine.Domain;
using FactoryLine.Hubs;
using FactoryLine.Services;
using FactoryLine.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

builder.Services.AddSingleton<IEquipmentGate, InMemoryEquipmentGate>();
builder.Services.AddScoped<MiniMesService>();

builder.Services.Configure<LineSimulatorOptions>(
    builder.Configuration.GetSection(LineSimulatorOptions.SectionName));

var equipmentSource = builder.Configuration["Equipment:Source"] ?? "Simulator";
if (equipmentSource.Equals("OpcUa", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<SimulatorUaServer>();
    builder.Services.AddHostedService<SimulatorUaServerHostedService>();
    builder.Services.AddSingleton<IEquipmentSource>(sp =>
    {
        var server = sp.GetRequiredService<SimulatorUaServer>();
        var logger = sp.GetRequiredService<ILogger<OpcUaEquipmentSource>>();
        return new OpcUaEquipmentSource(server.EndpointUrl, server.EquipmentIds, logger);
    });
}
else
{
    builder.Services.AddSingleton<IEquipmentSource, LineSimulator>();
}

var connectionString = builder.Configuration.GetConnectionString("FactoryLineDb")
    ?? throw new InvalidOperationException("Connection string 'FactoryLineDb' is missing.");

builder.Services.AddDbContext<FactoryLineDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddHostedService<EquipmentBridgeWorker>();
builder.Services.AddHostedService<WorkOrderMonitorWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FactoryLineDbContext>();
    var dbLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        dbLogger.LogWarning(ex, "Could not reach SQL Server at startup; dashboard will run without persistence. Start the dev DB with 'docker compose up -d'.");
    }
}

app.UseStaticFiles();

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<EquipmentHub>("/equipmenthub");
app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapPost("/workorders", async (CreateWorkOrderRequest request, MiniMesService mes, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.ProductCode) ||
        string.IsNullOrWhiteSpace(request.RequiredMaterialId) ||
        string.IsNullOrWhiteSpace(request.EquipmentId))
    {
        return Results.BadRequest(new { error = "ProductCode, RequiredMaterialId and EquipmentId are required." });
    }

    var workOrder = await mes.CreateWorkOrderAsync(request, ct);
    return Results.Created($"/api/workorders/{workOrder.Id}", workOrder);
});
api.MapGet("/workorders", (MiniMesService mes, CancellationToken ct) => mes.GetWorkOrdersAsync(ct));
api.MapPost("/arrivals", async (ArrivalCallback callback, MiniMesService mes, CancellationToken ct) =>
{
    var workOrder = await mes.OnArrivalAsync(callback, ct);
    return workOrder is null
        ? Results.Ok(new { released = false, message = "No waiting work order matched this arrival." })
        : Results.Ok(new { released = workOrder.State == WorkOrderState.Running.ToString(), workOrderId = workOrder.Id });
});
api.MapGet("/movements/pending", (MiniMesService mes, CancellationToken ct) => mes.GetPendingMovementRequestsAsync(ct));

app.Run();

public partial class Program { }
