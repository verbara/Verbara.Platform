using Verbara.Sdk.Pro.AgentAssist.Storage.Postgres.Stores;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The stored Agent Assist records of one session, as the session routes read them. The Pro stores are sealed
/// classes, so the routes read them through this seam.
/// </summary>
internal interface IAgentAssistRecordReader
{
    /// <summary>The suggestion rows stored under <paramref name="sessionId"/>, in the store's order.</summary>
    Task<IReadOnlyList<SuggestionLogRow>> SuggestionsBySessionAsync(string sessionId, CancellationToken ct);

    /// <summary>The compliance alert rows stored under <paramref name="sessionId"/>, in the store's order.</summary>
    Task<IReadOnlyList<ComplianceAlertRow>> ComplianceBySessionAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// The production <see cref="IAgentAssistRecordReader"/>. The stores are registered only with an Analytics
/// connection string, so each is resolved when a record is read, never when this reader is built.
/// </summary>
internal sealed class AgentAssistRecordReader : IAgentAssistRecordReader
{
    private readonly IServiceProvider _services;

    public AgentAssistRecordReader(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    public Task<IReadOnlyList<SuggestionLogRow>> SuggestionsBySessionAsync(string sessionId, CancellationToken ct) =>
        _services.GetRequiredService<SuggestionLogStore>().QueryBySessionAsync(sessionId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ComplianceAlertRow>> ComplianceBySessionAsync(string sessionId, CancellationToken ct) =>
        _services.GetRequiredService<ComplianceAlertStore>().QueryBySessionAsync(sessionId, ct);
}
