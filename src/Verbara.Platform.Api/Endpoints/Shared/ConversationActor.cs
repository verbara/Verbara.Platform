using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Who is acting on a conversation: the calling user and the Agent profile that user holds in the
/// tenant, if any. Ownership is decided on the Agent profile — a conversation's owner is an agent id,
/// never a user id, so a user without a profile owns nothing and an offer is made to an agent.
/// </summary>
/// <remarks>
/// The caller's user id is resolved the canonical way (<see cref="CallerIdentity.ResolveUserId"/>):
/// for an API key bound to a user that is the user, and for a key bound to no user it is the key's own
/// id, which no Agent profile carries.
/// </remarks>
internal sealed class ConversationActor
{
    /// <summary>The caller holds no Agent profile in the tenant.</summary>
    public const string NotAnAgent = "not-an-agent";

    /// <summary>The caller's Agent profile does not own the conversation.</summary>
    public const string NotOwner = "not-owner";

    /// <summary>The conversation is not an open offer made to the caller's Agent profile.</summary>
    public const string NotOfferedToYou = "not-offered-to-you";

    /// <summary>The target agent of a transfer is not an agent of the tenant.</summary>
    public const string TargetAgentNotFound = "target-agent-not-found";

    /// <summary>The target queue of a transfer is not a queue of the tenant.</summary>
    public const string TargetQueueNotFound = "target-queue-not-found";

    /// <summary>
    /// The policy whose holders may act on a conversation they do not own (transfer, close, typify).
    /// The same named policy the <c>/supervisor</c> routes require, evaluated rather than restated so
    /// the two cannot drift.
    /// </summary>
    public const string SupervisorPolicy = "SupervisorPlus";

    private ConversationActor(string? userId, Agent? agent)
    {
        UserId = userId;
        Agent = agent;
    }

    /// <summary>The calling user's id, or <see langword="null"/> when the principal carries none.</summary>
    public string? UserId { get; }

    /// <summary>The caller's Agent profile in the tenant, or <see langword="null"/> when it has none.</summary>
    public Agent? Agent { get; }

    /// <summary>Resolves the caller of <paramref name="context"/> and its Agent profile in <paramref name="tenantId"/>.</summary>
    public static async Task<ConversationActor> ResolveAsync(
        HttpContext context, TenantId tenantId, IAgentStore agents, CancellationToken ct)
    {
        var userId = CallerIdentity.ResolveUserId(context.User);
        var agent = userId is null ? null : await agents.GetByUserIdAsync(tenantId, EntityId.From(userId), ct);
        return new ConversationActor(userId, agent);
    }

    /// <summary>Whether the caller's Agent profile owns <paramref name="conversation"/>.</summary>
    public bool Owns(Conversation conversation) =>
        Agent is not null
        && conversation.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId }
        && ownerId == Agent.AgentId;

    /// <summary>Whether <paramref name="conversation"/> is an open offer made to the caller's Agent profile.</summary>
    public bool IsOfferedTo(Conversation conversation) =>
        Agent is not null && ConversationOffer.IsOfferedTo(conversation, Agent.AgentId);

    /// <summary>Whether the caller of <paramref name="context"/> satisfies <see cref="SupervisorPolicy"/>.</summary>
    public static async Task<bool> IsSupervisorAsync(HttpContext context)
    {
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(context.User, SupervisorPolicy)).Succeeded;
    }

    /// <summary>
    /// Whether <paramref name="agentId"/> may become a conversation's owner: it is an agent of the tenant
    /// and its user is <c>Active</c> (licensed-agent-metering, agent-account-status-routing). An agent
    /// whose user is suspended, deactivated or missing gets the same answer as an unknown agent, so a
    /// transfer or reassign to it fails with <see cref="TargetAgentNotFound"/>.
    /// </summary>
    public static async Task<bool> IsOwnableTargetAsync(
        IAgentStore agents, IAgentAccountStatusLookup accountStatus, TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var agent = await agents.GetByIdAsync(tenantId, agentId, ct);
        return agent is not null && await accountStatus.IsActiveAsync(tenantId, agent.UserId, ct);
    }

    /// <summary>403 with the stable <paramref name="code"/> as the error.</summary>
    public static JsonHttpResult<ErrorResponse> Forbidden(string code) =>
        TypedResults.Json(new ErrorResponse(code), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>409 with <paramref name="reason"/> as the error: the conversation's state does not allow the change.</summary>
    public static JsonHttpResult<ErrorResponse> Conflict(string reason) =>
        TypedResults.Json(new ErrorResponse(reason), ApiJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status409Conflict);

    /// <summary>The audit metadata naming a conversation's owner before a change: its kind and id.</summary>
    public static void AddOwner(IDictionary<string, string> metadata, string prefix, ConversationOwner? owner)
    {
        metadata[$"{prefix}_kind"] = owner?.Kind.ToString() ?? "None";
        metadata[$"{prefix}_id"] = owner is { OwnerId: { } ownerId } ? ownerId.Value : string.Empty;
    }
}
