// Verbara.Platform.Benchmarks — contact-history-opens-by-assignment task 3.6, cell T-2 (frozen plan:
// Predictions/contact-history-opens-by-assignment.md, sha256 a761a6da…; verbara-meta/ADR-0030 M-cost).
//
// One operation evaluates the delivery `.Where` of SseEndpoints.StreamEvents for every pair of the fixture's recipient
// classes (6 subscribers: a reader of every conversation, the owner, the offered agent, another agent, a handle holder
// without a profile, an unclassified stream) and four events (conversation.message, agentassist.suggestion,
// csat.response_recorded, and the non-content conversation.state_changed): 24 evaluations per op. Divide by 24 for the
// per-event-and-subscriber figures of the plan.
//
// CONTENT_DELIVERY_MODE in the environment selects the variant, so A and B run the same binary in separate processes,
// interleaved A/B/A/B:
//   off    (A) today's delivery: the revocation guard and PlatformDeliveryFilter only;
//   on     (B) plus ConversationContentDelivery.IsDeliverable (internal to the Api, bound once to a delegate);
//   format (control) as `on`, and the predicate also formats the audience to a string, which must show bytes per op.
//
// Repro: CONTENT_DELIVERY_MODE=on dotnet run -c Release --project tests/Verbara.Platform.Benchmarks -- --filter '*ConversationContentDeliveryBench*'

using System.Globalization;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Verbara.Platform.Core;
using Verbara.Sdk.Push.Delivery;

namespace Verbara.Platform.Benchmarks;

[MemoryDiagnoser]
public class ConversationContentDeliveryBench
{
    private const string Tenant = "bench-tenant";
    private const string Owner = "agent-owner";
    private const string Offered = "agent-offered";

    private readonly PlatformDeliveryFilter _filter = new();
    private Func<PlatformEvent, bool, EntityId?, bool> _isDeliverable = null!;
    private string _mode = "off";
    private PlatformEvent[] _events = null!;
    private Subscriber[] _subscribers = null!;
    private string? _sink;

    private sealed record Subscriber(SubscriberContext Context, bool ReadsAll, EntityId? HandlingAgentId);

    [GlobalSetup]
    public void Setup()
    {
        _mode = Environment.GetEnvironmentVariable("CONTENT_DELIVERY_MODE") ?? "off";
        if (_mode is not ("off" or "on" or "format"))
            throw new InvalidOperationException($"CONTENT_DELIVERY_MODE must be off, on or format, not {_mode}.");
        Console.WriteLine($"// CONTENT_DELIVERY_MODE={_mode}");

        var delivery = typeof(Verbara.Platform.Api.Services.ConversationCloseMetrics).Assembly
            .GetType("Verbara.Platform.Api.Endpoints.Shared.ConversationContentDelivery", throwOnError: true)!;
        _isDeliverable = delivery
            .GetMethod("IsDeliverable", BindingFlags.Static | BindingFlags.Public, [typeof(PlatformEvent), typeof(bool), typeof(EntityId?)])!
            .CreateDelegate<Func<PlatformEvent, bool, EntityId?, bool>>();

        _events =
        [
            new ConversationMessageEvent(Tenant, "conv-1", "m-1", "Inbound", "WhatsApp")
                { Audience = new ConversationAudience(Owner, Offered) },
            new AgentAssistSuggestionEvent(Tenant, "as-1", Owner, "conv-1", "sg-1", "Offer the tracking number.", "Important", "Keyword", null),
            new CsatResponseRecordedEvent(Tenant, "r-1", "s-1", "conv-1", "WhatsApp", "Support", 5, null, DateTimeOffset.UtcNow),
            new ConversationStateChangedEvent(Tenant, "conv-1", "Active", "Resolved"),
        ];

        SubscriberContext Context(string user) => new(Tenant, user, new HashSet<string>(), new HashSet<string>());
        _subscribers =
        [
            new(Context("u-reader"), true, null),
            new(Context("u-owner"), false, EntityId.From(Owner)),
            new(Context("u-offered"), false, EntityId.From(Offered)),
            new(Context("u-other"), false, EntityId.From("agent-other")),
            new(Context("u-no-profile"), false, null),
            new(Context("u-unclassified"), false, null),
        ];

        // The instrument checks itself: in `on`, the content events reach exactly the fixture's recipients.
        var delivered = Evaluate();
        // message: reader, owner, offered; suggestion: reader, the named owner; csat: reader; state change: all six.
        var expected = _mode == "off" ? 24 : 3 + 2 + 1 + 6;
        if (delivered != expected)
            throw new InvalidOperationException($"{_mode} delivered {delivered} of 24 pairs, not {expected}.");
    }

    [Benchmark]
    public int Deliver() => Evaluate();

    private int Evaluate()
    {
        var delivered = 0;
        foreach (var subscriber in _subscribers)
        {
            foreach (var evt in _events)
            {
                if (evt is not UserAccessRevokedEvent
                    && _filter.IsDeliverableToSubscriber(evt, subscriber.Context)
                    && Content(evt, subscriber))
                {
                    delivered++;
                }
            }
        }

        return delivered;
    }

    private bool Content(PlatformEvent evt, Subscriber subscriber)
    {
        switch (_mode)
        {
            case "off":
                return true;
            case "on":
                return _isDeliverable(evt, subscriber.ReadsAll, subscriber.HandlingAgentId);
            default:
                if (evt is IConversationContentEvent content)
                    _sink = string.Create(CultureInfo.InvariantCulture, $"{content.Audience.OwnerAgentId}|{content.Audience.OfferedToAgentId}|{subscriber.ReadsAll}");
                return _isDeliverable(evt, subscriber.ReadsAll, subscriber.HandlingAgentId);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => GC.KeepAlive(_sink);
}
