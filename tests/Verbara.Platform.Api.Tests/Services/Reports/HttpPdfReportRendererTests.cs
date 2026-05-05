using System.Net;
using Verbara.Platform.Api.Services.Reports;
using Verbara.Platform.Core.Reports;
using Verbara.Sdk.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Services.Reports;

public sealed class HttpPdfReportRendererTests
{
    private static readonly byte[] SamplePdf = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // "%PDF"

    private static ReportData BuildReportData() => new()
    {
        ReportName = "test-report",
        TenantName = "tenant-1",
        ReportType = "agent-performance",
        From = DateTimeOffset.UtcNow.AddDays(-7),
        To = DateTimeOffset.UtcNow,
        GeneratedAt = DateTimeOffset.UtcNow,
    };

    private static (HttpPdfReportRenderer Renderer, CountingPdfHandler Handler) BuildSut(
        ResiliencePolicy? policy = null, int failFirstCount = 0, HttpStatusCode finalStatus = HttpStatusCode.OK)
    {
        var handler = new CountingPdfHandler(finalStatus, SamplePdf, failFirstCount);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://renderer.test.invalid"),
            });

        var sut = new HttpPdfReportRenderer(
            httpClientFactory,
            NullLogger<HttpPdfReportRenderer>.Instance,
            policy);

        return (sut, handler);
    }

    [Fact]
    public async Task RenderAsync_ShouldInvokeHttpSendOnce_WhenRenderSucceeds()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler) = BuildSut(policy);

        var bytes = await sut.RenderAsync(BuildReportData(), CancellationToken.None);

        handler.Attempts.Should().Be(1);
        bytes.Should().Equal(SamplePdf);
    }

    [Fact]
    public async Task RenderAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransient()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler) = BuildSut(policy, failFirstCount: 1);

        var bytes = await sut.RenderAsync(BuildReportData(), CancellationToken.None);

        handler.Attempts.Should().Be(2);
        bytes.Should().Equal(SamplePdf);
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        HttpPdfReportRenderer.ResiliencePolicyKey.Should().Be("report.pdf-render");
    }

    private sealed class CountingPdfHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;
        private readonly int _failFirstCount;
        private int _attempts;

        public CountingPdfHandler(HttpStatusCode status, byte[] body, int failFirstCount)
        {
            _status = status;
            _body = body;
            _failFirstCount = failFirstCount;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _attempts);
            if (n <= _failFirstCount)
                throw new HttpRequestException($"transient PDF-render failure (attempt {n})");

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body),
            });
        }
    }
}
