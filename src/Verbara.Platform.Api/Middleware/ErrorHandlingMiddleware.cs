using System.Diagnostics;
using System.Text.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Middleware;

internal sealed partial class ErrorHandlingMiddleware
{
    /// <summary>
    /// The only detail a 5xx carries. An unexpected exception's message can hold driver, SQL or
    /// library text, so it goes to the log (correlated by <c>traceId</c>), never to the caller.
    /// </summary>
    internal const string ServerErrorDetail =
        "An unexpected error occurred. Quote the traceId when reporting it.";

    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (status, title) = exception switch
        {
            PlatformException px => (StatusCodes.Status400BadRequest, px.Code),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Bad Request"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
            OperationCanceledException => (499, "Client Closed Request"),
            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error"),
        };

        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        if (status == StatusCodes.Status500InternalServerError)
            LogUnhandledException(_logger, traceId, exception);
        else
            LogRequestError(_logger, title, traceId, exception);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = DetailFor(exception, status, title),
            Instance = context.Request.Path,
        };

        problem.Extensions["traceId"] = traceId;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, ApiJsonContext.Default.ProblemDetails));
    }

    /// <summary>
    /// The caller-facing detail. Only a <see cref="PlatformException"/> carries a message written for the
    /// caller (its code is the title). Every other exception's message — an
    /// <see cref="InvalidOperationException"/>, <see cref="ArgumentException"/> or
    /// <see cref="KeyNotFoundException"/> thrown anywhere below the endpoint can name tables, ids or
    /// internal state — goes to the log, correlated by <c>traceId</c>, and the caller gets the title.
    /// </summary>
    internal static string DetailFor(Exception exception, int status, string title) => exception switch
    {
        PlatformException => exception.Message,
        _ when status >= StatusCodes.Status500InternalServerError => ServerErrorDetail,
        _ => title,
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception (traceId {TraceId})")]
    private static partial void LogUnhandledException(ILogger logger, string traceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request error: {Title} (traceId {TraceId})")]
    private static partial void LogRequestError(ILogger logger, string title, string traceId, Exception exception);
}
