using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Mail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Mail.Endpoints;

internal static class MicrosoftAuthEndpoints
{
    private const string StateCookieName = ".Mail.OAuthState";
    private const int StateCookieTtlMinutes = 5;

    public static RouteGroupBuilder MapMicrosoftAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/microsoft/signin", (
            HttpContext context,
            [FromQuery] string tenantId,
            [FromQuery] string userId,
            [FromQuery] string returnUrl,
            [FromServices] IConfiguration config,
            [FromServices] IDataProtectionProvider dpProvider) =>
        {
            var clientId = config["Microsoft:ClientId"] ?? throw new InvalidOperationException("Microsoft:ClientId not configured");
            var azureTenantId = config["Microsoft:TenantId"] ?? "common";
            var redirectUri = config["Microsoft:RedirectUri"] ?? $"{context.Request.Scheme}://{context.Request.Host}/auth/microsoft/callback";

            // Generate PKCE
            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = GenerateCodeChallenge(codeVerifier);

            // Encrypt state
            var protector = dpProvider.CreateProtector("OAuthState");
            var statePayload = JsonSerializer.Serialize(new OAuthStatePayload(tenantId, userId, returnUrl, codeVerifier),
                MailJsonContext.Default.OAuthStatePayload);
            var encryptedState = protector.Protect(statePayload);

            // Set state cookie
            context.Response.Cookies.Append(StateCookieName, encryptedState, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(StateCookieTtlMinutes),
                Path = "/auth/microsoft",
            });

            var scope = Uri.EscapeDataString("offline_access Mail.ReadWrite Mail.Send");
            var authUrl = $"https://login.microsoftonline.com/{azureTenantId}/oauth2/v2.0/authorize" +
                          $"?client_id={clientId}" +
                          $"&response_type=code" +
                          $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                          $"&scope={scope}" +
                          $"&code_challenge={codeChallenge}" +
                          $"&code_challenge_method=S256" +
                          $"&state={Uri.EscapeDataString(encryptedState)}";

            return Results.Redirect(authUrl);
        })
        .WithName("MicrosoftSignIn")
        .WithTags("MicrosoftAuth")
        .AllowAnonymous();

        group.MapGet("/microsoft/callback", async (
            HttpContext context,
            [FromQuery] string code,
            [FromQuery] string state,
            [FromServices] IConfiguration config,
            [FromServices] IDataProtectionProvider dpProvider,
            [FromServices] TokenStore tokenStore,
            [FromServices] IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            // Validate state cookie
            if (!context.Request.Cookies.TryGetValue(StateCookieName, out var cookieState) || cookieState != state)
                return Results.BadRequest(new { error = "Invalid or missing state" });

            // Decrypt state
            var protector = dpProvider.CreateProtector("OAuthState");
            var stateJson = protector.Unprotect(state);
            var payload = JsonSerializer.Deserialize(stateJson, MailJsonContext.Default.OAuthStatePayload)
                          ?? throw new InvalidOperationException("Failed to deserialize OAuth state");

            // Exchange code for tokens
            var clientId = config["Microsoft:ClientId"]!;
            var clientSecret = config["Microsoft:ClientSecret"]!;
            var azureTenantId = config["Microsoft:TenantId"] ?? "common";
            var redirectUri = config["Microsoft:RedirectUri"] ?? $"{context.Request.Scheme}://{context.Request.Host}/auth/microsoft/callback";

            var tokenEndpoint = $"https://login.microsoftonline.com/{azureTenantId}/oauth2/v2.0/token";

            using var client = httpClientFactory.CreateClient("MicrosoftAuth");
            var request = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = payload.CodeVerifier,
            });

            var response = await client.PostAsync(tokenEndpoint, request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                return Results.BadRequest(new { error = "Token exchange failed", details = error });
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync(MailJsonContext.Default.TokenResponse, ct)
                                ?? throw new InvalidOperationException("Empty token response");

            var token = new OAuthToken
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = payload.TenantId,
                UserId = payload.UserId,
                Provider = "microsoft",
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken ?? string.Empty,
                ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
                Scopes = "offline_access Mail.ReadWrite Mail.Send",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            await tokenStore.UpsertAsync(token, ct);

            // Delete state cookie
            context.Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = "/auth/microsoft" });

            return Results.Redirect(payload.ReturnUrl);
        })
        .WithName("MicrosoftCallback")
        .WithTags("MicrosoftAuth")
        .AllowAnonymous();

        group.MapPost("/microsoft/signout", async (
            HttpContext context,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var tenantId = context.User.FindFirst("tid")?.Value;
            var userId = context.User.FindFirst("sub")?.Value;

            if (tenantId is null || userId is null)
                return Results.Unauthorized();

            await tokenStore.DeleteAsync(tenantId, userId, "microsoft", ct);
            return Results.NoContent();
        })
        .WithName("MicrosoftSignOut")
        .WithTags("MicrosoftAuth")
        .RequireAuthorization();

        return group;
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

internal sealed record OAuthStatePayload(
    string TenantId,
    string UserId,
    string ReturnUrl,
    string CodeVerifier);
