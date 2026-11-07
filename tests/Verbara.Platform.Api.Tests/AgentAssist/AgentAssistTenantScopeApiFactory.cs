using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Seeds;
using Verbara.Sdk.Pro.AgentAssist.Storage.Postgres.Stores;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-sessions-stay-in-their-tenant task 1.2: a host with two Customer tenants, <see cref="Acme"/> and
/// <see cref="Globex"/>, the Agent Assist licence and plan features seeded for both, and a Supervisor-role user in each
/// holding the built-in Supervisor template's permissions (which include <c>contacts:conversation:monitor</c>, so the
/// conversation read rule's reads-everything gate lets them through). The two Agent Assist seams are replaced by
/// hand-written fakes (<see cref="Live"/>, <see cref="Records"/>): both seams are internal, so a proxy library cannot
/// implement them (audit r2 F1).
/// </summary>
/// <remarks>
/// A request carrying <see cref="NoUserIdHeader"/> authenticates through a test scheme as a Supervisor of
/// <see cref="Acme"/> with no <c>sub</c>, <c>user_id</c> or <c>NameIdentifier</c> claim: neither the host's JWTs nor
/// its API keys can carry such a principal.
/// </remarks>
public sealed class AgentAssistTenantScopeApiFactory : AccountStatusApiFactory
{
    public const string Acme = "acme";
    public const string Globex = "globex";

    /// <summary>The header that selects the test scheme's principal with no user id claim.</summary>
    public const string NoUserIdHeader = "X-Test-Principal-Without-User-Id";

    private const string NoUserIdScheme = "AgentAssistTestNoUserId";

    private readonly Lock _seedLock = new();
    private readonly Dictionary<string, User> _supervisors = new(StringComparer.Ordinal);

    public FakeLiveSessions Live { get; } = new();

    public FakeRecordReader Records { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        services.RemoveAll<IAgentAssistLiveSessions>();
        services.AddSingleton<IAgentAssistLiveSessions>(Live);
        services.RemoveAll<IAgentAssistRecordReader>();
        services.AddSingleton<IAgentAssistRecordReader>(Records);

        services.RemoveAll<IUserRoleStore>();
        services.AddSingleton<IUserRoleStore>(RoleTemplateGrants);

        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NoUserIdHandler>(NoUserIdScheme, _ => { });
        services.PostConfigure<PolicySchemeOptions>(AuthSchemeConfiguration.DynamicSelector, options =>
        {
            var inner = options.ForwardDefaultSelector;
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(NoUserIdHeader) ? NoUserIdScheme : inner?.Invoke(context);
        });
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        foreach (var tenantId in new[] { Acme, Globex })
        {
            // An Enterprise Customer tenant with no parent: a parent's plan would cap its features.
            AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, tenantId);
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, tenantId);
        }

        return host;
    }

    /// <summary>The Supervisor-role user of <paramref name="tenantId"/>, created on first use.</summary>
    public User SupervisorOf(string tenantId)
    {
        lock (_seedLock)
        {
            if (!_supervisors.TryGetValue(tenantId, out var user))
            {
                user = SaveUser($"{tenantId}-supervisor-{Guid.NewGuid():N}", tenantId, UserRole.Supervisor, UserStatus.Active);
                _supervisors[tenantId] = user;
            }

            return user;
        }
    }

    /// <summary>A client signed in as <paramref name="tenantId"/>'s supervisor with an access token this host mints.</summary>
    public HttpClient SupervisorClient(string tenantId) => CreateBearerClient(MintAccessToken(SupervisorOf(tenantId)));

    /// <summary>A client whose principal is a Supervisor of <see cref="Acme"/> with no user id claim.</summary>
    public HttpClient SupervisorWithoutUserIdClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(NoUserIdHeader, "1");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Acme);
        return client;
    }

    private static IUserRoleStore RoleTemplateGrants(IServiceProvider services)
    {
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var user = await services.GetRequiredService<IUserStore>()
                    .GetByIdAsync(call.ArgAt<TenantId>(0), call.ArgAt<EntityId>(1), CancellationToken.None);
                var template = user?.Role switch
                {
                    UserRole.Supervisor => "supervisor",
                    UserRole.Admin => "admin",
                    UserRole.Agent => "agent",
                    _ => null,
                };
                return template is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : (IReadOnlySet<string>)new HashSet<string>(RoleTemplateSeeder.GetTemplatePermissions(template), StringComparer.Ordinal);
            });
        return roles;
    }

    /// <summary>A Supervisor of <see cref="Acme"/> whose credentials carry no user id claim.</summary>
    private sealed class NoUserIdHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, nameof(UserRole.Supervisor)), new Claim("tid", Acme)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

/// <summary>A hand-written <see cref="IAgentAssistLiveSessions"/>: sessions a test adds, and every whisper asked of it.</summary>
public sealed class FakeLiveSessions : IAgentAssistLiveSessions
{
    private readonly ConcurrentDictionary<string, (LiveAssistSession Session, bool DeliversWhispers)> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string SessionId, string Text)> _whispers = new();

    /// <summary>The whispers asked of this seam, in order.</summary>
    public IReadOnlyList<(string SessionId, string Text)> Whispers => [.. _whispers];

    internal void Add(LiveAssistSession session, bool deliversWhispers = true) =>
        _sessions[session.SessionId] = (session, deliversWhispers);

    IReadOnlyList<LiveAssistSession> IAgentAssistLiveSessions.Snapshot() => [.. _sessions.Values.Select(v => v.Session)];

    bool IAgentAssistLiveSessions.TryWhisper(string sessionId, string text)
    {
        _whispers.Enqueue((sessionId, text));
        return _sessions.TryGetValue(sessionId, out var entry) && entry.DeliversWhispers;
    }
}

/// <summary>A hand-written <see cref="IAgentAssistRecordReader"/> over rows a test adds, in the order added.</summary>
public sealed class FakeRecordReader : IAgentAssistRecordReader
{
    private readonly ConcurrentQueue<SuggestionLogRow> _suggestions = new();
    private readonly ConcurrentQueue<ComplianceAlertRow> _compliance = new();

    public void Add(SuggestionLogRow row) => _suggestions.Enqueue(row);

    public void Add(ComplianceAlertRow row) => _compliance.Enqueue(row);

    Task<IReadOnlyList<SuggestionLogRow>> IAgentAssistRecordReader.SuggestionsBySessionAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SuggestionLogRow>>([.. _suggestions.Where(r => r.SessionId == sessionId)]);

    Task<IReadOnlyList<ComplianceAlertRow>> IAgentAssistRecordReader.ComplianceBySessionAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ComplianceAlertRow>>([.. _compliance.Where(r => r.SessionId == sessionId)]);
}
