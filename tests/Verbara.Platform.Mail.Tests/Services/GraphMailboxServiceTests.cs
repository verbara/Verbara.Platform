using System.Net;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Mail.Endpoints;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Mail.Tests.Services;

public sealed class GraphMailboxServiceTests
{
    private const string AccessToken = "test-token";

    // Minimal Graph v1.0 JSON payload for a Messages list response — enough for the service
    // to return zero messages without parsing individual entries (which exercises the code path
    // around the ResiliencePolicy wrap end-to-end).
    private const string EmptyMessagesResponse = """
        {"@odata.context":"https://graph.microsoft.com/v1.0/$metadata#users('me')/messages","@odata.count":0,"value":[]}
        """;

    // One-message Graph v1.0 payload carrying every field ListMessagesAsync selects. Characterizes the
    // JSON -> Message -> MailMessageDto mapping through the real Graph/Kiota deserialization stack.
    private const string OneMessageResponse = """
        {
          "@odata.context":"https://graph.microsoft.com/v1.0/$metadata#users('me')/messages(id,subject,from,toRecipients,receivedDateTime,isRead,bodyPreview,hasAttachments)",
          "@odata.count":42,
          "value":[
            {
              "@odata.etag":"W/\"CQAAABYAAAA\"",
              "id":"AAMkAGI2TG93AAA=",
              "subject":"Quarterly report",
              "from":{"emailAddress":{"name":"Ana Lopez","address":"ana@contoso.com"}},
              "toRecipients":[
                {"emailAddress":{"name":"Bob Ruiz","address":"bob@contoso.com"}},
                {"emailAddress":{"address":"team@contoso.com"}}
              ],
              "receivedDateTime":"2026-11-10T14:30:05Z",
              "isRead":false,
              "bodyPreview":"Hi team, attached is the report.",
              "hasAttachments":true
            }
          ]
        }
        """;

    private const string OneAttachmentResponse = """
        {
          "@odata.context":"https://graph.microsoft.com/v1.0/$metadata#users('me')/messages('m-1')/attachments(id,name,contentType,size)",
          "value":[
            {
              "@odata.type":"#microsoft.graph.fileAttachment",
              "id":"AAMkAtt1=",
              "name":"invoice.pdf",
              "contentType":"application/pdf",
              "size":12345
            }
          ]
        }
        """;

    // PATCH me/messages/{id} answers with the updated message.
    private const string PatchedMessageResponse = """
        {"@odata.context":"https://graph.microsoft.com/v1.0/$metadata#users('me')/messages/$entity","id":"m-1","isRead":true}
        """;

    private static (GraphMailboxService Service, CountingGraphHandler Handler) BuildSut(
        ResiliencePolicy? policy = null, int failFirstCount = 0, string body = EmptyMessagesResponse)
    {
        var handler = new CountingGraphHandler(body, failFirstCount);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("MicrosoftGraph")
            .Returns(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
            });

        var sut = new GraphMailboxService(
            httpClientFactory,
            NullLogger<GraphMailboxService>.Instance,
            policy);

        return (sut, handler);
    }

    [Fact]
    public async Task ListMessagesAsync_ShouldInvokeHttpSendOnce_WhenGraphReturnsSuccess()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler) = BuildSut(policy);

        var (messages, total) = await sut.ListMessagesAsync(
            AccessToken, unreadOnly: false, top: 10, skip: 0, CancellationToken.None);

        handler.Attempts.Should().Be(1);
        messages.Should().BeEmpty();
        total.Should().Be(0);
    }

    [Fact]
    public async Task ListMessagesAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransient()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler) = BuildSut(policy, failFirstCount: 1);

        var (_, total) = await sut.ListMessagesAsync(
            AccessToken, unreadOnly: false, top: 10, skip: 0, CancellationToken.None);

        handler.Attempts.Should().Be(2, "first attempt threw HttpRequestException; retry succeeded");
        total.Should().Be(0);
    }

    [Fact]
    public async Task ListMessagesAsync_ShouldMapEveryField_WhenGraphReturnsOneMessage()
    {
        var (sut, _) = BuildSut(body: OneMessageResponse);

        var (messages, total) = await sut.ListMessagesAsync(
            AccessToken, unreadOnly: false, top: 10, skip: 0, CancellationToken.None);

        total.Should().Be(42, "TotalCount comes from @odata.count, not from the page size");
        messages.Should().ContainSingle();
        var m = messages[0];
        m.Id.Should().Be("AAMkAGI2TG93AAA=");
        m.Subject.Should().Be("Quarterly report");
        m.From.Should().Be(new MailRecipientDto { Email = "ana@contoso.com", Name = "Ana Lopez" });
        m.To.Should().Equal(
            new MailRecipientDto { Email = "bob@contoso.com", Name = "Bob Ruiz" },
            new MailRecipientDto { Email = "team@contoso.com", Name = null });
        m.ReceivedAt.Should().Be(new DateTime(2026, 11, 10, 14, 30, 5, DateTimeKind.Utc));
        m.ReceivedAt.Kind.Should().Be(DateTimeKind.Utc);
        m.IsRead.Should().BeFalse();
        m.BodyPreview.Should().Be("Hi team, attached is the report.");
        m.HasAttachments.Should().BeTrue();
    }

    [Fact]
    public async Task ListMessagesAsync_ShouldSendConsistencyLevelAndQueryOptions_WhenUnreadOnly()
    {
        var (sut, handler) = BuildSut();

        await sut.ListMessagesAsync(
            AccessToken, unreadOnly: true, top: 25, skip: 50, CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.AbsolutePath.Should().Be("/v1.0/me/messages");
        request.Headers.Should().ContainKey("ConsistencyLevel")
            .WhoseValue.Should().Equal("eventual");
        request.Headers.Should().ContainKey("Authorization")
            .WhoseValue.Should().Equal($"Bearer {AccessToken}");

        var query = ParseQuery(request.Uri);
        query.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["$filter"] = "isRead eq false",
            ["$top"] = "25",
            ["$skip"] = "50",
            ["$count"] = "true",
            ["$orderby"] = "receivedDateTime desc",
            ["$select"] = "id,subject,from,toRecipients,receivedDateTime,isRead,bodyPreview,hasAttachments",
        });
    }

    [Fact]
    public async Task SendMessageAsync_ShouldPostHtmlBodyAndRecipients_WhenRequestHasToAndCc()
    {
        var (sut, handler) = BuildSut(body: string.Empty);

        await sut.SendMessageAsync(AccessToken, new SendMailRequest
        {
            To = [new MailRecipientDto { Email = "bob@contoso.com", Name = "Bob Ruiz" }],
            Cc = [new MailRecipientDto { Email = "carol@contoso.com" }],
            Subject = "Hello",
            Body = "<p>Hi Bob</p>",
        }, CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be("/v1.0/me/sendMail");
        request.ContentType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        // Characterized on Graph 5.105.0: the sendMail action parameters are written with the PascalCase
        // names from the Graph metadata ("Message", "SaveToSentItems"), not camelCase.
        root.EnumerateObject().Select(p => p.Name).Should().Equal("Message", "SaveToSentItems");
        root.GetProperty("SaveToSentItems").GetBoolean().Should().BeTrue();
        var message = root.GetProperty("Message");
        message.GetProperty("subject").GetString().Should().Be("Hello");
        message.GetProperty("body").GetProperty("contentType").GetString().Should().Be("html");
        message.GetProperty("body").GetProperty("content").GetString().Should().Be("<p>Hi Bob</p>");

        var to = message.GetProperty("toRecipients").EnumerateArray()
            .Select(r => r.GetProperty("emailAddress")).ToList();
        to.Should().ContainSingle();
        to[0].GetProperty("address").GetString().Should().Be("bob@contoso.com");
        to[0].GetProperty("name").GetString().Should().Be("Bob Ruiz");

        var cc = message.GetProperty("ccRecipients").EnumerateArray()
            .Select(r => r.GetProperty("emailAddress")).ToList();
        cc.Should().ContainSingle();
        cc[0].GetProperty("address").GetString().Should().Be("carol@contoso.com");
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldPatchIsReadTrue_WhenCalled()
    {
        var (sut, handler) = BuildSut(body: PatchedMessageResponse);

        await sut.MarkAsReadAsync(AccessToken, "m-1", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Patch);
        request.Uri.AbsolutePath.Should().Be("/v1.0/me/messages/m-1");

        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        root.GetProperty("isRead").GetBoolean().Should().BeTrue();
        // Characterized on Graph 5.105.0: the generated Message constructor also writes its OData type.
        // Nothing else may ride along in the PATCH, or Graph would overwrite those fields.
        root.GetProperty("@odata.type").GetString().Should().Be("#microsoft.graph.message");
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("@odata.type", "isRead");
    }

    [Fact]
    public async Task ReplyMessageAsync_ShouldPostComment_WhenCalled()
    {
        var (sut, handler) = BuildSut(body: string.Empty);

        await sut.ReplyMessageAsync(AccessToken, "m-1", new ReplyMailRequest("Thanks, received."), CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be("/v1.0/me/messages/m-1/reply");
        request.ContentType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        // Characterized on Graph 5.105.0: like sendMail, the action parameters are written with the
        // PascalCase names from the Graph metadata.
        root.EnumerateObject().Select(p => p.Name).Should().Equal("Comment");
        root.GetProperty("Comment").GetString().Should().Be("Thanks, received.");
    }

    [Fact]
    public async Task ForwardMessageAsync_ShouldPostCommentAndRecipients_WhenCalled()
    {
        var (sut, handler) = BuildSut(body: string.Empty);

        await sut.ForwardMessageAsync(AccessToken, "m-1", new ForwardMailRequest(
            "FYI",
            [new MailRecipientDto { Email = "dave@contoso.com", Name = "Dave Kim" }]), CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be("/v1.0/me/messages/m-1/forward");
        request.ContentType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        // Characterized on Graph 5.105.0: like sendMail, the action parameters are written with the
        // PascalCase names from the Graph metadata; the recipients inside keep camelCase.
        root.EnumerateObject().Select(p => p.Name).Should().Equal("Comment", "ToRecipients");
        root.GetProperty("Comment").GetString().Should().Be("FYI");
        var to = root.GetProperty("ToRecipients").EnumerateArray()
            .Select(r => r.GetProperty("emailAddress")).ToList();
        to.Should().ContainSingle();
        to[0].GetProperty("address").GetString().Should().Be("dave@contoso.com");
        to[0].GetProperty("name").GetString().Should().Be("Dave Kim");
    }

    [Fact]
    public async Task DeleteMessageAsync_ShouldSendDeleteToMessagePath_WhenCalled()
    {
        var (sut, handler) = BuildSut(body: string.Empty);

        await sut.DeleteMessageAsync(AccessToken, "m-1", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        request.Uri.AbsolutePath.Should().Be("/v1.0/me/messages/m-1");
        request.Body.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task GetAttachmentsAsync_ShouldMapNameTypeAndSize_WhenGraphReturnsOneAttachment()
    {
        var (sut, handler) = BuildSut(body: OneAttachmentResponse);

        var attachments = await sut.GetAttachmentsAsync(AccessToken, "m-1", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.AbsolutePath.Should().Be("/v1.0/me/messages/m-1/attachments");
        ParseQuery(request.Uri).Should().Contain("$select", "id,name,contentType,size");

        attachments.Should().ContainSingle().Which.Should().Be(new MailAttachmentDto
        {
            Id = "AAMkAtt1=",
            Name = "invoice.pdf",
            ContentType = "application/pdf",
            Size = 12345,
        });
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        GraphMailboxService.ResiliencePolicyKey.Should().Be("mail.graph");
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                kv => Uri.UnescapeDataString(kv[0]),
                kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty);

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string? ContentType,
        string? Body);

    /// <summary>
    /// Handler that optionally throws transient failures on the first N attempts, then returns
    /// a static successful Graph JSON response. Used to assert that the ResiliencePolicy retry
    /// contract wraps the underlying HttpClient used by the Graph SDK.
    /// </summary>
    private sealed class CountingGraphHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly int _failFirstCount;
        private readonly List<CapturedRequest> _requests = [];
        private int _attempts;

        public CountingGraphHandler(string body, int failFirstCount)
        {
            _body = body;
            _failFirstCount = failFirstCount;
        }

        public int Attempts => _attempts;

        /// <summary>Every request that reached the handler, body read before the SDK disposes it.</summary>
        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_requests)
                    return [.. _requests];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _attempts);
            if (n <= _failFirstCount)
                throw new HttpRequestException($"transient Graph failure (attempt {n})");

            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            lock (_requests)
            {
                _requests.Add(new CapturedRequest(
                    request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.MediaType, body));
            }

            if (_body.Length == 0)
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
