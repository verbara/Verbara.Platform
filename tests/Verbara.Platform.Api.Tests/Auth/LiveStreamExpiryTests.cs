using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// An open SSE stream is authenticated once, when it opens, so it must not outlive the credential
/// that opened it: that expiry is the bound for every revocation the stream does not hear about
/// (a lost <c>user.access_revoked</c>, a stale cached status, sessions revoked for an account that
/// is still active, a revoked or rotated API key). The expiry runs on a clock the test moves;
/// JWT minting and validation stay on the real one, so a token is always valid to JwtBearer here.
/// </summary>
/// <remarks>
/// Each test gets its own host: moving one host's clock past a token's expiry would expire every
/// token the next test mints. A stream is shown open by an event it still delivers, never by a
/// read that times out (cancelling a TestServer read ends the client's side of the stream).
/// </remarks>
public sealed class LiveStreamExpiryTests : IDisposable
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly LiveStreamExpiryApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task StreamEvents_ShouldEndTheStream_WhenTheAccessTokenThatOpenedItExpires()
    {
        var token = _factory.MintAccessToken(NewActiveUser());
        var expiresAt = ExpiryOf(token);
        using var timeout = new CancellationTokenSource(GuardTimeout);
        using var stream = await OpenEventStreamAsync(Bearer(token), timeout.Token);

        _factory.Time.SetUtcNow(expiresAt - TimeSpan.FromSeconds(1));
        (await StillDeliversAsync(stream, timeout.Token)).Should().BeTrue(because: "the token is valid for one more second");

        _factory.Time.SetUtcNow(expiresAt);
        await stream.Ended.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task StreamEvents_ShouldRefuseToOpen_WhenTheAccessTokenHasAlreadyExpired()
    {
        // JwtBearer still accepts a token for its clock-skew grace past exp. Opened, the stream would
        // be ended at once, and the client's reconnect would loop through open-and-end until the
        // grace ran out; refused, the reconnect backs off like after any 401.
        var token = _factory.MintAccessToken(NewActiveUser());
        _factory.Time.SetUtcNow(ExpiryOf(token) + TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(GuardTimeout);

        using var response = await SendStreamRequestAsync(Bearer(token), timeout.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StreamEvents_ShouldEndAnApiKeyStreamWithinTheAccessTokenLifetime_AndLetTheKeyReopenIt()
    {
        // An API key carries no expiry of its own; it is re-checked on every request instead. A
        // stream is one request that does not end, so it gets an access token's lifetime: closing it
        // then makes the client reconnect, and the reconnect re-runs every check on the key.
        var rawKey = SeedApiKey(expiresAt: null);
        using var timeout = new CancellationTokenSource(GuardTimeout);
        using var stream = await OpenEventStreamAsync(ApiKey(rawKey), timeout.Token);

        _factory.Time.Advance(AccessTokenLifetime - TimeSpan.FromSeconds(1));
        (await StillDeliversAsync(stream, timeout.Token)).Should().BeTrue(because: "the key's stream has one more second");

        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await stream.Ended.WaitAsync(timeout.Token);

        using var reopened = await OpenEventStreamAsync(ApiKey(rawKey), timeout.Token);
        (await StillDeliversAsync(reopened, timeout.Token)).Should().BeTrue(
            because: "the close is not a refusal: a key that still authenticates gets a new stream");
    }

    [Fact]
    public async Task StreamEvents_ShouldEndAnApiKeyStream_WhenTheKeyItselfExpiresFirst()
    {
        var keyExpiry = _factory.Time.GetUtcNow() + TimeSpan.FromMinutes(5);
        var rawKey = SeedApiKey(expiresAt: keyExpiry);
        using var timeout = new CancellationTokenSource(GuardTimeout);
        using var stream = await OpenEventStreamAsync(ApiKey(rawKey), timeout.Token);

        _factory.Time.SetUtcNow(keyExpiry - TimeSpan.FromSeconds(1));
        (await StillDeliversAsync(stream, timeout.Token)).Should().BeTrue(because: "the key is valid for one more second");

        _factory.Time.SetUtcNow(keyExpiry);
        await stream.Ended.WaitAsync(timeout.Token);

        using var reopen = await SendStreamRequestAsync(ApiKey(rawKey), timeout.Token);
        reopen.StatusCode.Should().Be(HttpStatusCode.Unauthorized, because: "the key has expired");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private User NewActiveUser() =>
        _factory.SaveUser(
            $"expiring-stream-user-{Guid.NewGuid():N}", AccountStatusApiFactory.CustomerTenantId,
            UserRole.Agent, UserStatus.Active);

    /// <summary>Persists an owner-less tenant key and returns its raw value.</summary>
    private string SeedApiKey(DateTimeOffset? expiresAt)
    {
        var rawKey = $"vk_test_{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IApiKeyStore>().SaveAsync(new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = new TenantId(AccountStatusApiFactory.CustomerTenantId),
            Name = "live stream key",
            HashedKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
            Scopes = ["events:stream"],
            KeyType = ApiKeyType.Standard,
            ExpiresAt = expiresAt,
            CreatedAt = _factory.Time.GetUtcNow(),
        }, CancellationToken.None).GetAwaiter().GetResult();
        return rawKey;
    }

    private static DateTimeOffset ExpiryOf(string jwt) =>
        DateTimeOffset.FromUnixTimeSeconds(new JsonWebToken(jwt).GetPayloadValue<long>("exp"));

    private static AuthenticationHeaderValue Bearer(string jwt) => new("Bearer", jwt);

    private static AuthenticationHeaderValue ApiKey(string rawKey) => new("Bearer", rawKey);

    private async Task<HttpResponseMessage> SendStreamRequestAsync(AuthenticationHeaderValue credential, CancellationToken ct)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events/stream");
        request.Headers.Authorization = credential;
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<OpenStream> OpenEventStreamAsync(AuthenticationHeaderValue credential, CancellationToken ct)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events/stream");
        request.Headers.Authorization = credential;
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return new OpenStream(client, response, await response.Content.ReadAsStreamAsync(ct));
    }

    /// <summary>
    /// Whether the stream still delivers: publishes a tenant-wide event and waits for it to arrive.
    /// The stream subscribes to the bus just after it sends its headers, so an event published at
    /// once can precede the subscription; each attempt publishes a fresh probe, and any probe
    /// arriving proves the stream alive. <see langword="false"/> once the server has ended it.
    /// </summary>
    private async Task<bool> StillDeliversAsync(OpenStream stream, CancellationToken ct)
    {
        var bus = _factory.Services.GetRequiredService<PlatformEventBus>();
        var marker = $"probe-{Guid.NewGuid():N}";
        for (var attempt = 1; ; attempt++)
        {
            bus.Publish(new ConversationMessageEvent(
                AccountStatusApiFactory.CustomerTenantId, "probe-conversation", $"{marker}-{attempt}", "probe", "probe"));

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                return await stream.DeliversAsync(marker, window.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Not delivered within the window: publish another probe.
            }
        }
    }

    /// <summary>An open SSE response, read line by line in the background until the server ends it.</summary>
    private sealed class OpenStream : IDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpResponseMessage _response;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _stop = new();

        public OpenStream(HttpClient client, HttpResponseMessage response, Stream body)
        {
            _client = client;
            _response = response;
            Ended = ReadUntilEndAsync(body, _stop.Token);
        }

        /// <summary>Completes when the server ends the response.</summary>
        public Task Ended { get; }

        /// <summary>Waits for a line containing <paramref name="marker"/>; <see langword="false"/> if the stream ends first.</summary>
        public async Task<bool> DeliversAsync(string marker, CancellationToken ct)
        {
            while (await _lines.Reader.WaitToReadAsync(ct))
            {
                while (_lines.Reader.TryRead(out var line))
                {
                    if (line.Contains(marker, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _response.Dispose();
            _client.Dispose();
            _stop.Dispose();
        }

        private async Task ReadUntilEndAsync(Stream body, CancellationToken ct)
        {
            try
            {
                using var reader = new StreamReader(body);
                while (await reader.ReadLineAsync(ct) is { } line)
                    _lines.Writer.TryWrite(line);
            }
            catch (IOException)
            {
                // The server ended the response; TestServer may surface that as an aborted read.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Disposed by the test.
            }
            finally
            {
                _lines.Writer.TryComplete();
            }
        }
    }
}

/// <summary>
/// The account-status host on a clock the test moves (from a whole second, so expiries computed
/// from it land on whole seconds as JWT and stamped <c>exp</c> claims do). Only what resolves
/// <see cref="TimeProvider"/> from the container follows it — not JWT minting or validation.
/// </summary>
public sealed class LiveStreamExpiryApiFactory : AccountStatusApiFactory
{
    public FakeTimeProvider Time { get; } =
        new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(Time);
}
