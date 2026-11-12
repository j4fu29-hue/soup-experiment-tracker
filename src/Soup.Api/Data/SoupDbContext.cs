using Microsoft.EntityFrameworkCore;

namespace Soup.Api.Data;

public sealed class SoupDbContext(DbContextOptions<SoupDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<MetricBatch> MetricBatches => Set<MetricBatch>();
    public DbSet<MetricEvent> MetricEvents => Set<MetricEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.KeyHash).HasMaxLength(64);
            entity.HasIndex(x => x.KeyHash).IsUnique();
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        });

        modelBuilder.Entity<Run>(entity =>
        {
            entity.ToTable("runs");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.ProjectId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(30);
            entity.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        });

        modelBuilder.Entity<MetricBatch>(entity =>
        {
            entity.ToTable("metric_batches");
            entity.HasKey(x => new { x.ProjectId, x.BatchId });
            entity.Property(x => x.PayloadHash).HasMaxLength(64);
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
            entity.HasOne<Run>().WithMany()
                .HasForeignKey(x => new { x.ProjectId, x.RunId })
                .HasPrincipalKey(x => new { x.ProjectId, x.Id });
        });

        modelBuilder.Entity<MetricEvent>(entity =>
        {
            entity.ToTable("metric_events");
            entity.HasKey(x => new { x.ProjectId, x.EventId });
            entity.Property(x => x.Metric).HasMaxLength(100);
            entity.Property(x => x.Sequence).UseIdentityAlwaysColumn();
            entity.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.RunId, x.Sequence });
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
            entity.HasOne<Run>().WithMany()
                .HasForeignKey(x => new { x.ProjectId, x.RunId })
                .HasPrincipalKey(x => new { x.ProjectId, x.Id });
        });
    }
}
