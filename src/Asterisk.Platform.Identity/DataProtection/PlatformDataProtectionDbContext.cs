using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;

namespace Asterisk.Platform.Identity.DataProtection;

/// <summary>
/// EF Core <see cref="DbContext"/> mapped to the <c>data_protection_keys</c> table
/// created by Storage.Postgres migration <c>018_DataProtectionKeys.sql</c>.
///
/// Used by ASP.NET Core DataProtection (<c>PersistKeysToDbContext</c>) to persist
/// the platform-wide keyring across container recreations and across multiple
/// API replicas. See ADR-0003 (DataProtection key persistence strategy) for the
/// design rationale.
/// </summary>
/// <remarks>
/// EF Core 10 is not trim-/NativeAOT-safe (model-builder reflection). The
/// <see cref="RequiresUnreferencedCodeAttribute"/> + <see cref="RequiresDynamicCodeAttribute"/>
/// annotations propagate the warning to consumers; <see cref="DependencyInjection.PlatformDataProtectionExtensions"/>
/// also carries them. Platform.Api already opts out of AOT analyzers because of SignalR,
/// so accepting reflection here is consistent with the surrounding repo policy.
/// </remarks>
public sealed class PlatformDataProtectionDbContext : DbContext, IDataProtectionKeyContext
{
    [RequiresUnreferencedCode("EF Core uses reflection over entity types; not trim-safe.")]
    [RequiresDynamicCode("EF Core builds query plans dynamically; not NativeAOT-safe.")]
    public PlatformDataProtectionDbContext(DbContextOptions<PlatformDataProtectionDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Required by <see cref="IDataProtectionKeyContext"/>. Mapped to the unqualified
    /// <c>data_protection_keys</c> table — the V018 migration creates the table in the
    /// default schema (matches the existing Storage.Postgres convention; no dedicated
    /// <c>asterisk_platform</c> schema is in use).
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        // Migration 018_DataProtectionKeys.sql is the source of truth for the schema.
        // We only map the non-default column names + table name here. EF Core's
        // automatic migrations are NEVER used against this database — Platform owns
        // schema via DatabaseMigrationService.
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("data_protection_keys");

            entity.HasKey(k => k.Id);
            entity.Property(k => k.Id).HasColumnName("id");
            entity.Property(k => k.FriendlyName).HasColumnName("friendly_name");
            entity.Property(k => k.Xml).HasColumnName("xml");
        });
    }
}
