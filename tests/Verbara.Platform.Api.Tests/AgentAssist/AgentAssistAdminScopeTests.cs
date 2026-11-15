using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Endpoints;
using Verbara.Sdk.Pro.AgentAssist.Features;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// The Agent Assist feature toggle (<c>/api/v1/admin/features/agent-assist</c>) and the keyword and compliance rule
/// store (<c>/api/v1/admin/agent-assist/*</c>) each hold one state for the whole deployment, so they belong to the
/// deployment operator — a Platform-tenant administrator — and to no Customer tenant's administrator. The Customer
/// admin here is an Admin-role user whose permissions this host resolves from the built-in <c>admin</c> template, so
/// it holds <c>features:agent-assist:manage</c> and <c>agentassist:config:manage</c> and is still refused.
/// </summary>
public sealed class AgentAssistAdminScopeTests : IClassFixture<AgentAssistTenantScopeApiFactory>
{
    private const string Acme = AgentAssistTenantScopeApiFactory.Acme;
    private const string FeatureRoute = "/api/v1/admin/features/agent-assist";
    private const string ConfigRoute = "/api/v1/admin/agent-assist/config";
    private const string KeywordRulesRoute = "/api/v1/admin/agent-assist/keyword-rules";
    private const string ComplianceRulesRoute = "/api/v1/admin/agent-assist/compliance-rules";

    private const string FeatureOffJson = """{"enabled":false,"provider":null,"credentials":null}""";
    private const string FeatureOnJson =
        """{"enabled":true,"provider":"deepgram","credentials":{"apiKey":"dg-tenant-key","endpoint":"https://stt.example.test"}}""";
    private const string ConfigJson =
        """{"audioSocketPort":9092,"suggestionTimeoutMs":1500,"sentimentTimeoutMs":1500,"complianceTimeoutMs":1500,"maxHistorySegments":20,"inactivityTimeoutMinutes":5,"whisperEnabled":false,"whisperThreshold":"High","filterQueueNames":[],"filterAgentIds":[]}""";
    private const string KeywordRulesJson =
        """[{"ruleId":"kw-1","keyword":"refund","priority":"high","suggestionText":"Offer the refund policy"}]""";
    private const string ComplianceRulesJson =
        """[{"ruleId":"cr-1","pattern":"card number","severity":"high","action":"alert","description":null}]""";

    private readonly AgentAssistTenantScopeApiFactory _factory;

    public AgentAssistAdminScopeTests(AgentAssistTenantScopeApiFactory factory) => _factory = factory;

    public static TheoryData<string, string, string?> Routes() => new()
    {
        { "GET", FeatureRoute, null },
        { "PUT", FeatureRoute, FeatureOffJson },
        { "GET", ConfigRoute, null },
        { "PUT", ConfigRoute, ConfigJson },
        { "GET", KeywordRulesRoute, null },
        { "PUT", KeywordRulesRoute, KeywordRulesJson },
        { "GET", ComplianceRulesRoute, null },
        { "PUT", ComplianceRulesRoute, ComplianceRulesJson },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AgentAssistAdminRoute_ShouldReturn403_WhenCallerIsCustomerTenantAdmin(string method, string route, string? json)
    {
        using var client = _factory.CustomerAdminClient(Acme);

        using var response = await client.SendAsync(Request(method, route, json));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: $"{method} {route} reads or writes state every tenant shares, which no Customer tenant's admin may touch " +
                     $"({await response.Content.ReadAsStringAsync()})");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task AgentAssistAdminRoute_ShouldReturn200_WhenCallerIsPlatformOperator(string method, string route, string? json)
    {
        using var client = _factory.PlatformOperatorClient();

        using var response = await client.SendAsync(Request(method, route, json));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"the deployment operator configures Agent Assist ({await response.Content.ReadAsStringAsync()})");
    }

    [Fact]
    public async Task UpdateFeature_ShouldLeaveToggleUnchanged_WhenCallerIsCustomerTenantAdmin()
    {
        var toggle = _factory.Services.GetRequiredService<IAgentAssistFeatureToggle>();
        var before = await toggle.GetCurrentAsync();
        using var client = _factory.CustomerAdminClient(Acme);

        using var response = await client.SendAsync(Request("PUT", FeatureRoute, FeatureOnJson));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var after = await toggle.GetCurrentAsync();
        after.Enabled.Should().Be(before.Enabled);
        after.Provider.Should().Be(before.Provider);
        after.ProtectedCredentials.Should().BeEquivalentTo(before.ProtectedCredentials,
            because: "a refused write must not store the caller's provider key or endpoint");
    }

    [Fact]
    public async Task UpdateKeywordRules_ShouldLeaveRulesUnchanged_WhenCallerIsCustomerTenantAdmin()
    {
        var store = _factory.Services.GetRequiredService<AgentAssistConfigStore>();
        var before = store.GetKeywordRules();
        using var client = _factory.CustomerAdminClient(Acme);

        using var response = await client.SendAsync(Request(
            "PUT", KeywordRulesRoute, """[{"ruleId":"kw-tenant","keyword":"tenant","priority":"low","suggestionText":null}]"""));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        store.GetKeywordRules().Should().BeSameAs(before);
    }

    private static HttpRequestMessage Request(string method, string route, string? json)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }
}
