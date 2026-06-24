using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Services;

public sealed class BillingTypificationCreditMeterTests
{
    [Fact]
    public async Task RecordAsync_ShouldRecordTokensWithInOutMetadata_WhenTotalPositive()
    {
        var metering = Substitute.For<IMeteringService>();
        var sut = new BillingTypificationCreditMeter(metering, Substitute.For<IClock>());

        await sut.RecordAsync(new TenantId("t1"), "conv1", promptTokens: 30, completionTokens: 70, totalTokens: 100, "gpt-x", CancellationToken.None);

        await metering.Received(1).RecordBatchAsync(
            Arg.Is<IReadOnlyList<UsageRecord>>(r =>
                r.Count == 1 &&
                r[0].UsageType == UsageType.AiAnalysis &&
                r[0].Unit == UsageUnit.Tokens &&
                r[0].Quantity == 100m &&
                r[0].ReferenceId == "conv1" &&
                r[0].Metadata != null &&
                r[0].Metadata!["inputTokens"] == "30" &&
                r[0].Metadata!["outputTokens"] == "70" &&
                r[0].Metadata!["model"] == "gpt-x"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldDoNothing_WhenTotalTokensNonPositive()
    {
        var metering = Substitute.For<IMeteringService>();
        var sut = new BillingTypificationCreditMeter(metering, Substitute.For<IClock>());

        await sut.RecordAsync(new TenantId("t1"), "conv1", 0, 0, 0, "gpt-x", CancellationToken.None);

        await metering.DidNotReceive().RecordBatchAsync(Arg.Any<IReadOnlyList<UsageRecord>>(), Arg.Any<CancellationToken>());
    }
}
