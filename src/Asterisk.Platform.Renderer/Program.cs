using Asterisk.Platform.Core.Reports;
using Asterisk.Platform.Renderer;
using QuestPDF.Infrastructure;

// ── QuestPDF license ──
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateSlimBuilder(args);

// ── JSON serialization (AOT-safe) ──
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RendererJsonContext.Default);
});

// ── Renderers (keyed singletons) ──
builder.Services.AddKeyedSingleton<IReportRenderer, PdfReportRenderer>("pdf");
builder.Services.AddKeyedSingleton<IReportRenderer, CsvReportRenderer>("csv");

// ── Service key validation ──
var serviceKey = builder.Configuration["Services:ServiceKey"];

var app = builder.Build();

// ── Service key middleware ──
if (!string.IsNullOrEmpty(serviceKey))
{
    app.Use(async (context, next) =>
    {
        // Skip health endpoint
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }

        var requestKey = context.Request.Headers["X-Service-Key"].FirstOrDefault();
        if (!string.Equals(requestKey, serviceKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid or missing service key");
            return;
        }

        await next();
    });
}

// ── Endpoints ──
app.MapRenderEndpoint();

app.MapGet("/health", () =>
    Results.Json(
        new HealthResponse("healthy", RenderEndpoint.ActiveConcurrency, RenderEndpoint.MaxConcurrency),
        RendererJsonContext.Default.HealthResponse));

await app.RunAsync();
