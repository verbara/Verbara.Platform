using Microsoft.AspNetCore.Authorization;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Who a caller is to the conversation read rule (contact-history-opens-by-assignment, design D1, D2 steps 2-3, D16;
/// Platform/ADR-0047): whether it reads every conversation of the tenant, whether it handles conversations, and its
/// Agent profile. The REST rule (<see cref="ConversationReadRule"/>) and the live stream (design D17, resolved once
/// per stream) share this one definition of who reads everything.
/// </summary>
/// <remarks>
/// <para>
/// Each permission is evaluated through <see cref="IAuthorizationService"/> on its <c>Permission:</c> policy, so the
/// Admin/SystemAdmin shortcut and the impersonation rule (a token passes only on a permission minted into it) apply
/// exactly as on any permission gate. Membership is exact (ADR-0037): <c>monitor</c>, <c>read</c> and <c>handle</c>
/// are checked one by one, and none stands in for another. Each is evaluated at most once.
/// </para>
/// </remarks>
internal sealed class ConversationReaderClass
{
    /// <summary>Reads (and listens to) every conversation of the tenant.</summary>
    public const string MonitorPermission = "contacts:conversation:monitor";

    /// <summary>Reads every conversation of the tenant, with no listening and no action (design D16, Q1).</summary>
    public const string ReadPermission = "contacts:conversation:read";

    /// <summary>Handles conversations: reads what its Agent profile owns, is offered or holds the contact of.</summary>
    public const string HandlePermission = "contacts:conversation:handle";

    private const string PermissionPolicyPrefix = "Permission:";

    private ConversationReaderClass(ConversationActor actor, bool readsAll, bool handles)
    {
        Actor = actor;
        ReadsAll = readsAll;
        Handles = handles;
    }

    /// <summary>The caller and its Agent profile, the way the conversation actions resolve them.</summary>
    public ConversationActor Actor { get; }

    /// <summary>The caller holds <see cref="MonitorPermission"/> or <see cref="ReadPermission"/> (design D2 step 2).</summary>
    public bool ReadsAll { get; }

    /// <summary>The caller holds <see cref="HandlePermission"/> (design D2 step 3).</summary>
    public bool Handles { get; }

    /// <summary>The caller's Agent profile in the tenant, or <see langword="null"/> when it has none.</summary>
    public Agent? Agent => Actor.Agent;

    /// <summary>
    /// The Agent profile id the caller reads as when it is not <see cref="ReadsAll"/>: set only when it
    /// <see cref="Handles"/> and has a profile, else <see langword="null"/>.
    /// </summary>
    public EntityId? HandlingAgentId => Handles ? Actor.Agent?.AgentId : null;

    /// <summary>Classifies the caller of <paramref name="context"/> in <paramref name="tenantId"/>.</summary>
    public static async Task<ConversationReaderClass> ResolveAsync(
        HttpContext context, TenantId tenantId, IAgentStore agents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(agents);

        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        var readsAll = await SatisfiesAsync(authorization, context, MonitorPermission)
            || await SatisfiesAsync(authorization, context, ReadPermission);
        var handles = await SatisfiesAsync(authorization, context, HandlePermission);
        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        return new ConversationReaderClass(actor, readsAll, handles);
    }

    private static async Task<bool> SatisfiesAsync(IAuthorizationService authorization, HttpContext context, string permission) =>
        (await authorization.AuthorizeAsync(context.User, PermissionPolicyPrefix + permission)).Succeeded;
}
