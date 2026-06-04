using Verbara.Platform.Api.Voice;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Endpoints;

internal static partial class VoiceMetadataEndpoints
{
    public static void MapVoiceMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var voice = app.MapGroup("/admin/voice")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();

        voice.MapGet("/codecs", ListCodecs);
    }

    /// <summary>
    /// Returns the codecs Asterisk has loaded (<c>core show codecs</c> via AMI). Leader-gated like
    /// <see cref="TrunkConnectivityTester"/>: a follower pod or an unreachable Asterisk yields
    /// the static fallback catalog rather than an error, so the admin UI always has a usable list.
    /// When Voice-AMI is not configured (test environments or non-voice deployments) the AMI
    /// services are not registered and the endpoint also returns the fallback catalog.
    /// </summary>
    private static async Task<IResult> ListCodecs(
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("VoiceMetadataEndpoints");

        // AMI services are only registered when Asterisk:Ami:Hostname is configured — resolve
        // optionally so the endpoint works (returning fallback) in non-AMI environments.
        var leader = services.GetKeyedService<IClusterLeader>(VoiceLeaderResources.AmiOwner);
        if (leader is null || !leader.IsLeader)
            return Results.Ok(new VoiceCodecsResponse("fallback", KnownCodecs.FallbackCatalog));

        var serverPool = services.GetService<VerbaraServerPool>();
        var server = serverPool?.GetServer("primary");
        if (server is null)
            return Results.Ok(new VoiceCodecsResponse("fallback", KnownCodecs.FallbackCatalog));

        try
        {
            var response = await server.Connection
                .SendActionAsync<CommandResponse>(new CommandAction { Command = "core show codecs" }, ct)
                .ConfigureAwait(false);

            var installed = KnownCodecs.ParseInstalledCodecs(response.Output);
            return installed.Length > 0
                ? Results.Ok(new VoiceCodecsResponse("asterisk", installed))
                : Results.Ok(new VoiceCodecsResponse("fallback", KnownCodecs.FallbackCatalog));
        }
#pragma warning disable CA1031 // Do not catch general exception types — intentional graceful degradation: any AMI failure returns fallback catalog instead of 5xx
        catch (Exception ex)
        {
            LogCodecQueryFailed(logger, ex);
            return Results.Ok(new VoiceCodecsResponse("fallback", KnownCodecs.FallbackCatalog));
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to query Asterisk codecs via AMI; returning fallback catalog.")]
    private static partial void LogCodecQueryFailed(ILogger logger, Exception ex);
}
