using System.Collections.Concurrent;
using Asterisk.Sdk.Pro.CallAnalytics.Domain;
using Asterisk.Sdk.Pro.CallAnalytics.Store;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryCallAnalyticsStore : ICallAnalyticsStore
{
    private readonly ConcurrentDictionary<(string TenantId, string SessionId), CallAnalysisResult> _results = new();

    public ValueTask SaveAsync(CallAnalysisResult result, CancellationToken ct = default)
    {
        _results[(result.TenantId, result.SessionId)] = result;
        return default;
    }

    public ValueTask<CallAnalysisResult?> GetAsync(string sessionId, string tenantId, CancellationToken ct = default)
    {
        _results.TryGetValue((tenantId, sessionId), out var result);
        return new(result);
    }

    public ValueTask<IReadOnlyList<CallAnalysisResult>> QueryAsync(CallAnalyticsQuery query, CancellationToken ct = default)
    {
        var results = _results.Values
            .Where(r => r.TenantId == query.TenantId)
            .Where(r => query.From is null || r.AnalyzedAt >= query.From)
            .Where(r => query.To is null || r.AnalyzedAt <= query.To)
            .Where(r => query.MinQaScore is null || (r.QualityScore is not null && r.QualityScore.TotalScore >= query.MinQaScore))
            .Where(r => query.MaxQaScore is null || (r.QualityScore is not null && r.QualityScore.TotalScore <= query.MaxQaScore))
            .Where(r => query.HasComplianceViolations is null || (r.ComplianceViolations.Count > 0) == query.HasComplianceViolations)
            .OrderByDescending(r => r.AnalyzedAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToList();
        return new(results);
    }
}
