using FactoryLine.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryLine.Tests;

public class FactoryLineAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"FactoryLineTestDb_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("LineSimulator:EquipmentCount", "3");
        builder.UseSetting("LineSimulator:TickMilliseconds", "50");
        builder.UseSetting("ConnectionStrings:FactoryLineDb", "Server=localhost,1433;Database=FactoryLineTests;User Id=sa;Password=x;TrustServerCertificate=True;Encrypt=False");

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<FactoryLineDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<FactoryLineDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));
        });
    }
}
