using Asterisk.Platform.Mail.Endpoints;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Me.Messages;
using Microsoft.Graph.Me.SendMail;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Asterisk.Platform.Mail.Services;

internal sealed partial class GraphMailboxService
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps each Graph mailbox
    /// operation. Registered in Program.cs with circuit 5/60s + retry 2/500ms + timeout 20s.
    /// </summary>
    public const string ResiliencePolicyKey = "mail.graph";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GraphMailboxService> _logger;
    private readonly ResiliencePolicy _policy;

    public GraphMailboxService(
        IHttpClientFactory httpClientFactory,
        ILogger<GraphMailboxService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public async Task<(IReadOnlyList<MailMessageDto> Messages, int TotalCount)> ListMessagesAsync(
        string accessToken, bool unreadOnly, int top, int skip, CancellationToken ct)
    {
        var client = CreateClient(accessToken);

        var response = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt => await client.Me.Messages.GetAsync(config =>
            {
                config.QueryParameters.Top = top;
                config.QueryParameters.Skip = skip;
                config.QueryParameters.Orderby = ["receivedDateTime desc"];
                config.QueryParameters.Select = ["id", "subject", "from", "toRecipients", "receivedDateTime", "isRead", "bodyPreview", "hasAttachments"];
                config.QueryParameters.Count = true;
                if (unreadOnly)
                    config.QueryParameters.Filter = "isRead eq false";
                config.Headers.Add("ConsistencyLevel", "eventual");
            }, innerCt).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        var messages = response?.Value?.Select(MapMessage).ToList() ?? [];
        var total = (int)(response?.OdataCount ?? messages.Count);

        LogListMessages(messages.Count, total);
        return (messages, total);
    }

    public async Task SendMessageAsync(string accessToken, SendMailRequest request, CancellationToken ct)
    {
        var client = CreateClient(accessToken);

        var message = new Microsoft.Graph.Models.Message
        {
            Subject = request.Subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = request.Body },
            ToRecipients = request.To.Select(r => new Recipient
            {
                EmailAddress = new EmailAddress { Address = r.Email, Name = r.Name },
            }).ToList(),
        };

        if (request.Cc is { Count: > 0 })
        {
            message.CcRecipients = request.Cc.Select(r => new Recipient
            {
                EmailAddress = new EmailAddress { Address = r.Email, Name = r.Name },
            }).ToList();
        }

        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                await client.Me.SendMail.PostAsync(new SendMailPostRequestBody
                {
                    Message = message,
                    SaveToSentItems = true,
                }, cancellationToken: innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);

        LogMessageSent(request.Subject ?? "(no subject)");
    }

    public async Task ReplyMessageAsync(string accessToken, string messageId, ReplyMailRequest request, CancellationToken ct)
    {
        var client = CreateClient(accessToken);

        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                await client.Me.Messages[messageId].Reply.PostAsync(
                    new Microsoft.Graph.Me.Messages.Item.Reply.ReplyPostRequestBody
                    {
                        Comment = request.Comment,
                    }, cancellationToken: innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);

        LogMessageReplied(messageId);
    }

    public async Task ForwardMessageAsync(string accessToken, string messageId, ForwardMailRequest request, CancellationToken ct)
    {
        var client = CreateClient(accessToken);

        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                await client.Me.Messages[messageId].Forward.PostAsync(
                    new Microsoft.Graph.Me.Messages.Item.Forward.ForwardPostRequestBody
                    {
                        Comment = request.Comment,
                        ToRecipients = request.To.Select(r => new Recipient
                        {
                            EmailAddress = new EmailAddress { Address = r.Email, Name = r.Name },
                        }).ToList(),
                    }, cancellationToken: innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);

        LogMessageForwarded(messageId);
    }

    public async Task DeleteMessageAsync(string accessToken, string messageId, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                await client.Me.Messages[messageId].DeleteAsync(cancellationToken: innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);
        LogMessageDeleted(messageId);
    }

    public async Task MarkAsReadAsync(string accessToken, string messageId, CancellationToken ct)
    {
        var client = CreateClient(accessToken);
        await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                await client.Me.Messages[messageId].PatchAsync(new Microsoft.Graph.Models.Message
                {
                    IsRead = true,
                }, cancellationToken: innerCt).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);

        LogMessageMarkedRead(messageId);
    }

    public async Task<IReadOnlyList<MailAttachmentDto>> GetAttachmentsAsync(string accessToken, string messageId, CancellationToken ct)
    {
        var client = CreateClient(accessToken);

        var response = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt => await client.Me.Messages[messageId].Attachments.GetAsync(config =>
            {
                config.QueryParameters.Select = ["id", "name", "contentType", "size"];
            }, innerCt).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        var attachments = response?.Value?.Select(a => new MailAttachmentDto
        {
            Id = a.Id ?? string.Empty,
            Name = a.Name ?? string.Empty,
            ContentType = a.ContentType ?? "application/octet-stream",
            Size = a.Size ?? 0,
        }).ToList() ?? [];

        LogAttachmentsListed(messageId, attachments.Count);
        return attachments;
    }

    private GraphServiceClient CreateClient(string accessToken)
    {
        var authProvider = new BaseBearerTokenAuthenticationProvider(
            new TokenProvider(accessToken));

        var httpClient = _httpClientFactory.CreateClient("MicrosoftGraph");
        return new GraphServiceClient(httpClient, authProvider);
    }

    private static MailMessageDto MapMessage(Microsoft.Graph.Models.Message m) => new()
    {
        Id = m.Id ?? string.Empty,
        Subject = m.Subject ?? string.Empty,
        From = m.From?.EmailAddress is { } ea ? new MailRecipientDto { Email = ea.Address ?? string.Empty, Name = ea.Name } : null,
        To = m.ToRecipients?.Select(r => new MailRecipientDto
        {
            Email = r.EmailAddress?.Address ?? string.Empty,
            Name = r.EmailAddress?.Name,
        }).ToList() ?? [],
        ReceivedAt = m.ReceivedDateTime?.UtcDateTime ?? DateTime.MinValue,
        IsRead = m.IsRead ?? false,
        BodyPreview = m.BodyPreview ?? string.Empty,
        HasAttachments = m.HasAttachments ?? false,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Listed {Count}/{Total} messages from Graph")]
    private partial void LogListMessages(int count, int total);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message sent via Graph: subject={Subject}")]
    private partial void LogMessageSent(string subject);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reply sent for message {MessageId}")]
    private partial void LogMessageReplied(string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} forwarded")]
    private partial void LogMessageForwarded(string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} deleted")]
    private partial void LogMessageDeleted(string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} marked as read")]
    private partial void LogMessageMarkedRead(string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listed {Count} attachments for message {MessageId}")]
    private partial void LogAttachmentsListed(string messageId, int count);
}

internal sealed class TokenProvider : IAccessTokenProvider
{
    private readonly string _accessToken;

    public TokenProvider(string accessToken)
    {
        _accessToken = accessToken;
    }

    public Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_accessToken);
    }

    public AllowedHostsValidator AllowedHostsValidator { get; } = new();
}
