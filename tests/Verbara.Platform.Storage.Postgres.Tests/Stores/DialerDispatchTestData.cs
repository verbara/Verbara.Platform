using Verbara.Platform.Core;
using Verbara.Platform.Typification;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions (design D16): submissions and dispatch rows for the
/// insert-or-nothing and claim tests. Whole-second instants, so a timestamp round-trips through <c>timestamptz</c>.
/// </summary>
internal static class DialerDispatchTestData
{
    public const string Tenant = "tenant-dialer-dispatch";

    public static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    public static TypificationSubmission Submission(string conversationId, string leaf) => new()
    {
        TenantId = new TenantId(Tenant),
        ConversationId = EntityId.From(conversationId),
        AgentId = EntityId.From("agent-1"),
        SchemaId = EntityId.From("schema-1"),
        SchemaVersion = 1,
        SelectedNodePath = [EntityId.From("root"), EntityId.From(leaf)],
        LeafNodeId = EntityId.From(leaf),
        FieldValues = new Dictionary<string, string>(),
        Notes = "note",
        Source = SubmissionSource.Manual,
        CompletedAt = T0,
    };

    /// <summary>A row as the request writes it: pre-claimed by <paramref name="claimId"/> until <paramref name="expiresAt"/>.</summary>
    public static DialerDispositionDispatch Dispatch(
        string conversationId, long codeId = 7, string claimId = "11111111111111111111111111111111",
        DateTimeOffset? expiresAt = null, int attempts = 1, string? actionId = "5f0c2d9e8a7b4c3d9e1f0a2b3c4d5e6f") => new()
    {
        TenantId = Tenant,
        ConversationId = conversationId,
        CampaignId = 11,
        ActionId = actionId!,
        DialerContactId = 4711,
        DispositionCodeId = codeId,
        DispositionCode = "SALE",
        Comment = "note",
        CallbackAt = T0.AddDays(1),
        RequestsCallback = true,
        AgentId = "agent-1",
        Attempts = attempts,
        NextAttemptAt = T0,
        ClaimId = claimId,
        ClaimExpiresAt = expiresAt ?? T0.AddMinutes(1),
        CreatedAt = T0,
    };
}
