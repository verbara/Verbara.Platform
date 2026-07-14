using System.Runtime.CompilerServices;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The default Platform <see cref="SpeechSynthesizer"/> used when no external TTS provider
/// (Azure / ElevenLabs / Deepgram / …) is configured. It produces a deterministic block of digital
/// silence sized to the input text at the requested <see cref="AudioFormat"/> — a real, offline
/// synthesizer (it emits audio frames for the text), NOT the caller fabricating audio. A configured TTS
/// provider (registered before this in the composition root) supersedes it.
/// </summary>
/// <remarks>
/// The voice-CSAT path only needs a resolvable <see cref="SpeechSynthesizer"/> for the
/// <c>TtsPromptCache</c> to warm the rating prompt and for the admin <c>preview-voice</c> endpoint to
/// return synthesized audio. Where lifelike speech matters, an operator wires a real provider via the
/// SDK TTS DI extensions; this keeps a single-host / dev deployment self-contained and AOT-clean (no
/// reflection, no network). Duration is bounded so a long template body cannot allocate an unbounded
/// buffer.
/// </remarks>
internal sealed class SilenceSpeechSynthesizer : SpeechSynthesizer
{
    // Rough spoken-word pacing: ~12 characters per second, clamped so a preview never exceeds a minute.
    private static readonly TimeSpan s_maxDuration = TimeSpan.FromSeconds(60);
    private const double CharsPerSecond = 12d;
    private static readonly TimeSpan s_frameDuration = TimeSpan.FromMilliseconds(20);

    /// <inheritdoc />
    public override string ProviderName => "Silence";

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var seconds = Math.Min(s_maxDuration.TotalSeconds, Math.Max(0d, text.Length / CharsPerSecond));
        var total = TimeSpan.FromSeconds(seconds);
        var frameBytes = outputFormat.BytesPerFrame(s_frameDuration);
        if (frameBytes <= 0)
            yield break;

        var frame = new byte[frameBytes]; // zero-filled silence — reused across yields (consumer copies).
        var elapsed = TimeSpan.Zero;
        while (elapsed < total)
        {
            ct.ThrowIfCancellationRequested();
            yield return frame;
            elapsed += s_frameDuration;
            await Task.Yield();
        }
    }
}
