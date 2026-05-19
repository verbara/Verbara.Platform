using Verbara.Platform.Realtime.Auth;
using Verbara.Platform.Realtime.Contracts;
using Verbara.Platform.Realtime.Endpoints;

var builder = WebApplication.CreateSlimBuilder(args);

// AOT-safe JSON serialization for the contracts shared with Platform.Api.
// Full SignalR JSON config arrives with Phase A.2+A.3 (hub serialization
// uses MessagePack or SignalR's own JsonHubProtocol with separate setup).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RealtimeContractsJsonContext.Default);
});

// Service-to-service auth between Realtime and Platform.Api.
// Phase A.1 stages this without the full SignalR hub mapping — only the
// /health endpoint is exposed and skipped by the middleware.
var serviceKey = builder.Configuration["Services:ServiceKey"];

var app = builder.Build();

if (!string.IsNullOrEmpty(serviceKey))
{
    app.UseServiceKey(serviceKey);
}

app.MapHealthEndpoint();

await app.RunAsync();
