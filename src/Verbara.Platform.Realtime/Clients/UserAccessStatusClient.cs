using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Realtime.Contracts;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Realtime.Clients;

/// <summary>Platform.Api's answer to "may this account connect right now?".</summary>
internal enum UserAccessVerdict
{
    /// <summary>The account may authenticate.</summary>
    Allowed,

    /// <summary>The account may not authenticate: it is not Active, or it no longer exists.</summary>
    Denied,

    /// <summary>
    /// No answer: the Api was unreachable, misconfigured (service key), too slow, or predates the
    /// endpoint. Only <see cref="Denied"/> is a decision.
    /// </summary>
    Unavailable,
}

/// <summary>Asks Platform.Api whether an account may authenticate.</summary>
internal interface IUserAccessStatusClient
{
    Task<UserAccessVerdict> CheckAsync(string tenantId, string userId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IUserAccessStatusClient"/> backed by Platform.Api's
/// <c>GET /api/v1/internal/user-access/{tenantId}/{userId}</c> (X-Service-Key gated). Deliberately
/// uncached: it runs once per hub connection, and a cached "allowed" would let a suspended user
/// reconnect for as long as the entry lives.
/// </summary>
internal sealed partial class UserAccessStatusClient : IUserAccessStatusClient
{
    private const string HttpClientName = "platform-api-internal";

    // A hung Api must not stall hub connects for the HttpClient's 100 s default.
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UserAccessStatusClient> _logger;

    public UserAccessStatusClient(IHttpClientFactory httpClientFactory, ILogger<UserAccessStatusClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<UserAccessVerdict> CheckAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(userId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(
                $"api/v1/internal/user-access/{Uri.EscapeDataString(tenantId)}/{Uri.EscapeDataString(userId)}",
                timeout.Token).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogUnavailable(_logger, tenantId, userId, $"HTTP {(int)response.StatusCode}");
                return UserAccessVerdict.Unavailable;
            }

            var decision = await response.Content
                .ReadFromJsonAsync(RealtimeContractsJsonContext.Default.UserAccessResponse, timeout.Token)
                .ConfigureAwait(false);

            // The Api always echoes the identifiers; a body without them is not a decision.
            if (decision is null || string.IsNullOrEmpty(decision.UserId))
            {
                LogUnavailable(_logger, tenantId, userId, "response carried no decision");
                return UserAccessVerdict.Unavailable;
            }

            return decision.Allowed ? UserAccessVerdict.Allowed : UserAccessVerdict.Denied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        {
            LogUnavailable(_logger, tenantId, userId, ex.Message);
            return UserAccessVerdict.Unavailable;
        }
    }

    [LoggerMessage(EventId = 7311, Level = LogLevel.Warning,
        Message = "[AUTHZ/USER-ACCESS] Account-status lookup unavailable for user={UserId} tenant={TenantId}: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string tenantId, string userId, string reason);
}
