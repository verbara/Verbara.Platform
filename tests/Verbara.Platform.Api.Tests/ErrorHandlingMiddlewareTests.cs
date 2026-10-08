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
        // 4xx keeps its detail: domain code throws ArgumentException / PlatformException with a
        // caller-facing message, and console clients render it.
        var (sut, ctx) = CreateSut(_ => throw new PlatformException("INVALID_STATE", "Bad state"));

        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(400);
        (await ReadBodyAsync(ctx)).Should().Contain("Bad state");
    }
}
