using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Mail.Services;

internal sealed partial class TokenRefreshService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps the Azure AD
    /// token-endpoint POST. Registered in Program.cs with circuit 3/120s + retry 3/1s + timeout 15s.
    /// Separate from <see cref="GraphMailboxService.ResiliencePolicyKey"/> because refresh failure
    /// modes (clock skew, tenant config drift) differ from Graph API transient blips.
    /// </summary>
    public const string ResiliencePolicyKey = "mail.token-refresh";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromMinutes(10);

    private readonly TokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TokenRefreshService> _logger;
    private readonly ResiliencePolicy _policy;

    public TokenRefreshService(
        TokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<TokenRefreshService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _tokenStore = tokenStore;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            LogStarted();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshExpiringTokensAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogRefreshCycleError(ex.Message);
                }

                await Task.Delay(Interval, stoppingToken);
            }

            LogStopped();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(TokenRefreshService), fatalEx.Message, fatalEx);
            throw;
        }
    }

    private async Task RefreshExpiringTokensAsync(CancellationToken ct)
    {
        var tokens = await _tokenStore.GetExpiringAsync(ExpiryWindow, ct);
        if (tokens.Count == 0)
            return;

        LogRefreshingTokens(tokens.Count);

        var clientId = _configuration["Microsoft:ClientId"] ?? string.Empty;
        var clientSecret = _configuration["Microsoft:ClientSecret"] ?? string.Empty;

        foreach (var token in tokens)
        {
            try
            {
                await RefreshTokenAsync(token, clientId, clientSecret, ct);
                LogTokenRefreshed(token.TenantId, token.UserId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Previously this catch silently swallowed the exception with an info-level log.
                // After the resilience wrap, policy has already exhausted its retries; emit a
                // structured Warning so operators can see persistent failures in production.
                LogTokenRefreshExhausted(token.TenantId, token.UserId, ex.GetType().Name, ex.Message);
            }
        }
    }

    private async Task RefreshTokenAsync(OAuthToken token, string clientId, string clientSecret, CancellationToken ct)
    {
        var azureTenantId = _configuration["Microsoft:TenantId"] ?? "common";
        var tokenEndpoint = $"https://login.microsoftonline.com/{azureTenantId}/oauth2/v2.0/token";

        var result = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                using var client = _httpClientFactory.CreateClient("MicrosoftAuth");
                var request = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken,
                    ["scope"] = token.Scopes,
                });

                using var response = await client.PostAsync(tokenEndpoint, request, innerCt).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadFromJsonAsync(MailJsonContext.Default.TokenResponse, innerCt).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Empty token response from Azure AD");
            },
            ct).ConfigureAwait(false);

        var refreshed = new OAuthToken
        {
            Id = token.Id,
            TenantId = token.TenantId,
            UserId = token.UserId,
            Provider = token.Provider,
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken ?? token.RefreshToken,
            ExpiresAt = DateTime.UtcNow.AddSeconds(result.ExpiresIn),
            Scopes = token.Scopes,
            CreatedAt = token.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
        };

        await _tokenStore.UpsertAsync(refreshed, ct);
    }

    /// <summary>
    /// Test-only entry point that runs a single refresh cycle without spawning the
    /// BackgroundService loop. Exposed via <c>InternalsVisibleTo</c> so tests can assert
    /// the policy wrap (retries + exhaustion warning) without starting the hosted service.
    /// </summary>
    internal Task RefreshExpiringTokensForTestAsync(CancellationToken ct)
        => RefreshExpiringTokensAsync(ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh service started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh service stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshing {Count} expiring tokens")]
    private partial void LogRefreshingTokens(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refreshed for tenant={TenantId} user={UserId}")]
    private partial void LogTokenRefreshed(string tenantId, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token refresh exhausted retries for tenant={TenantId} user={UserId}: {ExceptionType}: {Error}")]
    private partial void LogTokenRefreshExhausted(string tenantId, string userId, string exceptionType, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Token refresh cycle error: {Error}")]
    private partial void LogRefreshCycleError(string error);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = string.Empty;
}
