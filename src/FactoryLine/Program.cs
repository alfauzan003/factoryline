using FactoryLine.Components;
using FactoryLine.Data;
using FactoryLine.Domain;
using FactoryLine.Hubs;
using FactoryLine.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

builder.Services.Configure<LineSimulatorOptions>(
    builder.Configuration.GetSection(LineSimulatorOptions.SectionName));
builder.Services.AddSingleton<IEquipmentSource, LineSimulator>();

var connectionString = builder.Configuration.GetConnectionString("FactoryLineDb")
    ?? throw new InvalidOperationException("Connection string 'FactoryLineDb' is missing.");

builder.Services.AddDbContext<FactoryLineDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddHostedService<EquipmentBridgeWorker>();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<EquipmentHub>("/equipmenthub");
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
