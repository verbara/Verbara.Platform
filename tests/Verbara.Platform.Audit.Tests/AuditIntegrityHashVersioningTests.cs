using Verbara.Platform.Core;

namespace Verbara.Platform.Audit.Tests;

/// <summary>
/// audit-trail-integrity-fixes (fix 4): <see cref="AuditEntry.RetainUntil"/> is now covered by
/// the integrity hash for NEWLY written entries under a versioned scheme
/// (<see cref="DefaultAuditService.HashSchemeV2Prefix"/>-prefixed), while pre-existing rows —
/// written before the scheme change, carrying a bare-hex v1 hash — must still verify unchanged
/// (no mass invalidation).
/// </summary>
public class AuditIntegrityHashVersioningTests
{
    private static readonly TenantId Tenant = new("tenant-hash-v2");
    private static readonly DateTimeOffset OccurredAt = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static AuditEntry NewV2Entry(DateTimeOffset? retainUntil, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var hash = DefaultAuditService.ComputeIntegrityHashV2(
            Tenant, "user", "user-1", "typification.autonomous.corrected",
            "Conversation", "conv-1", OccurredAt, retainUntil, metadata);

        return new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant,
            Action = "typification.autonomous.corrected",
            Category = "conversations",
            Severity = "info",
            ActorId = "user-1",
            ActorType = "user",
            TargetId = "conv-1",
            TargetType = "Conversation",
            Metadata = metadata,
            OccurredAt = OccurredAt,
            RetainUntil = retainUntil,
            IntegrityHash = hash,
        };
    }

    [Fact]
    public void ComputeIntegrityHashV2_ShouldProduceHashPrefixedWithSchemeMarker()
    {
        var hash = DefaultAuditService.ComputeIntegrityHashV2(
            Tenant, "user", "user-1", "action", "Conversation", "conv-1",
            OccurredAt, retainUntil: null, metadata: null);

        hash.Should().StartWith(DefaultAuditService.HashSchemeV2Prefix);
    }

    [Fact]
    public void ComputeIntegrityHashV2_ShouldDiffer_WhenRetainUntilChanges()
    {
        // The whole point of fix 4: a RetainUntil mutation must change the hash.
        var hashA = DefaultAuditService.ComputeIntegrityHashV2(
            Tenant, "user", "user-1", "action", "Conversation", "conv-1",
            OccurredAt, retainUntil: OccurredAt.AddDays(90), metadata: null);
        var hashB = DefaultAuditService.ComputeIntegrityHashV2(
            Tenant, "user", "user-1", "action", "Conversation", "conv-1",
            OccurredAt, retainUntil: OccurredAt.AddDays(180), metadata: null);

        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public void ComputeIntegrityHash_V1_ShouldBeUnaffected_ByRetainUntil()
    {
        // The v1 (legacy) scheme never covered RetainUntil — this characterizes that the OLD
        // algorithm is untouched by the fix (it simply isn't used for new writes anymore).
        var hash = DefaultAuditService.ComputeIntegrityHash(
            Tenant, "user", "user-1", "action", "Conversation", "conv-1", OccurredAt, metadata: null);

        hash.Should().NotStartWith(DefaultAuditService.HashSchemeV2Prefix);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void VerifyIntegrity_ShouldReturnTrue_WhenV2EntryUnmodified()
    {
        var entry = NewV2Entry(retainUntil: OccurredAt.AddDays(90));

        DefaultAuditService.VerifyIntegrity(entry).Should().BeTrue();
    }

    [Fact]
    public void VerifyIntegrity_ShouldReturnFalse_WhenV2EntryRetainUntilTampered()
    {
        // GIVEN an audit entry written under the new hash scheme
        var original = NewV2Entry(retainUntil: OccurredAt.AddDays(90));

        // WHEN its retain_until is mutated directly (simulating a storage-level tamper)
        var tampered = new AuditEntry
        {
            EntryId = original.EntryId,
            TenantId = original.TenantId,
            Action = original.Action,
            Category = original.Category,
            Severity = original.Severity,
            ActorId = original.ActorId,
            ActorType = original.ActorType,
            TargetId = original.TargetId,
            TargetType = original.TargetType,
            Metadata = original.Metadata,
            OccurredAt = original.OccurredAt,
            RetainUntil = OccurredAt.AddDays(9999), // tampered retention floor
            IntegrityHash = original.IntegrityHash, // stale hash — computed over the OLD RetainUntil
        };

        // THEN hash verification for that entry fails
        DefaultAuditService.VerifyIntegrity(tampered).Should().BeFalse();
    }

    [Fact]
    public void VerifyIntegrity_ShouldReturnFalse_WhenAnyOtherFieldTampered()
    {
        var original = NewV2Entry(retainUntil: OccurredAt.AddDays(90));
        var tampered = new AuditEntry
        {
            EntryId = original.EntryId,
            TenantId = original.TenantId,
            Action = original.Action,
            Category = original.Category,
            Severity = original.Severity,
            ActorId = "someone-else", // tampered actor
            ActorType = original.ActorType,
            TargetId = original.TargetId,
            TargetType = original.TargetType,
            Metadata = original.Metadata,
            OccurredAt = original.OccurredAt,
            RetainUntil = original.RetainUntil,
            IntegrityHash = original.IntegrityHash,
        };

        DefaultAuditService.VerifyIntegrity(tampered).Should().BeFalse();
    }

    // ─── Characterization: pre-existing (v1) rows still verify unchanged (fix 4) ───────────

    [Fact]
    public void VerifyIntegrity_ShouldStillVerify_WhenPreExistingV1RowHasNoRetainUntilCoverage()
    {
        // GIVEN an audit entry written BEFORE the hash-scheme change (bare-hex v1 hash, computed
        // WITHOUT RetainUntil — exactly what DefaultAuditService produced prior to fix 4).
        var v1Hash = DefaultAuditService.ComputeIntegrityHash(
            Tenant, "user", "user-1", "typification.autonomous.corrected",
            "Conversation", "conv-1", OccurredAt, metadata: null);

        var legacyEntry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant,
            Action = "typification.autonomous.corrected",
            Category = "conversations",
            Severity = "info",
            ActorId = "user-1",
            ActorType = "user",
            TargetId = "conv-1",
            TargetType = "Conversation",
            OccurredAt = OccurredAt,
            RetainUntil = OccurredAt.AddDays(90), // the row HAS a RetainUntil, but v1 never hashed it
            IntegrityHash = v1Hash,
        };

        // WHEN hash verification runs
        // THEN the entry verifies under its original (v1) scheme — no mass invalidation, even
        // though its RetainUntil is populated and would change a v2 hash.
        DefaultAuditService.VerifyIntegrity(legacyEntry).Should().BeTrue();
    }

    [Fact]
    public void VerifyIntegrity_ShouldReturnFalse_WhenPreExistingV1RowTampered()
    {
        // A v1 row is STILL tamper-evident for the fields v1 covers (actor, action, target, etc.)
        // — the fix doesn't weaken v1 verification, it only adds v2 coverage for new writes.
        var v1Hash = DefaultAuditService.ComputeIntegrityHash(
            Tenant, "user", "user-1", "action", "Conversation", "conv-1", OccurredAt, metadata: null);

        var tampered = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant,
            Action = "action",
            Category = "conversations",
            Severity = "info",
            ActorId = "tampered-actor",
            ActorType = "user",
            TargetId = "conv-1",
            TargetType = "Conversation",
            OccurredAt = OccurredAt,
            IntegrityHash = v1Hash,
        };

        DefaultAuditService.VerifyIntegrity(tampered).Should().BeFalse();
    }

    [Fact]
    public void VerifyIntegrity_ShouldReturnFalse_WhenIntegrityHashIsNullOrEmpty()
    {
        var noHash = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant,
            Action = "action",
            Category = "conversations",
            Severity = "info",
            ActorId = "user-1",
            ActorType = "user",
            OccurredAt = OccurredAt,
            IntegrityHash = null,
        };

        DefaultAuditService.VerifyIntegrity(noHash).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ShouldWriteV2Hash_CoveringRetainUntil()
    {
        var captured = new List<AuditEntry>();
        var store = Substitute.For<IAuditStore>();
        store.SaveAsync(Arg.Do<AuditEntry>(e => captured.Add(e)), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(OccurredAt);
        var service = new DefaultAuditService(store, clock);

        var retainUntil = OccurredAt.AddDays(90);
        await service.RecordAsync(
            Tenant, "conversations", "typification.autonomous.corrected", "info",
            actorId: "user-1", actorType: "user",
            targetId: "conv-1", targetType: "Conversation",
            retainUntil: retainUntil,
            ct: CancellationToken.None);

        captured.Should().ContainSingle();
        var entry = captured[0];
        entry.RetainUntil.Should().Be(retainUntil);
        DefaultAuditService.VerifyIntegrity(entry).Should().BeTrue();

        // A record with the SAME fields but a DIFFERENT RetainUntil must hash differently —
        // proving RetainUntil actually participates in the v2 hash (not just "some hash present").
        var otherHash = DefaultAuditService.ComputeIntegrityHashV2(
            Tenant, "user", "user-1", "typification.autonomous.corrected",
            "Conversation", "conv-1", OccurredAt, retainUntil.AddDays(1), metadata: null);
        entry.IntegrityHash.Should().NotBe(otherHash);
    }
}
