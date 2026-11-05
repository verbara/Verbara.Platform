using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The WebChat identity token secret on the existing channel configuration route, one test per case of
/// <c>Conversations/Fixtures/webchat-identity-secret-admin.v1.json</c>. Every test writes the tenant's WebChat row first.
/// </summary>
public sealed class WebChatChannelConfigEndpointsTests :
    IClassFixture<AuthenticatedPlatformApiFactory>,
    IClassFixture<ConversationOwnershipApiFactory>
{
    private const string Current = "identityTokenSecret";
    private const string Previous = "identityTokenSecretPrevious";
    private const string ReplacedAt = "identityTokenSecretReplacedAt";
    private const string QueueKey = "defaultQueueId";
    private const string OldStamp = "2026-11-04T08:00:00.000+00:00";

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly ConversationOwnershipApiFactory _ownership;
    private readonly HttpClient _client;

    public WebChatChannelConfigEndpointsTests(AuthenticatedPlatformApiFactory factory, ConversationOwnershipApiFactory ownership)
    {
        _factory = factory;
        _ownership = ownership;
        _client = factory.CreateAuthenticatedClient();
    }

    private static TenantId Tenant => new(AuthenticatedPlatformApiFactory.TestTenantId);

    private static JsonObject Fixture()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "webchat-identity-secret-admin.v1.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    private static string SecretOfSet => Fixture()["set"]!["request"]!["body"]!["credentials"]![Current]!.GetValue<string>();

    private static string SecretOfRotate => Fixture()["rotate"]!["request"]!["body"]!["credentials"]![Current]!.GetValue<string>();

    private async Task SeedAsync(Dictionary<string, string> credentials)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
            new TenantChannelConfig { TenantId = Tenant, Channel = ChannelType.WebChat, IsActive = true, Credentials = credentials },
            CancellationToken.None);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var config = await scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>()
            .GetAsync(Tenant, ChannelType.WebChat, CancellationToken.None);
        return config!.Credentials;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(JsonNode request)
    {
        var uri = new Uri(request["path"]!.GetValue<string>(), UriKind.Relative);
        using var message = new HttpRequestMessage(new HttpMethod(request["method"]!.GetValue<string>()), uri);
        if (request["body"] is { } body)
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task AssertLastChangedKeysAsync(string expected, params string[] neverRecorded)
    {
        using var scope = _factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            Tenant, new AuditQuery(Action: "channel.configured", Page: 1, PageSize: 500), CancellationToken.None);
        var last = audit.Items.Where(e => e.TargetId == "WebChat").MaxBy(e => e.OccurredAt);
        last.Should().NotBeNull();
        last!.Metadata!.GetValueOrDefault("changed_keys").Should().Be(expected);
        var recorded = string.Join('\n', audit.Items.Select(e =>
            $"{e.Changes?.Before} {e.Changes?.After} " +
            string.Join(';', (e.Metadata ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}={kv.Value}"))));
        foreach (var secret in neverRecorded)
            recorded.Should().NotContain(secret);
    }

    private static void AssertBodyMatchesFixture(string body, JsonNode? expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(body), expected).Should().BeTrue($"it was {body}");

    [Fact]
    public async Task Put_ShouldSetFirstSecret_WhenTenantHasNone()
    {
        await SeedAsync(new() { [QueueKey] = "q1" });
        var set = Fixture()["set"]!;

        var (status, body) = await SendAsync(set["request"]!);

        status.Should().Be((HttpStatusCode)set["status"]!.GetValue<int>(), body);
        AssertBodyMatchesFixture(body, set["body"]);
        (await LoadAsync())[Current].Should().Be(SecretOfSet);
        await AssertLastChangedKeysAsync(set["audit"]!["metadata.changed_keys"]!.GetValue<string>(), SecretOfSet);
    }

    [Fact]
    public async Task Put_ShouldRotate_WhenSecretIsReplaced()
    {
        await SeedAsync(new() { [QueueKey] = "q1", [Current] = SecretOfSet });
        var rotate = Fixture()["rotate"]!;
        var before = DateTimeOffset.UtcNow;

        var (status, body) = await SendAsync(rotate["request"]!);

        status.Should().Be((HttpStatusCode)rotate["status"]!.GetValue<int>(), body);
        AssertBodyMatchesFixture(body, rotate["body"]);
        var stored = await LoadAsync();
        stored[Current].Should().Be(SecretOfRotate);
        stored[Previous].Should().Be(SecretOfSet);
        DateTimeOffset.Parse(stored[ReplacedAt], CultureInfo.InvariantCulture)
            .Should().BeOnOrAfter(before.AddMilliseconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        await AssertLastChangedKeysAsync(rotate["audit"]!["metadata.changed_keys"]!.GetValue<string>(), SecretOfSet, SecretOfRotate);
    }

    [Fact]
    public async Task Put_ShouldMoveNothing_WhenSecretIsUnchanged()
    {
        await SeedAsync(new() { [QueueKey] = "q1", [Current] = SecretOfRotate });

        var (status, body) = await SendAsync(Fixture()["rotate"]!["request"]!);

        status.Should().Be(HttpStatusCode.OK, body);
        var stored = await LoadAsync();
        stored.Should().NotContainKey(Previous).And.NotContainKey(ReplacedAt);
        await AssertLastChangedKeysAsync(string.Empty, SecretOfRotate);
    }

    [Fact]
    public async Task Put_ShouldRevokePrevious_WhenPreviousIsNull()
    {
        await SeedAsync(new() { [QueueKey] = "q1", [Current] = SecretOfRotate, [Previous] = SecretOfSet, [ReplacedAt] = OldStamp });
        var revoke = Fixture()["revokePrevious"]!;

        var (status, body) = await SendAsync(revoke["request"]!);

        status.Should().Be((HttpStatusCode)revoke["status"]!.GetValue<int>(), body);
        AssertBodyMatchesFixture(body, revoke["body"]);
        var stored = await LoadAsync();
        stored.Should().NotContainKey(Previous).And.NotContainKey(ReplacedAt);
        stored[Current].Should().Be(SecretOfRotate);
        await AssertLastChangedKeysAsync(revoke["audit"]!["metadata.changed_keys"]!.GetValue<string>(), SecretOfSet, SecretOfRotate);
    }

    [Fact]
    public async Task Put_ShouldRemoveTheSecretsAndKeepTheQueue_WhenLevel2IsDisabled()
    {
        await SeedAsync(new() { [QueueKey] = "q1", [Current] = SecretOfRotate, [Previous] = SecretOfSet, [ReplacedAt] = OldStamp });
        var disable = Fixture()["disableLevel2"]!;

        var (status, body) = await SendAsync(disable["request"]!);

        status.Should().Be((HttpStatusCode)disable["status"]!.GetValue<int>(), body);
        (await LoadAsync()).Should().Equal(new Dictionary<string, string> { [QueueKey] = "q1" });
    }

    [Fact]
    public async Task Put_ShouldKeepNoGrace_WhenOnlyTheCurrentSecretIsRemoved()
    {
        await SeedAsync(new() { [QueueKey] = "q1", [Current] = SecretOfRotate, [Previous] = SecretOfSet, [ReplacedAt] = OldStamp });

        var (status, body) = await SendAsync(new JsonObject
        {
            ["method"] = "PUT",
            ["path"] = "/api/v1/admin/channels/WebChat",
            ["body"] = new JsonObject { ["isActive"] = true, ["credentials"] = new JsonObject { [Current] = null } },
        });

        status.Should().Be(HttpStatusCode.OK, body);
        (await LoadAsync()).Should().Equal(new Dictionary<string, string> { [QueueKey] = "q1" });
    }

    [Fact]
    public async Task Get_ShouldMatchFixture_WhenQueueAndSecretAreStored()
    {
        await SeedAsync(new() { [QueueKey] = "q1", [Current] = SecretOfSet });
        var read = Fixture()["read"]!;

        var (status, body) = await SendAsync(read["request"]!);

        status.Should().Be((HttpStatusCode)read["status"]!.GetValue<int>(), body);
        AssertBodyMatchesFixture(body, read["body"]);
        body.Should().NotContain(SecretOfSet);
    }

    [Fact]
    public async Task Get_ShouldListStoredLegacyKeyLast_WhenOutsideTheSchema()
    {
        await SeedAsync(new() { ["WidgetKey"] = "legacy", [Current] = SecretOfSet });

        var (_, body) = await SendAsync(Fixture()["read"]!["request"]!);

        JsonNode.Parse(body)!["credentials"]!.AsArray().Select(c => c!["key"]!.GetValue<string>())
            .Should().Equal(QueueKey, Current, Previous, ReplacedAt, "WidgetKey");
    }

    public static TheoryData<int> RefusalsWithABody()
    {
        var data = new TheoryData<int>();
        var refusals = Fixture()["refusals"]!.AsArray();
        for (var i = 0; i < refusals.Count; i++)
        {
            if (refusals[i]!["body"] is not null)
                data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusalsWithABody))]
    public async Task Put_ShouldAnswerFixtureRefusalAndWriteNothing_WhenCaseIsRefused(int index)
    {
        var seeded = new Dictionary<string, string> { [QueueKey] = "q1", [Current] = SecretOfSet };
        await SeedAsync(seeded);
        var refusal = Fixture()["refusals"]!.AsArray()[index]!;

        var (status, body) = await SendAsync(refusal["request"]!);

        status.Should().Be((HttpStatusCode)refusal["status"]!.GetValue<int>(), body);
        AssertBodyMatchesFixture(body, refusal["body"]);
        (await LoadAsync()).Should().Equal(seeded, "a refused request stores nothing");
    }

    [Theory]
    [InlineData("2026-11-04T09:00:00Z")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Put_ShouldRefuseTheStamp_WhenNamedWithAnyValue(string? value)
    {
        var seeded = new Dictionary<string, string> { [Current] = SecretOfSet, [Previous] = SecretOfRotate, [ReplacedAt] = OldStamp };
        await SeedAsync(seeded);

        var (status, body) = await SendAsync(new JsonObject
        {
            ["method"] = "PUT",
            ["path"] = "/api/v1/admin/channels/WebChat",
            ["body"] = new JsonObject { ["isActive"] = true, ["credentials"] = new JsonObject { [ReplacedAt] = value } },
        });

        status.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be("server-managed-credential-key");
        (await LoadAsync()).Should().Equal(seeded);
    }

    [Fact]
    public async Task Put_ShouldRefuseSecretOverTheLimit_WhenLongerThan512()
    {
        var seeded = new Dictionary<string, string> { [Current] = SecretOfSet };
        await SeedAsync(seeded);

        var (status, body) = await SendAsync(new JsonObject
        {
            ["method"] = "PUT",
            ["path"] = "/api/v1/admin/channels/WebChat",
            ["body"] = new JsonObject { ["isActive"] = true, ["credentials"] = new JsonObject { [Current] = new string('x', 513) } },
        });

        status.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be("weak-identity-token-secret");
        (await LoadAsync()).Should().Equal(seeded);
    }

    [Fact]
    public async Task Put_ShouldAnswer403_WhenCallerIsNotAnAdmin()
    {
        var refusal = Fixture()["refusals"]!.AsArray().Single(r => r!["body"] is null)!;
        var agent = _ownership.NewUser(UserRole.Agent, "not-an-admin");
        using var client = _ownership.ClientFor(agent);
        using var content = new StringContent("""{"isActive":true}""", Encoding.UTF8, "application/json");

        using var response = await client.PutAsync(new Uri(refusal["request"]!["path"]!.GetValue<string>(), UriKind.Relative), content);

        response.StatusCode.Should().Be((HttpStatusCode)refusal["status"]!.GetValue<int>());
    }
}
