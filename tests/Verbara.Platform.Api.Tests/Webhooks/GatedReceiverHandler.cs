using System.Collections.Concurrent;
using System.Net;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>
/// The tenant's webhook receiver, in process. It counts every request per <c>X-Webhook-Id</c> as it arrives and
/// answers 200. When <c>holdFirstRequest</c> is set, the first request it receives signals
/// <see cref="FirstRequestArrived"/> and then stays open until <see cref="ReleaseFirstRequest"/>, which is how a
/// test keeps one POST in flight while something else runs. Every later request is answered at once.
/// No wall-clock waits: the gate is a <see cref="TaskCompletionSource"/> (spec <c>test-determinism</c>).
/// </summary>
internal sealed class GatedReceiverHandler(bool holdFirstRequest = true) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _postsPerWebhookId = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();
    private readonly TaskCompletionSource _firstRequestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requests;

    /// <summary>Completes when the first request has arrived (and, if held, is now waiting on the gate).</summary>
    public Task FirstRequestArrived => _firstRequestArrived.Task;

    /// <summary>Every request received, in arrival order.</summary>
    public IReadOnlyList<ReceivedWebhook> Received => [.. _received];

    /// <summary>How many requests carried this <c>X-Webhook-Id</c>.</summary>
    public int PostsFor(string webhookId) => _postsPerWebhookId.GetValueOrDefault(webhookId);

    /// <summary>Lets the held first request answer 200.</summary>
    public void ReleaseFirstRequest() => _releaseFirstRequest.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var webhookId = request.Headers.TryGetValues("X-Webhook-Id", out var ids) ? string.Join(",", ids) : string.Empty;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        _postsPerWebhookId.AddOrUpdate(webhookId, 1, static (_, count) => count + 1);
        _received.Enqueue(new ReceivedWebhook(webhookId, request.Method.Method, body));

        if (Interlocked.Increment(ref _requests) == 1)
        {
            _firstRequestArrived.TrySetResult();
            if (holdFirstRequest)
                await _releaseFirstRequest.Task.WaitAsync(cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>One request as the receiver saw it.</summary>
internal sealed record ReceivedWebhook(string WebhookId, string Method, string Body);
