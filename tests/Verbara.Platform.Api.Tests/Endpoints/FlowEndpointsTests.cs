using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Flows;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests.Endpoints;

public sealed class FlowEndpointsTests
{
    [Fact]
    public void FlowResponseTypes_ShouldResolveInApiJsonContext()
    {
        // FlowEndpoints handlers return IResult, so the response payload type is invisible to both
        // the compiler and the trim/AOT analyzers. ListFlows in particular returns
        // IFlowStore.ListAsync's result verbatim — no DTO projection — which makes the COLLECTION a
        // root serializable type, not just FlowDefinition.
        //
        // A WebApplicationFactory test cannot catch a miss here: the test host keeps reflection
        // enabled, so JsonSerializer resolves the type at runtime and the endpoint answers 200.
        // Only the published image (JsonSerializerIsReflectionEnabledByDefault=false) throws
        // NotSupportedException and turns GET /admin/flows into a 500. Assert on the resolver
        // directly instead.
        var required = new[] { typeof(FlowDefinition), typeof(IReadOnlyList<FlowDefinition>) };

        var unregistered = required
            .Where(t => ApiJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .ToList();

        unregistered.Should().BeEmpty(
            "every type FlowEndpoints hands to Results.Ok must be in ApiJsonContext for the AOT image");
    }
}
