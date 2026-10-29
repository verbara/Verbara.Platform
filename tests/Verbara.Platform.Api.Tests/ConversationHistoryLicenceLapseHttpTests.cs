using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Conversation history does not depend on the EventStore module: with an expired licence whose
/// real <see cref="LicenseGuard"/> denies <see cref="LicenseFeature.EventStore"/>, an authenticated
/// admin still lists conversations, reads one and reads its messages over HTTP.
/// </summary>
public sealed class ConversationHistoryLicenceLapseHttpTests
{
    [Fact]
    public async Task GetConversationHistory_ShouldReturnSeededData_WhenLicenceExpiredAndEventStoreDenied()
    {
        using var factory = new ExpiredLicenceFactory();
        factory.Services.GetRequiredService<ILicenseGuard>()
            .CanExecute(LicenseFeature.EventStore).Allowed
            .Should().BeFalse("the test must run on a host whose guard denies EventStore");

        var (conversationId, messageIds) = await SeedConversationAsync(factory.Services);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);

        using (var list = await GetJsonAsync(client, "/api/v1/conversations?pageSize=100"))
            list.RootElement.GetProperty("items").EnumerateArray()
                .Select(c => c.GetProperty("conversationId").GetString())
                .Should().Contain(conversationId);

        using (var one = await GetJsonAsync(client, $"/api/v1/conversations/{conversationId}"))
            one.RootElement.GetProperty("conversationId").GetString().Should().Be(conversationId);

        using (var messages = await GetJsonAsync(client, $"/api/v1/conversations/{conversationId}/messages"))
            messages.RootElement.EnumerateArray()
                .Select(m => m.GetProperty("messageId").GetString())
                .Should().BeEquivalentTo(messageIds);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path} answered {body}");
        return JsonDocument.Parse(body);
    }

    private static async Task<(string ConversationId, string[] MessageIds)> SeedConversationAsync(IServiceProvider services)
    {
        var tenant = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId);
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-lapse-{Guid.NewGuid():N}"),
            TenantId = tenant,
            ContactId = EntityId.From($"contact-lapse-{Guid.NewGuid():N}"),
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            CreatedAt = now.AddMinutes(-10),
        };
        await services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);

        var messageStore = services.GetRequiredService<IMessageStore>();
        var messageIds = new List<string>();
        foreach (var (direction, text, minutesAgo) in new[]
                 {
                     (MessageDirection.Inbound, "hello, my order is late", 9),
                     (MessageDirection.Outbound, "let me check that for you", 8),
                 })
        {
            var message = new Message
            {
                MessageId = EntityId.New(),
                ConversationId = conversation.ConversationId,
                TenantId = tenant,
                Direction = direction,
                Channel = ChannelType.WebChat,
                Content = new MessageEnvelope([new TextBlock(text)]),
                DeliveryStatus = MessageDeliveryStatus.Delivered,
                CreatedAt = now.AddMinutes(-minutesAgo),
            };
            await messageStore.SaveAsync(message, CancellationToken.None);
            messageIds.Add(message.MessageId.Value);
        }

        return (conversation.ConversationId.Value, [.. messageIds]);
    }

    /// <summary>
    /// Admin-authenticated host whose <see cref="ILicenseStatus"/> is the real tracker the real
    /// guard reads, set to <see cref="LicenseValidationResult.Expired"/> with a key carrying every
    /// bit (same construction as <c>LicenseGateLicenceStateHttpTests.LicenceStateFactory</c>).
    /// </summary>
    private sealed class ExpiredLicenceFactory : WebApplicationFactory<Program>
    {
        private static readonly string s_hashedKey =
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(AuthenticatedPlatformApiFactory.TestApiKey)));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.SetupTestAuth(
                    services, s_hashedKey,
                    AuthenticatedPlatformApiFactory.TestTenantId,
                    AuthenticatedPlatformApiFactory.TestUserId);
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

                foreach (var d in services.Where(d => d.ServiceType == typeof(ILicenseStatus)).ToList())
                    services.Remove(d);
                services.AddSingleton<ILicenseStatus>(sp => sp.GetRequiredService<LicenseStatusTracker>());
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);

                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            });

            var host = base.CreateHost(builder);

            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);
            AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);

            host.Services.GetRequiredService<LicenseStatusTracker>().Update(
                LicenseValidationResult.Expired,
                new LicenseKey("lic-history-test", "Test Licensee", LicenseTier.SelfHostBusiness,
                    DateTimeOffset.UtcNow.AddDays(-30),
                    LicenseFeature.All, MaxAgents: 50, MaxNodes: 3, Signature: "not-checked-by-the-gate"));

            return host;
        }
    }
}
