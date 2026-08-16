using Microsoft.EntityFrameworkCore;

namespace FactoryLine.Data;

public sealed class FactoryLineDbContext : DbContext
{
    public FactoryLineDbContext(DbContextOptions<FactoryLineDbContext> options)
        : base(options)
    {
    }

    public DbSet<EquipmentStateRow> EquipmentStates => Set<EquipmentStateRow>();
    public DbSet<WorkOrderRow> WorkOrders => Set<WorkOrderRow>();
    public DbSet<NextMovementRequestRow> NextMovementRequests => Set<NextMovementRequestRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EquipmentStateRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EquipmentId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.State).IsRequired().HasMaxLength(20);
            entity.HasIndex(e => new { e.EquipmentId, e.ChangedAt });
        });

        modelBuilder.Entity<WorkOrderRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProductCode).IsRequired().HasMaxLength(100);
            entity.Property(e => e.RequiredMaterialId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EquipmentId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.State).IsRequired().HasMaxLength(30);
            entity.HasIndex(e => new { e.EquipmentId, e.State });
        });

        modelBuilder.Entity<NextMovementRequestRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MaterialCode).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FromLocation).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ToLocation).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.MovementId).IsUnique();
        });
    }
}
