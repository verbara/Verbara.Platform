using System.Text.Json.Serialization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

/// <summary>
/// supervisor-silent-listen (design D3, D8, D12): a user's silent-listen profile — the SIP credentials of their
/// receive-only listen endpoint (option B) and the phone number an administrator stored for them (option C).
/// One row per (tenant, user). This is NOT an Agent profile: it creates no <c>agents</c> row and no licensed agent.
/// </summary>
public sealed class SupervisorListenProfile
{
    public required TenantId TenantId { get; init; }
    public required EntityId UserId { get; init; }

    /// <summary>The listen endpoint's extension (<c>sl</c> + 8 hex), unique per tenant; null until first provisioned.</summary>
    public string? SipExtension { get; set; }

    /// <summary>
    /// The listen endpoint's SIP password. Never serialized: it crosses HTTP only to its owner, through the
    /// credentials route, the same rule as <c>Agent.SipPassword</c>.
    /// </summary>
    [JsonIgnore]
    public string? SipPassword { get; set; }

    /// <summary>The strict E.164 number a phone listen rings, set only by a tenant administrator; null when none.</summary>
    public string? PhoneNumber { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether the listen endpoint has been provisioned (extension and password both set).</summary>
    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrEmpty(SipExtension) && !string.IsNullOrEmpty(SipPassword);
}

/// <summary>Stores <see cref="SupervisorListenProfile"/>s, keyed by (tenant, user).</summary>
public interface ISupervisorListenProfileStore
{
    Task<SupervisorListenProfile?> GetAsync(TenantId tenantId, EntityId userId, CancellationToken ct);

    /// <summary>Every profile of <paramref name="tenantId"/> whose listen endpoint has credentials.</summary>
    Task<IReadOnlyList<SupervisorListenProfile>> ListWithCredentialsAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>
    /// Inserts or replaces the profile of (tenant, user). Returns <see langword="false"/>, and stores nothing, when
    /// another user of the tenant already holds <see cref="SupervisorListenProfile.SipExtension"/>.
    /// </summary>
    Task<bool> SaveAsync(SupervisorListenProfile profile, CancellationToken ct);

    /// <summary>Deletes the profile of (tenant, user); a no-op when there is none.</summary>
    Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
