using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemorySurveyResponseStore : ISurveyResponseStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), SurveyResponse> _items = new();
    private readonly Lock _gate = new();

    public Task<bool> SaveAsync(SurveyResponse response, CancellationToken ct)
    {
        // Mirrors PostgresSurveyResponseStore: a stored response id, or a stored capture of the same email/SMS
        // dispatch (tenant, conversation, survey, channel), is not overwritten (#697).
        lock (_gate)
        {
            if (_items.ContainsKey((response.TenantId, response.ResponseId)))
                return Task.FromResult(false);
            if (IsDispatchKeyed(response.Channel) && _items.Values.Any(r => SameDispatch(r, response)))
                return Task.FromResult(false);
            _items[(response.TenantId, response.ResponseId)] = response;
            return Task.FromResult(true);
        }
    }

    private static bool IsDispatchKeyed(string? channel) => channel is "email" or "sms";

    private static bool SameDispatch(SurveyResponse a, SurveyResponse b) =>
        a.TenantId == b.TenantId && a.ConversationId == b.ConversationId && a.SurveyId == b.SurveyId &&
        string.Equals(a.Channel, b.Channel, StringComparison.Ordinal);

    public Task<IReadOnlyList<SurveyResponse>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        IReadOnlyList<SurveyResponse> result = _items.Values
            .Where(r => r.TenantId == tenantId && r.ConversationId == conversationId)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SurveyResponse>> GetBySurveyAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        IReadOnlyList<SurveyResponse> result = _items.Values
            .Where(r => r.TenantId == tenantId && r.SurveyId == surveyId)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SurveyResponse>> GetByQueueAndChannelAsync(
        TenantId tenantId, string queueName, string channel, DateRange range, CancellationToken ct)
    {
        IReadOnlyList<SurveyResponse> result = _items.Values
            .Where(r => r.TenantId == tenantId
                        && r.Channel is not null
                        && string.Equals(r.QueueName, queueName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase)
                        && r.CapturedAt.HasValue
                        && range.Contains(r.CapturedAt.Value))
            .OrderByDescending(r => r.CapturedAt!.Value)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SurveyResponse>> GetByChannelAndRangeAsync(
        TenantId tenantId, string? channel, DateRange range, CancellationToken ct)
    {
        var hasChannel = !string.IsNullOrWhiteSpace(channel);
        IReadOnlyList<SurveyResponse> result = _items.Values
            .Where(r => r.TenantId == tenantId
                        && r.Channel is not null
                        && (!hasChannel || string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase))
                        && r.CapturedAt.HasValue
                        && range.Contains(r.CapturedAt.Value))
            .OrderByDescending(r => r.CapturedAt!.Value)
            .ToList();

        return Task.FromResult(result);
    }
}
