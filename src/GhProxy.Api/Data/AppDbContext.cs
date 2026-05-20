using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<VpsNode> VpsNodes => Set<VpsNode>();
    public DbSet<ProxySession> ProxySessions => Set<ProxySession>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VpsNode>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Host).HasMaxLength(255).IsRequired();
            entity.Property(x => x.SshUsername).HasMaxLength(120).IsRequired();
            entity.Property(x => x.SshKeyPath).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Region).HasMaxLength(120);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.Property(x => x.ProxyUsername).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ProtectedProxyPassword).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<ProxySession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasOne(x => x.Node)
                .WithMany(x => x.Sessions)
                .HasForeignKey(x => x.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Message).HasMaxLength(2000).IsRequired();
            entity.HasIndex(x => x.Timestamp);
        });

        modelBuilder.Entity<OperationalEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Severity).HasMaxLength(32).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Message).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(120);
            entity.Property(x => x.CommandKind).HasMaxLength(120);
            entity.Property(x => x.CommandDisplay).HasMaxLength(4000);
            entity.Property(x => x.StandardOutputSnippet).HasMaxLength(8000);
            entity.Property(x => x.StandardErrorSnippet).HasMaxLength(8000);
            entity.Property(x => x.DetailsJson).HasMaxLength(8000);
            entity.HasIndex(x => x.Timestamp);
            entity.HasIndex(x => x.CorrelationId);
            entity.HasIndex(x => x.NodeId);
        });
    }
}
