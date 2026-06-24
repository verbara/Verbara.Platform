using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Records platform-managed Typification LLM usage for metering/billing. Called
/// ONLY for <c>AiSource.PlatformManaged</c> classifies (BYO is never metered —
/// the tenant pays its own provider). Tokens are the stored unit; AI Credits are
/// derived by aggregation downstream. No-op for non-positive token counts.
/// </summary>
public interface ITypificationCreditMeter
{
    Task RecordAsync(TenantId tenantId, string conversationId, int promptTokens, int completionTokens, int totalTokens, string model, CancellationToken ct);
}
