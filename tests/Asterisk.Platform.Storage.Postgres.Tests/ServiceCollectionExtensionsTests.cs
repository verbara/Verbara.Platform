using Asterisk.Platform.Automation;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Storage.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Storage.Postgres.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private const string FakeConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres";

    [Fact]
    public void AddPostgresStorage_ShouldRegisterAllStoreInterfaces()
    {
        var services = new ServiceCollection();
        services.AddPostgresStorage(FakeConnectionString);
        var provider = services.BuildServiceProvider();

        // Identity
        provider.GetService<IUserStore>().Should().NotBeNull();
        provider.GetService<IApiKeyStore>().Should().NotBeNull();

        // Conversations
        provider.GetService<IConversationStore>().Should().NotBeNull();
        provider.GetService<IMessageStore>().Should().NotBeNull();
        provider.GetService<IContactStore>().Should().NotBeNull();

        // Queues
        provider.GetService<IQueueStore>().Should().NotBeNull();
        provider.GetService<IAgentStore>().Should().NotBeNull();

        // Channels
        provider.GetService<ITenantChannelConfigStore>().Should().NotBeNull();

        // Flows
        provider.GetService<IFlowStore>().Should().NotBeNull();
        provider.GetService<IFlowExecutionStore>().Should().NotBeNull();

        // Bot
        provider.GetService<IBotConfigStore>().Should().NotBeNull();

        // Automation
        provider.GetService<IAutomationRuleStore>().Should().NotBeNull();
        provider.GetService<ITimerStore>().Should().NotBeNull();
    }

    [Fact]
    public void AddPostgresStorage_ShouldRegisterStoresAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddPostgresStorage(FakeConnectionString);
        var provider = services.BuildServiceProvider();

        var store1 = provider.GetRequiredService<IConversationStore>();
        var store2 = provider.GetRequiredService<IConversationStore>();

        store1.Should().BeSameAs(store2);
    }

    [Fact]
    public void AddPostgresStorage_ShouldRegister13Stores()
    {
        var services = new ServiceCollection();
        services.AddPostgresStorage(FakeConnectionString);

        var storeInterfaces = new[]
        {
            typeof(IUserStore),
            typeof(IApiKeyStore),
            typeof(IConversationStore),
            typeof(IMessageStore),
            typeof(IContactStore),
            typeof(IQueueStore),
            typeof(IAgentStore),
            typeof(ITenantChannelConfigStore),
            typeof(IFlowStore),
            typeof(IFlowExecutionStore),
            typeof(IBotConfigStore),
            typeof(IAutomationRuleStore),
            typeof(ITimerStore),
        };

        storeInterfaces.Should().HaveCount(13);

        var provider = services.BuildServiceProvider();
        foreach (var iface in storeInterfaces)
        {
            provider.GetService(iface).Should().NotBeNull(
                because: $"{iface.Name} should be registered");
        }
    }

    [Fact]
    public void AddPostgresStorage_ShouldReturnServiceCollection_ForFluentChaining()
    {
        var services = new ServiceCollection();
        var result = services.AddPostgresStorage(FakeConnectionString);
        result.Should().BeSameAs(services);
    }

    [Fact]
    public void MigrationSql_ShouldExistAndContainExpectedTables()
    {
        // Verify the migration file ships with the package and contains expected DDL
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        // The SQL is a file in the Migrations folder — verify it exists on disk
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Asterisk.Platform.Storage.Postgres", "Migrations", "001_InitialSchema.sql");

        var normalizedPath = Path.GetFullPath(migrationPath);
        File.Exists(normalizedPath).Should().BeTrue(
            because: "migration file 001_InitialSchema.sql must exist on disk");

        var sql = File.ReadAllText(normalizedPath);
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS users", because: "users table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS api_keys", because: "api_keys table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS conversations", because: "conversations table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS messages", because: "messages table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS contacts", because: "contacts table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS queues", because: "queues table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS agents", because: "agents table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS tenant_channel_configs", because: "tenant_channel_configs table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS flow_definitions", because: "flow_definitions table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS flow_executions", because: "flow_executions table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS bot_configurations", because: "bot_configurations table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS automation_rules", because: "automation_rules table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS scheduled_timers", because: "scheduled_timers table must be defined");
    }
}
