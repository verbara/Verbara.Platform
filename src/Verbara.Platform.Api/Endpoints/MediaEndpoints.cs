using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Core;
using Verbara.Platform.Media;

namespace Verbara.Platform.Api.Endpoints;

internal static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/media").RequireAuthorization("SupervisorPlus");

        group.MapPost("/upload", UploadFile)
             .DisableAntiforgery();
        group.MapGet("/{id}/download", DownloadFile);
    }

    private static async Task<IResult> UploadFile(
        HttpContext context,
        IMediaService mediaService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (!context.Request.HasFormContentType)
            return Results.BadRequest(new ErrorResponse("Request must be multipart/form-data"));

        var form = await context.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");

        if (file is null || file.Length == 0)
            return Results.BadRequest(new ErrorResponse("No file provided or file is empty"));

        // `sub` first, NameIdentifier fallback — JwtBearerOptions.MapInboundClaims=false
        // (Program.cs:118) means the JWT `sub` claim is not auto-remapped.
        var uploadedBy = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        try
        {
            await using var stream = file.OpenReadStream();
            var mediaFile = await mediaService.UploadAsync(
                tenantId,
                file.FileName,
                file.ContentType,
                stream,
                uploadedBy: uploadedBy,
                ct: ct);

            return Results.Created($"/media/{mediaFile.FileId}/download", mediaFile);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> DownloadFile(
        string id,
        HttpContext context,
        IMediaService mediaService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var stream = await mediaService.DownloadAsync(tenantId, EntityId.From(id), ct);

        if (stream is null)
            return Results.NotFound();

        return Results.File(stream, "application/octet-stream");
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
