using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 1.4 through the API host on the in-memory stores: the new clock stays off
/// the wire (design D2, <c>[JsonIgnore]</c>), and the per-tenant window override survives the existing tenant-settings
/// save, whose response gains no field (design D11).
/// </summary>
public sealed class ConversationClockApiTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId Tenant = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public ConversationClockApiTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetConversation_ShouldNotExposeLastMessageAt_WhenTheClockIsSet()
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            ContactId = EntityId.From("contact-clock"),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);

        using var response = await _client.GetAsync($"/api/v1/conversations/{conversation.ConversationId.Value}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(conversation.ConversationId.Value);
        body.Should().NotContainEquivalentOf("lastMessageAt", "the clock is [JsonIgnore]: no wire change (D2, Web)");
    }

    [Fact]
    public async Task UpdateSettings_ShouldKeepTheStoredOverrideAndAddNoField_WhenSecuritySettingsAreSaved()
    {
        var inner = (InMemoryTenantAuthConfigStore)_factory.Services
            .GetRequiredKeyedService<ITenantAuthConfigStore>(AuthHotpathCacheKeys.TenantAuthConfigStoreInner);
        await inner.SaveAsync(new TenantAuthConfig { TenantId = Tenant.Value }, CancellationToken.None);
        inner.SeedInactivityCloseAfterHours(Tenant.Value, 48);

        using var put = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            auth = new { passwordMinLength = 15 },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var stored = await inner.GetAsync(Tenant.Value, CancellationToken.None);
        stored!.PasswordMinLength.Should().Be(15, "the save ran");
        stored.InactivityCloseAfterHours.Should().Be(48, "the whole-row save never writes the override (D11)");

        using var get = await _client.GetAsync("/api/v1/admin/tenant/settings");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("inactivity",
            "the override has no endpoint and no DTO field (D11)");
    }
}
