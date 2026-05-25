using GhProxy.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GhProxy.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<VpsNode> VpsNodes => Set<VpsNode>();
    public DbSet<ProxySession> ProxySessions => Set<ProxySession>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();
    public DbSet<GitHubAccount> GitHubAccounts => Set<GitHubAccount>();
    public DbSet<CodespaceSnapshot> CodespaceSnapshots => Set<CodespaceSnapshot>();
    public DbSet<CodespaceStateSample> CodespaceStateSamples => Set<CodespaceStateSample>();
    public DbSet<LocalProxyProfile> LocalProxyProfiles => Set<LocalProxyProfile>();
    public DbSet<LocalProxySession> LocalProxySessions => Set<LocalProxySession>();
    public DbSet<LocalProxyGatewayRequest> LocalProxyGatewayRequests => Set<LocalProxyGatewayRequest>();

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

        modelBuilder.Entity<GitHubAccount>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Username).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ProtectedPersonalAccessToken).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.Plan).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ValidationStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.QuotaState).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ValidationMessage).HasMaxLength(2000);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<CodespaceSnapshot>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.State).HasMaxLength(80).IsRequired();
            entity.Property(x => x.RepositoryFullName).HasMaxLength(260);
            entity.Property(x => x.MachineDisplayName).HasMaxLength(160);
            entity.Property(x => x.Location).HasMaxLength(120);
            entity.Property(x => x.WebUrl).HasMaxLength(1000);
            entity.Property(x => x.BillableOwner).HasMaxLength(120);
            entity.HasIndex(x => new { x.AccountId, x.Name }).IsUnique();
            entity.HasOne(x => x.Account)
                .WithMany(x => x.Codespaces)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CodespaceStateSample>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CodespaceName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.State).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ObservedAt)
                .HasConversion(
                    value => value.UtcTicks,
                    value => new DateTimeOffset(value, TimeSpan.Zero));
            entity.HasIndex(x => x.ObservedAt);
            entity.HasIndex(x => new { x.AccountId, x.CodespaceName, x.ObservedAt });
            entity.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LocalProxyProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.BindHost).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ProxyUsername).HasMaxLength(120);
            entity.Property(x => x.ProtectedProxyPassword).HasMaxLength(2000);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<LocalProxySession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.BindHost).HasMaxLength(120).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.Property(x => x.CodespaceName).HasMaxLength(200);
            entity.HasOne(x => x.Profile)
                .WithMany(x => x.Sessions)
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.ProfileId);
            entity.HasIndex(x => x.StartedAt);
        });

        modelBuilder.Entity<LocalProxyGatewayRequest>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Protocol).HasMaxLength(16).IsRequired();
            entity.Property(x => x.TargetHost).HasMaxLength(255);
            entity.Property(x => x.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CodespaceName).HasMaxLength(200);
            entity.Property(x => x.ErrorMessage).HasMaxLength(1000);
            entity.HasIndex(x => x.ObservedAt);
            entity.HasIndex(x => x.SessionId);
        });
    }
}
