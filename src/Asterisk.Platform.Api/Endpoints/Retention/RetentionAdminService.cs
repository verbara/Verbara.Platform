using System.Diagnostics;
using Asterisk.Sdk.Pro.Storage.Common.Retention;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Endpoints.Retention;

/// <summary>
/// Default <see cref="IRetentionAdminService"/> implementation. Drives manual
/// runs directly over the registered <see cref="IRetentionTarget"/> collection
/// (rather than delegating to <c>RetentionService.RunOnceAsync</c>) so the
/// admin <c>dryRun</c> override can take effect immediately without needing to
/// mutate <c>IOptions&lt;RetentionOptions&gt;</c>.
/// </summary>
public sealed partial class RetentionAdminService : IRetentionAdminService
{
    private readonly IRetentionTarget[] _targets;
    private readonly RetentionOptions _options;
    private readonly RetentionAdminState _state;
    private readonly RetentionExecutionTracker _tracker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionAdminService> _logger;

    public RetentionAdminService(
        IEnumerable<IRetentionTarget> targets,
        IOptions<RetentionOptions> options,
        RetentionAdminState state,
        RetentionExecutionTracker tracker,
        TimeProvider timeProvider,
        ILogger<RetentionAdminService> logger)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _targets = targets.ToArray();
        _options = options.Value;
        _state = state;
        _tracker = tracker;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public IReadOnlyList<RetentionTargetDto> ListTargets()
    {
        var result = new List<RetentionTargetDto>(_targets.Length);
        foreach (var target in _targets)
        {
            var (schema, table) = RetentionTargetMetadata.Resolve(target.Name);
            var window = target.CustomWindow ?? _options.DefaultWindow;
            var record = _tracker.Get(target.Name);
            result.Add(new RetentionTargetDto
            {
                Name = target.Name,
                Schema = schema,
                Table = table,
                WindowDays = (int)Math.Round(window.TotalDays),
                LastExecutionAt = record?.At,
                LastRowsPurged = record?.RowsPurged,
                LastStatus = record?.Status,
                LastWasDryRun = record?.WasDryRun ?? false,
            });
        }
        return result;
    }

    public RetentionConfigDto GetConfig() => new()
    {
        DryRun = _state.DryRun,
        DefaultWindowDays = (int)Math.Round(_options.DefaultWindow.TotalDays),
        BatchSize = _options.BatchSize,
        CronExpression = _options.CronExpression,
        RegisteredTargetCount = _targets.Length,
    };

    public async Task<RetentionRunResultDto> RunNowAsync(
        bool dryRunOverride,
        string? targetName,
        CancellationToken ct)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var outcomes = new List<RetentionRunTargetOutcomeDto>(_targets.Length);

        foreach (var target in _targets)
        {
            if (ct.IsCancellationRequested) break;

            if (!string.IsNullOrEmpty(targetName) && !string.Equals(target.Name, targetName, StringComparison.Ordinal))
            {
                outcomes.Add(new RetentionRunTargetOutcomeDto
                {
                    Name = target.Name,
                    Status = "skipped",
                    RowsPurged = 0,
                    ErrorMessage = null,
                    DurationMs = 0,
                });
                continue;
            }

            var window = target.CustomWindow ?? _options.DefaultWindow;
            var cutoff = _timeProvider.GetUtcNow() - window;
            var sw = Stopwatch.StartNew();
            try
            {
                var purged = await target.PurgeAsync(cutoff, dryRunOverride, _options.BatchSize, ct).ConfigureAwait(false);
                sw.Stop();

                outcomes.Add(new RetentionRunTargetOutcomeDto
                {
                    Name = target.Name,
                    Status = "success",
                    RowsPurged = purged,
                    ErrorMessage = null,
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                });

                _tracker.Record(target.Name, _timeProvider.GetUtcNow(), purged, "success", dryRunOverride);
                LogManualPurge(_logger, target.Name, purged, dryRunOverride);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                sw.Stop();
                outcomes.Add(new RetentionRunTargetOutcomeDto
                {
                    Name = target.Name,
                    Status = "error",
                    RowsPurged = 0,
                    ErrorMessage = ex.Message,
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                });

                _tracker.Record(target.Name, _timeProvider.GetUtcNow(), 0, "error", dryRunOverride);
                LogManualPurgeFailed(_logger, ex, target.Name);
            }
        }

        return new RetentionRunResultDto
        {
            DryRun = dryRunOverride,
            StartedAt = startedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            Targets = outcomes,
        };
    }

    public bool SetDryRun(bool value) => _state.SetDryRun(value);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Retention manual run target={Target} rows={Rows} dryRun={DryRun}.")]
    private static partial void LogManualPurge(ILogger logger, string target, int rows, bool dryRun);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Retention manual run target={Target} failed.")]
    private static partial void LogManualPurgeFailed(ILogger logger, Exception exception, string target);
}
