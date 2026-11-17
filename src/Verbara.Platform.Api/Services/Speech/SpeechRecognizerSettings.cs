namespace Verbara.Platform.Api.Services.Speech;

/// <summary>
/// What one speech recognizer is built from: the provider id and that provider's options. The caller
/// fills it from its own configuration (post-call analysis from its configuration section, Agent Assist
/// from the stored feature toggle) and decides which providers it accepts.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> never prints <see cref="ApiKey"/>, nor the user information or query of
/// <see cref="Endpoint"/>, so an instance can be logged or put in an exception message.
/// </remarks>
internal sealed class SpeechRecognizerSettings
{
    /// <summary>Lowercase provider id, for example <c>whisper</c>.</summary>
    public required string ProviderId { get; init; }

    /// <summary>The language the recognizer is asked to transcribe, for example <c>es</c> or <c>pt</c>.</summary>
    public required string Language { get; init; }

    /// <summary>The provider's API key, when it takes one.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The provider endpoint; unset keeps the Sdk recognizer's default.</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>The provider model; unset keeps the Sdk recognizer's default.</summary>
    public string? Model { get; init; }

    /// <summary>The settings without any credential: provider, language, endpoint host and model.</summary>
    public override string ToString() =>
        $"provider={ProviderId}, language={Language}, endpointHost={SafeEndpointHost(Endpoint)}, model={Model ?? "(default)"}";

    /// <summary>The host of <paramref name="endpoint"/> only, never its user information, path or query.</summary>
    internal static string SafeEndpointHost(Uri? endpoint) =>
        endpoint is null ? "(default)" : endpoint.IsAbsoluteUri ? endpoint.Host : "(relative)";
}
