using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Mail.Tests.Hosting;

/// <summary>
/// A logger provider that keeps every record the host writes to it, with everything a log sink could
/// persist from that record: category, level, the formatted message, each structured state value, the
/// values of the scopes active when it was written, and the exception.
/// </summary>
/// <remarks>
/// Attach it with <c>AddProvider</c> only. Never pair it with <c>AddFilter</c> or <c>SetMinimumLevel</c>:
/// a test host applies its logging configuration after Program.cs, so a test-side rule for a category
/// would override the host's own rule for that category and decide the outcome either way.
/// </remarks>
internal sealed class LogRecordCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLogRecord> _records = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<CapturedLogRecord> Records => [.. _records];

    /// <summary>Every record in which <paramref name="value"/> appears anywhere a sink could write it.</summary>
    public IReadOnlyList<CapturedLogRecord> RecordsContaining(string value) =>
        [.. _records.Where(record => record.Contains(value))];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private void Add<TState>(
        string category, LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = new List<string>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            values.AddRange(pairs.Select(pair => $"{pair.Key}={pair.Value}"));

        var scopes = new List<string>();
        _scopes.ForEachScope(static (scope, list) =>
        {
            list.Add(scope?.ToString() ?? string.Empty);
            if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                list.AddRange(scopePairs.Select(pair => $"{pair.Key}={pair.Value}"));
        }, scopes);

        _records.Enqueue(new CapturedLogRecord(
            category, level, eventId, formatter(state, exception), values, scopes, exception?.ToString()));
    }

    private sealed class CaptureLogger(string category, LogRecordCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        // The host's filter rules have already been applied by the time a record reaches a provider.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                owner.Add(category, logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>One captured log record.</summary>
internal sealed record CapturedLogRecord(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyList<string> StateValues,
    IReadOnlyList<string> ScopeValues,
    string? Exception)
{
    public bool Contains(string value) =>
        Message.Contains(value, StringComparison.Ordinal)
        || StateValues.Any(v => v.Contains(value, StringComparison.Ordinal))
        || ScopeValues.Any(v => v.Contains(value, StringComparison.Ordinal))
        || (Exception?.Contains(value, StringComparison.Ordinal) ?? false);

    public override string ToString() => $"[{Level}] {Category}: {Message}";
}
