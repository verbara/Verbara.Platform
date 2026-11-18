using System.Net;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Verbara.Sdk.Pro.CallAnalytics.Domain;
using Verbara.Sdk.Pro.CallAnalytics.Store;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Pro <c>2.27.0-pro</c>: the call-analytics Postgres store writes and reads each analysis's queue, so the
/// queue filter of the sentiment-trends and compliance-summary endpoints matches the analyses stored since the upgrade.
/// A row stored before it keeps a null queue and still does not match. The real Api <c>Program</c> boots with an
/// Analytics connection string (a Testcontainers Postgres), so the endpoints read Pro's Postgres store as
/// <c>Program.cs</c> registers it.
/// </summary>
public sealed class CallAnalyticsQueueFilterLiveDbTests : IAsyncLifetime
{
    private const string Queue = "q1";
    private const string SavedSessionId = "queue-filter-saved";
    private const string PreUpgradeSessionId = "queue-filter-pre-upgrade";

    private IContainer? _postgres;

    [Fact]
    public async Task GetSentimentTrendsAndComplianceSummary_ShouldReturnTheQueuesSavedAnalysis_WhenAPreUpgradeRowHasNoQueue()
    {
        await using var factory = new PostgresCallAnalyticsApiFactory(ConnectionString);
        using var client = factory.CreateAuthenticatedClient();
        var store = factory.Services.GetRequiredService<ICallAnalyticsStore>();
        store.GetType().Name.Should().Be("PostgresCallAnalyticsStore", "precondition: the endpoints read Pro's Postgres store");

        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(Analysis(SavedSessionId, now.AddMinutes(-1)));
        await store.SaveAsync(Analysis(PreUpgradeSessionId, now.AddMinutes(-2)));
        await ClearQueueAsync(PreUpgradeSessionId); // a row written before the store stored the queue

        var trends = await GetJsonAsync(client, $"/api/v1/call-analytics/sentiment/trends?queueName={Queue}");
        var compliance = await GetJsonAsync(client, $"/api/v1/call-analytics/compliance/summary?queueName={Queue}");

        trends["points"]!.AsArray().Sum(p => p!["totalCount"]!.GetValue<int>()).Should().Be(1,
            "sentiment trends count the analysis saved with the queue and not the pre-upgrade row");
        compliance["totalSessionsWithViolations"]!.GetValue<int>().Should().Be(1,
            "the compliance summary counts the analysis saved with the queue and not the pre-upgrade row");
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private async Task ClearQueueAsync(string sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE call_analysis_results SET queue_name = NULL WHERE tenant_id = @tenant AND session_id = @session",
            connection);
        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlTypes.NpgsqlDbType.Text) { Value = UnifiedPlatformApiFactory.TestTenantId });
        command.Parameters.Add(new NpgsqlParameter("session", NpgsqlTypes.NpgsqlDbType.Text) { Value = sessionId });
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static CallAnalysisResult Analysis(string sessionId, DateTimeOffset analyzedAt) => new()
    {
        SessionId = sessionId,
        TenantId = UnifiedPlatformApiFactory.TestTenantId,
        QueueName = Queue,
        AnalyzedAt = analyzedAt,
        ProcessingTime = TimeSpan.FromSeconds(2),
        ComplianceViolations =
        [
            new ComplianceViolation
            {
                RuleId = "rule-0", RuleName = "Rule 0", Severity = ComplianceSeverity.Warning, Description = "V",
            },
        ],
        Sentiment = new SentimentResult
        {
            OverallScore = 0.6f,
            OverallLabel = SentimentLabel.Positive,
            StartLabel = SentimentLabel.Neutral,
            EndLabel = SentimentLabel.Positive,
            Trend = SentimentTrend.Improving,
        },
        Metrics = new ConversationMetrics
        {
            AgentTalkRatio = 0.45,
            TotalAgentTalk = TimeSpan.FromMinutes(3),
            TotalCallerTalk = TimeSpan.FromMinutes(2),
            LongestAgentMonologue = TimeSpan.FromSeconds(45),
            LongestCallerMonologue = TimeSpan.FromSeconds(30),
            AgentTurnCount = 12,
            CallerTurnCount = 10,
            SilenceCount = 3,
            TotalSilence = TimeSpan.FromSeconds(15),
            InterruptionCount = 1,
        },
    };

    private string ConnectionString =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres;Application Name=call-analytics-queue-filter";

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the image's entrypoint first runs a socket-only temporary
            // server, so a socket probe greens before 5432 accepts connections.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    /// <summary>
    /// <see cref="UnifiedPlatformApiFactory"/> over an Analytics connection string, keeping the
    /// <see cref="ICallAnalyticsStore"/> registrations <c>Program.cs</c> made (Pro's <c>AddProCallAnalyticsPostgres</c>).
    /// </summary>
    private sealed class PostgresCallAnalyticsApiFactory(string analyticsConnectionString) : UnifiedPlatformApiFactory
    {
        private List<ServiceDescriptor> _programStores = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:Analytics", analyticsConnectionString);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Registered before the base factory's callback, so it reads Program.cs's registrations untouched.
            builder.ConfigureServices(services =>
                _programStores = services.Where(d => d.ServiceType == typeof(ICallAnalyticsStore)).ToList());
            return base.CreateHost(builder);
        }

        protected override void ConfigureCallAnalyticsStore(IServiceCollection services)
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(ICallAnalyticsStore)).ToList())
                services.Remove(d);
            foreach (var d in _programStores)
                services.Add(d);
        }
    }
}
