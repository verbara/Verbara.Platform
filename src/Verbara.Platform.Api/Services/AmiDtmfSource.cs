using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Verbara.Sdk;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.CsatRunner.Adapters.Voice;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform implementation of the Pro-defined <see cref="IDtmfSource"/> seam (csat-completion,
/// Platform/ADR-0020, verbara-meta/ADR-0005 open-core boundary). Adapts the AMI event stream
/// (<see cref="IAmiConnection.Subscribe(System.IObserver{ManagerEvent})"/>) into a per-channel digit
/// stream the Pro <c>DtmfCollector</c> reads: it subscribes to the primary server's AMI events, filters
/// to the AMI <c>DTMFEnd</c> event on the requested <c>Channel</c>, and yields each pressed
/// <c>Digit</c> (<c>"0"</c>-<c>"9"</c>, <c>"*"</c>, <c>"#"</c>) in press order until the caller-supplied
/// token is signaled or the leg ends.
/// </summary>
/// <remarks>
/// Native AOT clean (no reflection): the AMI event is matched on the string-keyed
/// <see cref="ManagerEvent.RawFields"/> map (<c>Event</c> / <c>Channel</c> / <c>Digit</c>), not by a
/// generated event type. When AMI is not connected (no primary server) the stream completes empty and
/// the collector times out gracefully → no rating. The live DTMF wire is exercised in the voice lab
/// harness, mirroring the other AMI wires in <c>VoiceConversationBridge</c>.
/// </remarks>
internal sealed class AmiDtmfSource : IDtmfSource
{
    private const string DtmfEndEvent = "DTMFEnd";

    private readonly VerbaraServerPool _serverPool;

    public AmiDtmfSource(VerbaraServerPool serverPool)
    {
        ArgumentNullException.ThrowIfNull(serverPool);
        _serverPool = serverPool;
    }

    public async IAsyncEnumerable<string> ReadDigitsAsync(
        string channelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        var server = _serverPool.GetServer("primary");
        if (server is null)
            yield break; // AMI not connected — no digits to observe (collector times out gracefully)

        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        using var observer = new DtmfObserver(channelId, channel.Writer);
        using var subscription = server.Connection.Subscribe(observer);
        using var registration = cancellationToken.Register(static state => ((ChannelWriter<string>)state!).TryComplete(), channel.Writer);

        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var digit))
                yield return digit;
        }
    }

    // Bridges the AMI push observer onto a Channel writer, filtering to DTMFEnd on the target leg.
    private sealed class DtmfObserver : IObserver<ManagerEvent>, IDisposable
    {
        private readonly string _channelId;
        private readonly ChannelWriter<string> _writer;

        public DtmfObserver(string channelId, ChannelWriter<string> writer)
        {
            _channelId = channelId;
            _writer = writer;
        }

        public void OnNext(ManagerEvent value)
        {
            var fields = value.RawFields;
            if (fields is null)
                return;
            if (!fields.TryGetValue("Event", out var evt) ||
                !string.Equals(evt, DtmfEndEvent, StringComparison.OrdinalIgnoreCase))
                return;

            if (!fields.TryGetValue("Channel", out var channel) ||
                !string.Equals(channel, _channelId, StringComparison.Ordinal))
                return;

            if (fields.TryGetValue("Digit", out var digit) && !string.IsNullOrEmpty(digit))
                _writer.TryWrite(digit);
        }

        public void OnError(Exception error) => _writer.TryComplete(error);

        public void OnCompleted() => _writer.TryComplete();

        public void Dispose() => _writer.TryComplete();
    }
}
