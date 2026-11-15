using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>One frame of <c>GET /api/v1/events/stream</c>: its <c>event:</c> name and its <c>data:</c> JSON.</summary>
internal sealed record SseFrame(string Event, JsonObject Data)
{
    public string? Member(string name) => Data[name]?.GetValue<string>();
}

/// <summary>
/// An open live stream of one subscriber, read in the background into a list of frames. Absence is asserted against
/// a marker: <see cref="SyncAsync"/> publishes a non-content, tenant-wide event (<c>conversation.offer_expired</c> for
/// a conversation id nobody holds) after the action under test, and waits until every probe received it. The bus
/// delivers each subscriber's events in publish order, so a frame published before the marker that a probe has not
/// received by then was not delivered to it.
/// </summary>
/// <remarks>
/// Every wait here is on a signal, not on a length of time (timed-tests-hold-on-a-loaded-runner, F3). The stream's
/// subscription to the bus is observed through <see cref="StreamProbeDeliveryFilter"/>, which the
/// <see cref="ContentDeliveryApiFactory"/> host puts in front of the stream's delivery filter and which reports, for
/// the request that carries <see cref="ProbeHeader"/>, each marker the subscription evaluates. A frame's arrival is
/// signalled by the reader. <see cref="HangGuard"/> only turns a hang into a message; a slow host that does
/// subscribe and deliver passes, whatever the delay.
/// </remarks>
internal sealed class LiveStreamProbe : IAsyncDisposable
{
    /// <summary>The request header that names the probe to <see cref="StreamProbeDeliveryFilter"/>.</summary>
    internal const string ProbeHeader = "X-Test-Live-Stream-Probe";

    /// <summary>The <see cref="ConversationOfferExpiredEvent.AgentId"/> every marker carries.</summary>
    internal const string MarkerAgentId = "stream-marker";

    /// <summary>
    /// How long a wait on a signal may last before it is reported as a hang. It bounds no expected latency: it is the
    /// point where the test stops waiting for a signal that is not coming, kept well inside CI's 5-minute
    /// <c>--blame-hang-timeout</c> so the failure carries this class's message instead of a hang dump.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    /// <summary>How often <see cref="SyncAsync"/> republishes the marker while a stream is not subscribed yet.</summary>
    private static readonly TimeSpan RepublishPacing = TimeSpan.FromMilliseconds(200);

    /// <summary>The open probes by id, for <see cref="StreamProbeDeliveryFilter"/>.</summary>
    private static readonly ConcurrentDictionary<string, LiveStreamProbe> OpenProbes = new(StringComparer.Ordinal);

    private readonly string _id;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<SseFrame> _frames = [];
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HttpClient? _client;
    private HttpResponseMessage? _response;
    private Task _reader = Task.CompletedTask;
    private TaskCompletionSource _framesChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _readerEnded;

    private LiveStreamProbe(string name)
    {
        Name = name;
        _id = Guid.NewGuid().ToString("N");
    }

    /// <summary>Who this stream belongs to, for failure messages.</summary>
    public string Name { get; }

    /// <summary>Every frame received so far, oldest first.</summary>
    public IReadOnlyList<SseFrame> Frames
    {
        get
        {
            lock (_frames)
                return [.. _frames];
        }
    }

    /// <summary>The <c>conversation.message</c> frames received so far for <paramref name="conversationId"/>.</summary>
    public IReadOnlyList<SseFrame> MessagesOf(string conversationId) =>
        [.. Frames.Where(f => f.Event == "conversation.message" && f.Member("conversationId") == conversationId)];

    /// <summary>Whether a <c>conversation.message</c> frame with <paramref name="messageId"/> was received.</summary>
    public bool Received(string messageId) =>
        Frames.Any(f => f.Event == "conversation.message" && f.Member("messageId") == messageId);

    /// <summary>
    /// Opens the stream with <paramref name="client"/> (which the probe then owns) and waits for the 200. The host must
    /// be a <see cref="ContentDeliveryApiFactory"/>, whose delivery filter reports the stream's subscription.
    /// </summary>
    public static async Task<LiveStreamProbe> OpenAsync(string name, HttpClient client)
    {
        var probe = new LiveStreamProbe(name) { _client = client };
        OpenProbes[probe._id] = probe;
        try
        {
            using var hang = new CancellationTokenSource(HangGuard);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events/stream");
            request.Headers.Add(ProbeHeader, probe._id);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, hang.Token);
            probe._response = response;
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{name}'s stream must open");
            var body = await response.Content.ReadAsStreamAsync(hang.Token);
            probe._reader = Task.Run(() => probe.ReadAsync(body, probe._stop.Token));
            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Publishes a marker on <paramref name="bus"/> and returns once every probe has received it. The handler
    /// subscribes to the bus after it flushes the headers, so a stream can still be unsubscribed here: the marker is
    /// republished until every probe's subscription has evaluated one, and then the wait is for each probe's reader.
    /// </summary>
    public static async Task SyncAsync(PlatformEventBus bus, string tenantId, params LiveStreamProbe[] probes)
    {
        var marker = $"stream-marker-{Guid.NewGuid():N}";
        using var hang = new CancellationTokenSource(HangGuard);

        // 1. The subscription (from the handler's header flush to its bus subscription). A subscription, once made,
        // lasts for the stream's life, and the bus hands each published event to every subscription inside Publish.
        var subscribed = Task.WhenAll(probes.Select(p => p._subscribed.Task));
        while (!subscribed.IsCompleted)
        {
            bus.Publish(new ConversationOfferExpiredEvent(tenantId, marker, MarkerAgentId));
            await Task.WhenAny(subscribed, Task.Delay(RepublishPacing, hang.Token)); // fence-allow: LOOP-DRIVER — republish pacing while a stream has not subscribed; completion is the subscription signal
            if (hang.IsCancellationRequested && !subscribed.IsCompleted)
            {
                var unsubscribed = probes.Where(p => !p._subscribed.Task.IsCompleted).Select(p => p.Name);
                throw new TimeoutException(
                    $"the stream never subscribed to the bus within {HangGuard.TotalSeconds:0} s: {string.Join(", ", unsubscribed)}");
            }
        }

        await subscribed;

        // Now every probe is subscribed, so this publish puts the marker in every stream's buffer.
        bus.Publish(new ConversationOfferExpiredEvent(tenantId, marker, MarkerAgentId));

        // 2. The delivery (the handler's write loop and this probe's reader), signalled by the reader.
        var arrived = await Task.WhenAll(probes.Select(p => p.WaitForMarkerAsync(marker, hang.Token)));
        var missing = probes.Where((_, i) => !arrived[i]).Select(p => p.Name).ToList();
        if (missing.Count > 0)
            throw new TimeoutException($"the stream marker never reached: {string.Join(", ", missing)}");
    }

    /// <summary>Called by <see cref="StreamProbeDeliveryFilter"/> when probe <paramref name="probeId"/>'s subscription
    /// evaluated a marker, with the filter's verdict.</summary>
    internal static void OnMarkerEvaluated(string probeId, bool deliverable)
    {
        if (!OpenProbes.TryGetValue(probeId, out var probe))
            return;
        if (deliverable)
            probe._subscribed.TrySetResult();
        else
            probe._subscribed.TrySetException(new InvalidOperationException(
                $"{probe.Name}'s stream is subscribed, but its delivery filter refused the stream marker"));
    }

    private async Task<bool> WaitForMarkerAsync(string marker, CancellationToken hang)
    {
        while (true)
        {
            Task changed;
            lock (_frames)
            {
                if (_frames.Any(f => f.Member("conversationId") == marker))
                    return true;
                if (_readerEnded)
                    return false;
                changed = _framesChanged.Task;
            }

            try
            {
                await changed.WaitAsync(hang);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private void Pulse(SseFrame? frame)
    {
        TaskCompletionSource changed;
        lock (_frames)
        {
            if (frame is null)
                _readerEnded = true;
            else
                _frames.Add(frame);
            changed = _framesChanged;
            _framesChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    private async Task ReadAsync(Stream body, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(body);
            string? eventName = null;
            var data = new System.Text.StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    return;
                if (line.Length == 0)
                {
                    if (eventName is not null && data.Length > 0 && JsonNode.Parse(data.ToString()) is JsonObject json)
                        Pulse(new SseFrame(eventName, json));

                    eventName = null;
                    data.Clear();
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    eventName = line["event: ".Length..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data.Append(line["data: ".Length..]);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: DisposeAsync stopped the reader.
        }
        catch (IOException)
        {
            // Expected: the server or DisposeAsync closed the stream.
        }
        catch (ObjectDisposedException)
        {
            // Expected: DisposeAsync disposed the response under the reader.
        }
        finally
        {
            Pulse(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        OpenProbes.TryRemove(_id, out _);
        await _stop.CancelAsync();
        _response?.Dispose();
        try
        {
            await _reader;
        }
        catch (OperationCanceledException)
        {
            // Expected: the reader ends on the stop just requested.
        }

        _client?.Dispose();
        _stop.Dispose();
    }
}
