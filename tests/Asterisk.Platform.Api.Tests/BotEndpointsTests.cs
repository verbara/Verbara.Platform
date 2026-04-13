using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Tests;

public sealed class BotEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;
    private readonly IBotConfigStore _store;
    private readonly TenantId _tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);

    public BotEndpointsTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _store = factory.Services.GetRequiredService<IBotConfigStore>();
    }

    private async Task ClearBotsAsync()
    {
        var existing = await _store.ListAsync(_tenantId, CancellationToken.None);
        foreach (var b in existing)
            await _store.DeleteAsync(_tenantId, b.BotId, CancellationToken.None);
    }

    private async Task SeedBotAsync(string name, bool isActive = true)
    {
        await _store.SaveAsync(new BotConfiguration
        {
            BotId = EntityId.New(),
            TenantId = _tenantId,
            Name = name,
            IsActive = isActive,
            ConfidenceThreshold = 0.7,
            MaxTurnsBeforeHandoff = 20,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ListBots_ShouldReturnAllTenantBots_AsBotDto()
    {
        await ClearBotsAsync();
        await SeedBotAsync("Bot Alpha");
        await SeedBotAsync("Bot Beta");

        var response = await _client.GetAsync("/api/v1/admin/bots");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().Be(2);
        foreach (var item in arr.EnumerateArray())
        {
            item.TryGetProperty("id", out var idProp).Should().BeTrue("DTO must expose lowercase 'id'");
            idProp.GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task CreateBot_ShouldReturn201WithBotDto_HavingIdField()
    {
        await ClearBotsAsync();

        var body = JsonContent.Create(new
        {
            name = "New Bot",
            confidenceThreshold = 0.85,
            maxTurns = 15,
            isActive = true,
        });

        var response = await _client.PostAsync("/api/v1/admin/bots", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("id", out var idProp).Should().BeTrue();
        idProp.GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("name").GetString().Should().Be("New Bot");
        root.GetProperty("maxTurns").GetInt32().Should().Be(15);
        // defaultFlowId is null => serializer omits it (WhenWritingNull) OR emits null.
        if (root.TryGetProperty("defaultFlowId", out var flowProp))
            flowProp.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CreateBot_ShouldAcceptNullDefaultFlowId()
    {
        await ClearBotsAsync();

        var body = JsonContent.Create(new
        {
            name = "Bot Without Flow",
            confidenceThreshold = 0.7,
            maxTurns = 20,
            isActive = true,
        });

        var response = await _client.PostAsync("/api/v1/admin/bots", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DeleteBot_ShouldHardDelete_AndSubsequentListExcludesIt()
    {
        await ClearBotsAsync();
        var botId = EntityId.New();
        await _store.SaveAsync(new BotConfiguration
        {
            BotId = botId,
            TenantId = _tenantId,
            Name = "To Be Deleted",
            IsActive = true,
            ConfidenceThreshold = 0.7,
            MaxTurnsBeforeHandoff = 20,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/bots/{botId.Value}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listResponse = await _client.GetAsync("/api/v1/admin/bots");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await listResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task DeleteBot_ShouldReturn404_WhenBotDoesNotExist()
    {
        var randomId = EntityId.New().Value;

        var response = await _client.DeleteAsync($"/api/v1/admin/bots/{randomId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
