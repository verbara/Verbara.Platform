using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Email;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public sealed class PasswordResetEmailTests
{
    private const string TestTenantId = "reset-email-test-tenant";
    private const string TestEmail = "user@reset-test.internal";
    private const string TestUserId = "reset-test-user-id";

    // ─── Test 1: ForgotPassword returns 200 when user exists (email sending attempted) ──

    [Fact]
    public async Task ForgotPassword_ShouldReturn200_WhenEmailExists()
    {
        // Arrange — factory with a real user seeded in IUserStore and a mock IEmailService
        using var factory = new PasswordResetEmailFactory(withEmailService: true);
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            tenantId = TestTenantId,
            email = TestEmail,
        });

        // Assert — always 200 (anti-enumeration)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("reset link has been sent");
    }

    // ─── Test 2: ForgotPassword works even when IEmailService is not configured ──

    [Fact]
    public async Task ForgotPassword_ShouldStillWork_WhenEmailServiceNotConfigured()
    {
        // Arrange — factory without IEmailService registered at all
        using var factory = new PasswordResetEmailFactory(withEmailService: false);
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            tenantId = TestTenantId,
            email = TestEmail,
        });

        // Assert — endpoint must not crash; always returns 200
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("reset link has been sent");
    }

    // ─── Shared factory ──────────────────────────────────────────────────────────

    private sealed class PasswordResetEmailFactory : WebApplicationFactory<Program>
    {
        private readonly bool _withEmailService;

        public PasswordResetEmailFactory(bool withEmailService)
        {
            _withEmailService = withEmailService;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Stub Asterisk hosted services and licensing
                AuthenticatedPlatformApiFactory.StubAsteriskHostedServices(services);
                services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);

                // Dialer / analytics InMemory fallbacks
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                // Seed IUserStore with a real user so ForgotPassword finds them
                var userEntityId = EntityId.From(TestUserId);
                var tenantId = new TenantId(TestTenantId);

                var userStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserStore));
                if (userStoreDescriptor is not null) services.Remove(userStoreDescriptor);

                var userStore = Substitute.For<IUserStore>();
                var testUser = new User
                {
                    UserId = userEntityId,
                    TenantId = tenantId,
                    Email = TestEmail,
                    DisplayName = "Reset Test User",
                    Role = UserRole.Agent,
                    Status = UserStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                userStore.GetByEmailAsync(tenantId, TestEmail, Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<User?>(testUser));
                services.AddSingleton(userStore);

                if (_withEmailService)
                {
                    // Register a no-op mock IEmailService so the email path executes without SMTP.
                    // NSubstitute returns default(ValueTask) for value-task methods by default.
                    var emailSvc = Substitute.For<IEmailService>();
                    services.AddSingleton(emailSvc);
                }
                else
                {
                    // Remove IEmailService if registered so the graceful-degradation path is exercised
                    var emailDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailService));
                    if (emailDescriptor is not null) services.Remove(emailDescriptor);
                }
            });

            var host = base.CreateHost(builder);

            // Seed feature gate cache so plan-gated endpoints resolve (not needed here, but defensive)
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TestTenantId);

            return host;
        }
    }
}
