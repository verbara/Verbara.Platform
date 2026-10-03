using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The audit entry a conversation change writes: actor = the calling user, the impersonation context when
/// impersonating, and a failed write logged instead of failing a change that has already been made.
/// </summary>
public sealed class ConversationAuditTests
{
    private static readonly TenantId s_tenant = new("tenant-1");
    private static readonly EntityId s_conversation = EntityId.From("conv-1");

    [Fact]
    public async Task TryRecordAsync_ShouldRecordTheCallerAndTheImpersonationContext_WhenTheCallerImpersonates()
    {
        var audit = Substitute.For<IAuditService>();
        var context = ContextFor(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "admin-7"),
            new Claim("impersonation", "true"),
            new Claim("impersonator_id", "admin-7"),
            new Claim("impersonator_tenant", "platform"),
            new Claim("impersonation_session_id", "session-9"),
        ], "test")));

        await ConversationAudit.TryRecordAsync(
            context, audit, s_tenant, "conversation.transferred", s_conversation,
            new Dictionary<string, string> { ["target_queue"] = "queue-1" }, CancellationToken.None);

        await audit.Received(1).RecordAsync(
            s_tenant,
            "conversations",
            "conversation.transferred",
            "info",
            "admin-7",
            "user",
            s_conversation.Value,
            "Conversation",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m =>
                m != null
                && m["target_queue"] == "queue-1"
                && m["impersonator_tenant"] == "platform"
                && m["impersonation_session_id"] == "session-9"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRecordAsync_ShouldLogAndReturn_WhenTheAuditWriteFails()
    {
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(default, default!, default!, default!, default!, default!)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("audit store unavailable")));
        var capture = new LogRecordCapture();
        var context = ContextFor(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "agent-user-1")], "test")), capture);

        var record = () => ConversationAudit.TryRecordAsync(
            context, audit, s_tenant, "conversation.taken_over", s_conversation,
            new Dictionary<string, string>(), CancellationToken.None);

        await record.Should().NotThrowAsync(because: "the change was made before its entry was written");
        capture.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.EventId.Id == 7510)
            .Which.Message.Should().Contain("conversation.taken_over").And.Contain(s_conversation.Value);
    }

    private static DefaultHttpContext ContextFor(ClaimsPrincipal user, LogRecordCapture? capture = null)
    {
        var services = new ServiceCollection().AddLogging(logging =>
        {
            if (capture is not null)
                logging.AddProvider(capture);
        });
        return new DefaultHttpContext { User = user, RequestServices = services.BuildServiceProvider() };
    }
}
