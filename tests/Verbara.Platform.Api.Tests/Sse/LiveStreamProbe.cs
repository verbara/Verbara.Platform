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
internal sealed class LiveStreamProbe : IAsyncDisposable
{
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<SseFrame> _frames = [];
    private readonly Task _reader;

    private LiveStreamProbe(string name, HttpClient client, HttpResponseMessage response, Stream body)
    {
        Name = name;
        _client = client;
        _response = response;
        _reader = Task.Run(() => ReadAsync(body, _stop.Token));
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

    /// <summary>Opens the stream with <paramref name="client"/> (which the probe then owns) and waits for the 200.</summary>
    public static async Task<LiveStreamProbe> OpenAsync(string name, HttpClient client)
    {
        using var timeout = new CancellationTokenSource(SyncTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events/stream");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{name}'s stream must open");
        var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        return new LiveStreamProbe(name, client, response, body);
    }

    /// <summary>
    /// Publishes a marker on <paramref name="bus"/> until every probe has received one: the handler subscribes to the
    /// bus after it flushes the headers, so the first marker can precede the subscription. Returns once all have it.
    /// </summary>
    public static async Task SyncAsync(PlatformEventBus bus, string tenantId, params LiveStreamProbe[] probes)
    {
        var marker = $"stream-marker-{Guid.NewGuid():N}";
        var deadline = DateTimeOffset.UtcNow + SyncTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            bus.Publish(new ConversationOfferExpiredEvent(tenantId, marker, "stream-marker"));
            for (var i = 0; i < 10; i++)
            {
                if (probes.All(p => p.Frames.Any(f => f.Member("conversationId") == marker)))
                    return;
                await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until every stream has read the marker
            }
        }

        var missing = probes.Where(p => !p.Frames.Any(f => f.Member("conversationId") == marker)).Select(p => p.Name);
        throw new TimeoutException($"the stream marker never reached: {string.Join(", ", missing)}");
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
                    {
                        lock (_frames)
                            _frames.Add(new SseFrame(eventName, json));
                    }

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
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _response.Dispose();
        try
        {
            await _reader;
        }
        catch (OperationCanceledException)
        {
            // Expected: the reader ends on the stop just requested.
        }

        _client.Dispose();
        _stop.Dispose();
    }
}
