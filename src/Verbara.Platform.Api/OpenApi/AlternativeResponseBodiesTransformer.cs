using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Verbara.Platform.Api.OpenApi;

/// <summary>
/// Endpoint metadata: the route answers <see cref="StatusCode"/> with any one of <see cref="BodyTypes"/>. ApiExplorer keeps
/// one body type per status code (the last <c>Produces</c> wins), so a route whose status code carries two shapes
/// declares them here and <see cref="AlternativeResponseBodiesTransformer"/> emits them as <c>oneOf</c>.
/// </summary>
internal sealed class AlternativeResponseBodies(int statusCode, params Type[] bodyTypes)
{
    public int StatusCode { get; } = statusCode;

    public IReadOnlyList<Type> BodyTypes { get; } = bodyTypes;
}

/// <summary>
/// Replaces the body schema of each status code an endpoint marks with <see cref="AlternativeResponseBodies"/> by a
/// <c>oneOf</c> of component references, one per body type (realtime-objects-stay-in-their-tenant: Management create
/// answers 409 with <c>ErrorResponse</c> for an unavailable tenant id or <c>PlanAboveParentRefusal</c> for a plan above
/// the parent's). Each body type must also be declared by some route, so its component schema exists; the operation
/// test checks it. Reflection-free: the component id is the type's name, as the default schema naming gives it.
/// </summary>
internal sealed class AlternativeResponseBodiesTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        foreach (var marker in context.Description.ActionDescriptor.EndpointMetadata.OfType<AlternativeResponseBodies>())
        {
            var key = marker.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (operation.Responses?.TryGetValue(key, out var response) != true || response is not OpenApiResponse concrete)
                continue;
            var schema = new OpenApiSchema
            {
                OneOf = [.. marker.BodyTypes.Select(t => (IOpenApiSchema)new OpenApiSchemaReference(t.Name, context.Document))],
            };
            concrete.Content ??= new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal);
            concrete.Content["application/json"] = new OpenApiMediaType { Schema = schema };
        }

        return Task.CompletedTask;
    }
}
