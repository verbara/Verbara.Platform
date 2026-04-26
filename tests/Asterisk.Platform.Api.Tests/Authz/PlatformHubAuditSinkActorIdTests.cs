using Asterisk.Platform.Api.Authz;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Push.SignalR.Authz;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Authz;

/// <summary>
/// R5.3 Phase A Wave 2 Task A.3.b — Verifies that <see cref="PlatformHubAuditSink"/>
/// threads <see cref="HubAuditEntry.ActorId"/> through to <see cref="IAuditService.RecordAsync"/>
/// instead of the legacy hardcoded <c>"unknown"</c> placeholder. Closes B.4 Set B item.
/// </summary>
public sealed class PlatformHubAuditSinkActorIdTests
{
    [Fact]
    public async Task WriteAsync_ShouldRecordActorIdFromEntry_WhenActorIdProvided()
    {
        var auditService = Substitute.For<IAuditService>();
        var sink = new PlatformHubAuditSink(auditService, NullLogger<PlatformHubAuditSink>.Instance);

        var entry = new HubAuditEntry
        {
            Action = "hub.cross_tenant_subscription_denied",
            ActorTenantId = "tenant-A",
            ActorId = "user-123",
            TargetId = "agent-7",
            Metadata = null,
        };

        await sink.WriteAsync(entry, CancellationToken.None);

        await auditService.Received(1).RecordAsync(
            Arg.Any<TenantId>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            actorId: "user-123",
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }
}
