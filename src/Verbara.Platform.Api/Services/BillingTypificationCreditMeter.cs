using System.Globalization;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Typification.Ai;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// <see cref="ITypificationCreditMeter"/> backed by the Billing <see cref="IMeteringService"/>.
/// Records platform-managed LLM token usage as a single <c>AiAnalysis</c>/<c>Tokens</c>
/// <see cref="UsageRecord"/> carrying input/output token counts + model in metadata
/// (via <c>RecordBatchAsync</c> — the metering API that preserves <c>Metadata</c>).
/// </summary>
internal sealed class BillingTypificationCreditMeter(IMeteringService metering, IClock clock) : ITypificationCreditMeter
{
    private readonly IMeteringService _metering = metering ?? throw new ArgumentNullException(nameof(metering));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Task RecordAsync(TenantId tenantId, string conversationId, int promptTokens, int completionTokens, int totalTokens, string model, CancellationToken ct)
    {
        if (totalTokens <= 0)
            return Task.CompletedTask;

        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tenantId,
            UsageType = UsageType.AiAnalysis,
            Quantity = totalTokens,
            Unit = UsageUnit.Tokens,
            Channel = null,
            ReferenceId = conversationId,
            RecordedAt = _clock.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inputTokens"] = promptTokens.ToString(CultureInfo.InvariantCulture),
                ["outputTokens"] = completionTokens.ToString(CultureInfo.InvariantCulture),
                ["model"] = model,
            },
        };
        return _metering.RecordBatchAsync(new[] { record }, ct);
    }
}
