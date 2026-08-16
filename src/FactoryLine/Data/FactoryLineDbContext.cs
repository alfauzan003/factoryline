using Microsoft.EntityFrameworkCore;

namespace FactoryLine.Data;

public sealed class FactoryLineDbContext : DbContext
{
    public FactoryLineDbContext(DbContextOptions<FactoryLineDbContext> options)
        : base(options)
    {
    }

    public DbSet<EquipmentStateRow> EquipmentStates => Set<EquipmentStateRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EquipmentStateRow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EquipmentId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.State).IsRequired().HasMaxLength(20);
            entity.HasIndex(e => new { e.EquipmentId, e.ChangedAt });
        });
    }
}
