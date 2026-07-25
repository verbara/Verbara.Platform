using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.OpenApi;

/// <summary>
/// OpenAPI registration for the Api host, extracted from the composition root so the
/// <c>Program.cs</c> LOC budget (verbara-meta/ADR-0012 gate #9) is not grown by adding
/// schema transformers. The caller owns the enable flag (it also gates
/// <c>MapOpenApi()</c>/Scalar) — spec generation is opt-in outside Development via
/// <c>Platform__OpenApi__Enabled=true</c> / <c>Platform:OpenApi:Enabled=true</c>.
///
/// openapi-typed-client (Platform/ADR-0035): the runtime <c>AddOpenApi()</c> surface is
/// exported via CI-runtime capture (start the host with the flag, curl <c>/openapi/v1.json</c>),
/// NOT a build-time generator — the host's ~28 eager-Postgres <c>IHostedService</c>s make a
/// no-live-DB design-time export infeasible, so this registration never needs to move.
///
/// The schema transformers make the emitted document tell the truth (Platform/ADR-0036):
/// <see cref="NumericSchemaTruthTransformer"/> strips the spurious .NET 10 numeric
/// <c>string</c> union; <see cref="ComplianceSeverityEnumTransformer"/> narrows
/// <c>ComplianceRuleSummaryDto.severity</c> to the <c>Info | Warning | Critical</c> enum.
/// </summary>
internal static class VerbaraOpenApiExtensions
{
    internal static IServiceCollection AddVerbaraOpenApi(this IServiceCollection services, bool enabled)
    {
        if (enabled)
        {
            services.AddOpenApi(o => o
                .AddSchemaTransformer<NumericSchemaTruthTransformer>()
                .AddSchemaTransformer<ComplianceSeverityEnumTransformer>());
        }

        return services;
    }
}
