using System.Diagnostics;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Where an endpoint puts the message of an exception it caught and turned into a 4xx: the log,
/// correlated by trace id, never the response body (v2.27.0, D13; enforced by the Architecture.Tests
/// <c>Endpoints_ShouldNotCopyExceptionMessage_IntoErrorResponses</c> scan).
/// </summary>
internal static partial class RejectedRequestLog
{
    private const string LoggerCategory = "Verbara.Platform.Api.Endpoints.RejectedRequest";

    /// <summary>Logs why <paramref name="operation"/> was refused, with the request's trace id.</summary>
    internal static void Write(HttpContext context, string operation, Exception exception) =>
        LogRejected(
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory),
            operation,
            Activity.Current?.Id ?? context.TraceIdentifier,
            exception);

    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
        Message = "Request refused: {Operation} (traceId {TraceId})")]
    private static partial void LogRejected(ILogger logger, string operation, string traceId, Exception exception);
}
