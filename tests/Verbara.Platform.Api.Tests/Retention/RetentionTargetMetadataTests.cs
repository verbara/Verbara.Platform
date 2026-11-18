using FluentAssertions;
using Verbara.Platform.Api.Endpoints.Retention;

namespace Verbara.Platform.Api.Tests.Retention;

/// <summary>
/// The admin retention overview labels each registered target with its schema and table. Pro <c>2.27.0-pro</c>'s
/// <c>AddCallAnalyticsRetentionTargets</c> registers <c>call_analysis_queue_ended</c> (ended entries of the durable
/// analysis queue) beside <c>call_analysis_results</c>; without a row it reads <c>unknown.unknown</c>.
/// </summary>
public sealed class RetentionTargetMetadataTests
{
    [Fact]
    public void Resolve_ShouldNameTheAnalysisQueueTable_WhenTheTargetIsTheQueuesEndedEntries()
    {
        var resolved = RetentionTargetMetadata.Resolve("call_analysis_queue_ended");

        resolved.Should().NotBe(("unknown", "unknown"));
        resolved.Should().Be((RetentionTargetMetadata.Resolve("call_analysis_results").Schema, "call_analysis_queue"));
    }
}
