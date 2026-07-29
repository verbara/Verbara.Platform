using Verbara.Platform.Automation;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Flows;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Storage.Postgres.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private const string FakeConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres";

    [Fact]
    public void AddPostgresStorage_ShouldRegisterAllStoreInterfaces()
    {
        var services = new ServiceCollection();
        // AddPostgresStorage carries an implicit IDataProtectionProvider dependency:
        // PostgresUserStore and PostgresTenantAuthConfigStore resolve it to wrap their
        // column-level secrets (ADR-0003's protected-column register). The composition
        // root always supplies it via AddPlatformDataProtection; a bare ServiceCollection
        // must register a keyring explicitly. AddPostgresStorage deliberately does NOT
        // self-register one — a silently installed ephemeral keyring is the exact
        // ADR-0003 footgun ("keys regenerated on each process start… in use by accident").
        services.AddDataProtection();
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

        // MultiTenant
        provider.GetService<ITenantStore>().Should().NotBeNull();
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
    public void AddPostgresStorage_ShouldRegisterAllExpectedStores()
    {
        var services = new ServiceCollection();
        // See AddPostgresStorage_ShouldRegisterAllStoreInterfaces for why the keyring
        // is registered explicitly here rather than by AddPostgresStorage itself.
        services.AddDataProtection();
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
            typeof(ITenantStore),
        };

        storeInterfaces.Should().HaveCount(14);

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
    public void BaselineMigration_ShouldExistAndContainExpectedTables()
    {
        // The migration history was consolidated into a single clean baseline
        // (001_Baseline.sql) pre-launch — verify it exists on disk.
        var sql = ReadBaselineMigration();

        sql.Should().Contain("CREATE TABLE IF NOT EXISTS users", because: "users table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS api_keys", because: "api_keys table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS conversations", because: "conversations table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS messages", because: "messages table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS contacts", because: "contacts table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS queue_configs", because: "queue_configs table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS agents", because: "agents table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS tenant_channel_configs", because: "tenant_channel_configs table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS flow_definitions", because: "flow_definitions table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS flow_executions", because: "flow_executions table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS bot_configurations", because: "bot_configurations table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS automation_rules", because: "automation_rules table must be defined");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS scheduled_timers", because: "scheduled_timers table must be defined");

        // Folded-in tables that were previously separate migration files.
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS tenants", because: "tenants table must be folded into the baseline");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS scheduled_reports", because: "scheduled_reports table must be folded into the baseline");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS report_executions", because: "report_executions table must be folded into the baseline");
    }

    [Fact]
    public void BaselineMigration_ShouldDropFlatDispositionDomain()
    {
        var sql = ReadBaselineMigration();

        sql.Should().NotContain("CREATE TABLE IF NOT EXISTS dispositions",
            because: "the flat-disposition domain was removed in the clean-break baseline");
        sql.Should().NotContain("CREATE TABLE IF NOT EXISTS wrap_up_records",
            because: "the flat-disposition wrap-up records table was removed in the clean-break baseline");
        sql.Should().NotContain("wrap_up JSONB",
            because: "the conversations.wrap_up column was removed in the clean-break baseline");
    }

    [Fact]
    public void BaselineMigration_ShouldContainTypificationTables()
    {
        var sql = ReadBaselineMigration();

        sql.Should().Contain("CREATE TABLE IF NOT EXISTS typification_schemas",
            because: "typification_schemas replaces the removed flat-disposition domain");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS typification_bindings",
            because: "typification_bindings replaces the removed flat-disposition domain");
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS typification_submissions",
            because: "typification_submissions replaces the removed flat-disposition domain");
    }

    private static string ReadBaselineMigration()
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Verbara.Platform.Storage.Postgres", "Migrations", "001_Baseline.sql");

        var normalizedPath = Path.GetFullPath(migrationPath);
        File.Exists(normalizedPath).Should().BeTrue(
            because: "consolidated migration file 001_Baseline.sql must exist on disk");

        return File.ReadAllText(normalizedPath);
    }
}
