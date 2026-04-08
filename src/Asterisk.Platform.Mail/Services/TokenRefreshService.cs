using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Asterisk.Platform.Mail.Services;

internal sealed partial class TokenRefreshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromMinutes(10);

    private readonly TokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TokenRefreshService> _logger;

    public TokenRefreshService(
        TokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<TokenRefreshService> logger)
    {
        _tokenStore = tokenStore;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
            catch (Exception ex)
            {
                LogTokenRefreshFailed(token.TenantId, token.UserId, ex.Message);
            }
        }
    }

    private async Task RefreshTokenAsync(OAuthToken token, string clientId, string clientSecret, CancellationToken ct)
    {
        var azureTenantId = _configuration["Microsoft:TenantId"] ?? "common";
        var tokenEndpoint = $"https://login.microsoftonline.com/{azureTenantId}/oauth2/v2.0/token";

        using var client = _httpClientFactory.CreateClient("MicrosoftAuth");
        var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
            ["scope"] = token.Scopes,
        });

        var response = await client.PostAsync(tokenEndpoint, request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync(MailJsonContext.Default.TokenResponse, ct)
                     ?? throw new InvalidOperationException("Empty token response from Azure AD");

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh service started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh service stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshing {Count} expiring tokens")]
    private partial void LogRefreshingTokens(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refreshed for tenant={TenantId} user={UserId}")]
    private partial void LogTokenRefreshed(string tenantId, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token refresh failed for tenant={TenantId} user={UserId}: {Error}")]
    private partial void LogTokenRefreshFailed(string tenantId, string userId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Token refresh cycle error: {Error}")]
    private partial void LogRefreshCycleError(string error);
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
