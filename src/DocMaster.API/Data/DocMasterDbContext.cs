using DocMaster.API.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocMaster.API.Data;

/// <summary>
/// Database context for DocMaster metadata and cluster management
/// </summary>
public class DocMasterDbContext : DbContext
{
    public DocMasterDbContext(DbContextOptions<DocMasterDbContext> options)
        : base(options)
    {
    }

    public DbSet<NodeEntity> Nodes => Set<NodeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure NodeEntity
        modelBuilder.Entity<NodeEntity>(entity =>
        {
            entity.HasKey(e => e.NodeId);

            entity.Property(e => e.NodeId)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.GrpcAddress)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.Datacenter)
                .HasMaxLength(100);

            entity.Property(e => e.Tier)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue("Hot");

            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue("Unknown");

            entity.Property(e => e.ConsecutiveFailures)
                .HasDefaultValue(0);

            entity.Property(e => e.CreatedUtc)
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.Property(e => e.UpdatedUtc)
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Indices for common queries
            entity.HasIndex(e => e.Status)
                .HasDatabaseName("IX_Nodes_Status");

            entity.HasIndex(e => e.UpdatedUtc)
                .HasDatabaseName("IX_Nodes_UpdatedUtc");

            entity.HasIndex(e => new { e.Status, e.Tier })
                .HasDatabaseName("IX_Nodes_Status_Tier");
        });
    }
}
