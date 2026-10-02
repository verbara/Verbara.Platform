using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// An open SSE stream is a live connection like a Realtime hub connection: it is registered under
/// its owner when it opens and ends when that owner's connections are aborted (which the
/// revocation listener does on <c>user.access_revoked</c>, from this replica or another).
/// </summary>
public sealed class AccountStatusLiveStreamTests : IClassFixture<AccountStatusApiFactory>
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(15);

    private readonly AccountStatusApiFactory _factory;

    public AccountStatusLiveStreamTests(AccountStatusApiFactory factory) => _factory = factory;

    [Fact]
    public async Task StreamEvents_ShouldEndTheOpenStream_WhenTheOwnersConnectionsAreAborted()
    {
        var user = NewActiveUser();
        using var timeout = new CancellationTokenSource(GuardTimeout);
        using var stream = await OpenEventStreamAsync(user, timeout.Token);

        // No wait needed: the handler registers the stream before it flushes the response headers,
        // so having received the 200 is itself the proof the registration exists.
        var aborted = _factory.Services.GetRequiredService<LiveConnectionRegistry>()
            .AbortAll(user.TenantId.Value, user.UserId.Value);

        aborted.Should().Be(1, because: "the open stream is registered under its owner");
        (await ReadToEndAsync(stream.Body, timeout.Token)).Should().BeTrue(
            because: "aborting the owner's connections must end the stream, not leave it streaming");
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Deactivated")]
    public async Task UpdateUser_ShouldEndTheUsersOpenStream_WhenStatusMovesAwayFromActive(string newStatus)
    {
        // The whole chain the host wires, nothing called by hand: the admin's status change publishes
        // user.access_revoked, the revocation listener Program.cs registers receives it from the push
        // bus, and the stream's registration is aborted.
        var user = NewActiveUser();
        using var timeout = new CancellationTokenSource(GuardTimeout);
        using var stream = await OpenEventStreamAsync(user, timeout.Token);

        using var admin = CustomerAdminClient();
        (await admin.PutAsJsonAsync($"/api/v1/admin/users/{user.UserId.Value}", new { status = newStatus }, timeout.Token))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadToEndAsync(stream.Body, timeout.Token)).Should().BeTrue(
            because: "a suspended user's open event stream must end, not keep streaming tenant events");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private User NewActiveUser() =>
        _factory.SaveUser(
            $"live-stream-user-{Guid.NewGuid():N}", AccountStatusApiFactory.CustomerTenantId,
            UserRole.Agent, UserStatus.Active);

    private HttpClient CustomerAdminClient()
    {
        var admin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private async Task<OpenStream> OpenEventStreamAsync(User user, CancellationToken ct)
    {
        var client = _factory.CreateBearerClient(_factory.MintAccessToken(user));
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events/stream");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStreamAsync(ct);
        return new OpenStream(client, request, response, body);
    }

    /// <returns><see langword="true"/> once the server ends the response; <see langword="false"/> if it is still open when <paramref name="ct"/> fires.</returns>
    private static async Task<bool> ReadToEndAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            int read;
            do
            {
                read = await body.ReadAsync(buffer, ct);
            }
            while (read > 0);

            return true;
        }
        catch (IOException)
        {
            // The server ended the response; TestServer may surface that as an aborted read.
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class OpenStream(HttpClient client, HttpRequestMessage request, HttpResponseMessage response, Stream body)
        : IDisposable
    {
        public Stream Body { get; } = body;

        public void Dispose()
        {
            Body.Dispose();
            response.Dispose();
            request.Dispose();
            client.Dispose();
        }
    }
}
