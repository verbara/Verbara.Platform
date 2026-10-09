using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Api.Tests;

public class ErrorHandlingMiddlewareTests
{
    private static (ErrorHandlingMiddleware middleware, DefaultHttpContext context) CreateSut(
        Func<HttpContext, Task> next)
    {
        var middleware = new ErrorHandlingMiddleware(
            new RequestDelegate(next),
            NullLogger<ErrorHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return (middleware, context);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn400_WhenPlatformExceptionThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new PlatformException("INVALID_STATE", "Bad state"));
        await sut.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn400_WhenArgumentExceptionThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new ArgumentException("Bad arg"));
        await sut.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn499_WhenOperationCancelledThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new OperationCanceledException());
        await sut.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(499);
    }

    [Fact]
    public async Task InvokeAsync_ShouldIncludeTraceId_InResponse()
    {
        var (sut, ctx) = CreateSut(_ => throw new KeyNotFoundException("not found"));
        ctx.TraceIdentifier = "test-trace-123";
        await sut.InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.Should().Contain("test-trace-123");
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn500_WhenUnknownExceptionThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new NotSupportedException("Unexpected"));
        await sut.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(500);
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotEchoExceptionMessage_WhenUnhandledExceptionBecomes500()
    {
        const string marker = "MARKER-7f3c internal detail";
        var (sut, ctx) = CreateSut(_ => throw new NotSupportedException(marker));
        ctx.TraceIdentifier = "trace-500";

        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(500);
        var body = await ReadBodyAsync(ctx);
        body.Should().NotContain("MARKER-7f3c");
        body.Should().Contain("trace-500");
        body.Should().Contain(ErrorHandlingMiddleware.ServerErrorDetail);
    }

    [Fact]
    public async Task InvokeAsync_ShouldKeepExceptionMessageAsDetail_WhenMappedTo4xx()
    {
        // A PlatformException keeps its detail: it carries a caller-facing message (and its code as
        // the title). Other exception types' messages are scrubbed from 4xx bodies (v2.27.0).
        var (sut, ctx) = CreateSut(_ => throw new PlatformException("INVALID_STATE", "Bad state"));

        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(400);
        (await ReadBodyAsync(ctx)).Should().Contain("Bad state");
    }
    [Theory]
    [InlineData(nameof(InvalidOperationException), 400)]
    [InlineData(nameof(ArgumentException), 400)]
    [InlineData(nameof(ArgumentNullException), 400)]
    [InlineData(nameof(KeyNotFoundException), 404)]
    public async Task HandleException_ShouldNotEchoMessage_WhenInvalidOperationException(string exceptionType, int expectedStatus)
    {
        // The status stays; the message (which can name tables, ids or internal state) goes to the
        // log, and the caller gets the title plus a traceId to quote.
        const string marker = "internal detail X-4b1e";
        Exception exception = exceptionType switch
        {
            nameof(InvalidOperationException) => new InvalidOperationException(marker),
            nameof(ArgumentException) => new ArgumentException(marker),
            nameof(ArgumentNullException) => new ArgumentNullException(nameof(exceptionType), marker),
            _ => new KeyNotFoundException(marker),
        };
        var (sut, ctx) = CreateSut(_ => throw exception);
        ctx.TraceIdentifier = "trace-4xx";

        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(expectedStatus);
        var body = await ReadBodyAsync(ctx);
        body.Should().NotContain("X-4b1e");
        body.Should().Contain("trace-4xx");
    }

    [Fact]
    public async Task HandleException_ShouldKeepCode_WhenPlatformException()
    {
        var (sut, ctx) = CreateSut(_ => throw new PlatformException("some-code", "Caller-facing reason"));

        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(400);
        var body = await ReadBodyAsync(ctx);
        body.Should().Contain("\"title\":\"some-code\"");
        body.Should().Contain("Caller-facing reason");
    }
}
