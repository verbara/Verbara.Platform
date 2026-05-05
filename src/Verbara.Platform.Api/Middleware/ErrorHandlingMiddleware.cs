using System.Diagnostics;
using System.Text.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Middleware;

internal sealed partial class ErrorHandlingMiddleware
{
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

        if (status == StatusCodes.Status500InternalServerError)
            LogUnhandledException(_logger, exception);
        else
            LogRequestError(_logger, title, exception);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message,
            Instance = context.Request.Path,
        };

        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, ApiJsonContext.Default.ProblemDetails));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request error: {Title}")]
    private static partial void LogRequestError(ILogger logger, string title, Exception exception);
}
