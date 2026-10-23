using Verbara.Sdk.Ari.Client;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The two Stasis application names the Api registers on Asterisk, resolved in one place (Platform/ADR-0044,
/// spec <c>voice-stasis-app-ownership</c>). Asterisk hands a Stasis app to exactly one ARI WebSocket, the last one
/// to register it, so the dialplan's app (<see cref="Consumer"/>) is registered only by the inbound-voice leader's
/// <see cref="StasisInboundConsumer"/>, and the per-node ARI client of the cluster's Asterisk node registers an app
/// of its own (<see cref="Node"/>), which nothing in the dialplan targets.
/// </summary>
internal static class StasisAppNames
{
    /// <summary>The dialplan's app: <c>Stasis(verbara,…)</c> in <c>extensions.conf</c>.</summary>
    public const string ApplicationKey = "Asterisk:Ari:Application";

    /// <summary>The per-node ARI client's app.</summary>
    public const string NodeApplicationKey = "Asterisk:Ari:NodeApplication";

    private const string BaseUrlKey = "Asterisk:Ari:BaseUrl";
    private const string DefaultApplication = "verbara";
    private const string NodeSuffix = "-node";

    /// <summary><c>Asterisk:Ari:Application</c>, default <c>verbara</c>.</summary>
    public static string Consumer(IConfiguration configuration) =>
        NonBlank(configuration[ApplicationKey]) ?? DefaultApplication;

    /// <summary><c>Asterisk:Ari:NodeApplication</c>, default the dialplan's app followed by <c>-node</c>.</summary>
    public static string Node(IConfiguration configuration) =>
        NonBlank(configuration[NodeApplicationKey]) ?? Consumer(configuration) + NodeSuffix;

    /// <summary>
    /// The per-node ARI client options, or <see langword="null"/> when ARI is not configured. Throws when the two
    /// app names are equal (compared without regard to case), so the race ADR-0044 closes cannot come back through
    /// configuration. <c>Program.cs</c> calls this eagerly, before the host is built.
    /// </summary>
    public static AriClientOptions? NodeOptions(IConfiguration configuration)
    {
        var options = Options(configuration, Node(configuration));
        if (options is not null && string.Equals(options.Application, Consumer(configuration), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{NodeApplicationKey} ('{options.Application}') must differ from {ApplicationKey} " +
                $"('{Consumer(configuration)}'): only the inbound-voice leader may register the dialplan's Stasis app " +
                "(Platform/ADR-0044). Unset Asterisk:Ari:NodeApplication to use the default '<Application>-node'.");
        return options;
    }

    /// <summary>The Stasis consumer's ARI client options, or <see langword="null"/> when ARI is not configured.</summary>
    public static AriClientOptions? ConsumerOptions(IConfiguration configuration) =>
        Options(configuration, Consumer(configuration));

    private static AriClientOptions? Options(IConfiguration configuration, string application)
    {
        var baseUrl = NonBlank(configuration[BaseUrlKey]);
        if (baseUrl is null)
            return null;

        return new AriClientOptions
        {
            BaseUrl = baseUrl,
            Username = configuration["Asterisk:Ari:Username"] ?? "",
            Password = configuration["Asterisk:Ari:Password"] ?? "",
            Application = application,
        };
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
