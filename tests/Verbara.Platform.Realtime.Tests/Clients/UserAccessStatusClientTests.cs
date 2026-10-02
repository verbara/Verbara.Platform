using System.Net;
using System.Text;
using Verbara.Platform.Realtime.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Realtime.Tests.Clients;

public sealed class UserAccessStatusClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAsync_ShouldReturnTheApisDecision_WhenTheLookupSucceeds(bool allowed)
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            $$"""{"TenantId":"acme","UserId":"alice","Allowed":{{(allowed ? "true" : "false")}}}""");
        var client = NewClient(handler);

        var verdict = await client.CheckAsync("acme", "alice", CancellationToken.None);

        verdict.Should().Be(allowed ? UserAccessVerdict.Allowed : UserAccessVerdict.Denied);
        handler.RequestedPath.Should().Be("/api/v1/internal/user-access/acme/alice");
    }

    [Fact]
    public async Task CheckAsync_ShouldEscapeTheIdentifiers_WhenBuildingThePath()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"TenantId":"a b","UserId":"x/y","Allowed":true}""");
        var client = NewClient(handler);

        await client.CheckAsync("a b", "x/y", CancellationToken.None);

        handler.RequestedPath.Should().Be("/api/v1/internal/user-access/a%20b/x%2Fy");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]            // an Api that predates the endpoint (rolling deploy)
    [InlineData(HttpStatusCode.Unauthorized)]        // service key mismatch
    [InlineData(HttpStatusCode.ServiceUnavailable)]  // service key not configured on the Api
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_ShouldReturnUnavailable_WhenTheApiDoesNotAnswerTheQuestion(HttpStatusCode status)
    {
        var client = NewClient(new StubHandler(status, ""));

        var verdict = await client.CheckAsync("acme", "alice", CancellationToken.None);

        verdict.Should().Be(UserAccessVerdict.Unavailable,
            because: "only a definite answer from the Api may refuse a connection");
    }

    [Fact]
    public async Task CheckAsync_ShouldReturnUnavailable_WhenTheApiIsUnreachable()
    {
        var client = NewClient(new StubHandler(new HttpRequestException("connection refused")));

        var verdict = await client.CheckAsync("acme", "alice", CancellationToken.None);

        verdict.Should().Be(UserAccessVerdict.Unavailable);
    }

    [Fact]
    public async Task CheckAsync_ShouldReturnUnavailable_WhenTheBodyIsNotADecision()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.OK, "<html>proxy error</html>"));

        var verdict = await client.CheckAsync("acme", "alice", CancellationToken.None);

        verdict.Should().Be(UserAccessVerdict.Unavailable);
    }

    private static UserAccessStatusClient NewClient(StubHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://platform-api:5000/"),
        });
        return new UserAccessStatusClient(factory, NullLogger<UserAccessStatusClient>.Instance);
    }

    /// <summary>Answers every request with one canned response (or failure); owns and disposes it.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body) =>
            _response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        public StubHandler(Exception failure) => _failure = failure;

        public string? RequestedPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPath = request.RequestUri!.AbsolutePath;
            if (_failure is not null)
                throw _failure;

            return Task.FromResult(_response!);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response?.Dispose();
            base.Dispose(disposing);
        }
    }
}
