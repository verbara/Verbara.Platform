using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// contacts-writes-follow-their-permissions, task 2.6 (design D17): <see cref="InMemoryContactAuditWriter"/> keeps a
/// contact change and its audit record consistent as the Postgres transaction does. When the record fails, the contact
/// write fails, or the call is cancelled between the two writes, neither persists; a retry then applies once with one
/// record. The same three cases as <c>PostgresContactAuditWriterTests</c>.
/// </summary>
public sealed class InMemoryContactAuditWriterTests
{
    private static readonly TenantId Tenant = new("tenant-contact-audit");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 30, 0, TimeSpan.Zero);

    private readonly FaultingContactStore _contacts = new(new InMemoryContactStore());
    private readonly FaultingAuditStore _audit = new(new InMemoryAuditStore());
    private readonly InMemoryContactAuditWriter _writer;

    public InMemoryContactAuditWriterTests() => _writer = new InMemoryContactAuditWriter(_contacts, _audit);

    private async Task<Contact> SeedAsync()
    {
        var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, FirstName = "Ana", CreatedAt = Now.AddDays(-1) };
        contact.AddAddress(new ChannelAddress(ChannelType.Voice, "+15551234567"));
        contact.SetConsent(ChannelType.Voice, true);
        await _contacts.SaveAsync(contact, CancellationToken.None);
        return contact;
    }

    private async Task<Contact?> LoadAsync(Contact contact) =>
        await _contacts.GetByIdAsync(Tenant, contact.ContactId, CancellationToken.None);

    private async Task<Contact> OptedOutAsync(Contact contact)
    {
        var changed = (await LoadAsync(contact))!;
        changed.DoNotContact = true;
        changed.SetConsent(ChannelType.Sms, false);
        changed.UpdatedAt = Now;
        changed.UpdatedBy = "u-agent-0042";
        return changed;
    }

    private static AuditEntry Entry(Contact contact, string action = "contact.opted_out") => new()
    {
        EntryId = EntityId.New(),
        TenantId = Tenant,
        Action = action,
        Category = "contact",
        Severity = "info",
        ActorId = "u-agent-0042",
        ActorType = "user",
        TargetId = contact.ContactId.Value,
        TargetType = "contact",
        OccurredAt = Now,
        RetainUntil = Now.AddYears(5),
    };

    private async Task<IReadOnlyList<AuditEntry>> RecordsOfAsync(Contact contact) =>
        await _audit.GetByEntityAsync(Tenant, "contact", contact.ContactId.Value, CancellationToken.None);

    private async Task ShouldBeUnchangedWithNoRecordAsync(Contact contact)
    {
        var stored = (await LoadAsync(contact))!;
        stored.DoNotContact.Should().BeFalse("the contact change is undone with its record");
        stored.ChannelConsent.Should().NotContainKey(ChannelType.Sms);
        stored.UpdatedBy.Should().BeNull();
        (await RecordsOfAsync(contact)).Should().BeEmpty();
    }

    private async Task ShouldBeAppliedOnceAsync(Contact contact)
    {
        var stored = (await LoadAsync(contact))!;
        stored.DoNotContact.Should().BeTrue();
        stored.ChannelConsent.Should().Contain(ChannelType.Sms, false);
        (await RecordsOfAsync(contact)).Should().ContainSingle();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldAnswerACopy_SoAChangeBeforeTheWriteLeavesTheStoredContact()
    {
        var contact = await SeedAsync();

        var read = (await LoadAsync(contact))!;
        read.DoNotContact = true;
        read.SetConsent(ChannelType.Sms, false);

        var stored = (await LoadAsync(contact))!;
        stored.DoNotContact.Should().BeFalse("a read is a snapshot, as on Postgres; only a write changes the store");
        stored.ChannelConsent.Should().NotContainKey(ChannelType.Sms);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistTheContactAndItsRecordTogether_WhenBothWritesSucceed()
    {
        var contact = await SeedAsync();

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistNeitherThenApplyOnceOnRetry_WhenTheAuditInsertFails()
    {
        var contact = await SeedAsync();
        _audit.FailNextSave = true;

        var act = async () => await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await act.Should().ThrowAsync<InjectedFaultException>();
        await ShouldBeUnchangedWithNoRecordAsync(contact);

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistNeitherThenApplyOnceOnRetry_WhenTheContactWriteFails()
    {
        var contact = await SeedAsync();
        _contacts.FailNextWrite = true;

        var act = async () => await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await act.Should().ThrowAsync<InjectedFaultException>();
        await ShouldBeUnchangedWithNoRecordAsync(contact);

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistNeitherThenApplyOnceOnRetry_WhenCancelledBetweenTheTwoWrites()
    {
        var contact = await SeedAsync();
        using var cancel = new CancellationTokenSource();
        _contacts.AfterNextWrite = cancel.Cancel;

        var act = async () => await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await ShouldBeUnchangedWithNoRecordAsync(contact);

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
    }

    [Fact]
    public async Task SaveAsync_ShouldLeaveNoContact_WhenTheRecordOfANewContactFails()
    {
        var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = Now };
        _audit.FailNextSave = true;

        var act = () => _writer.SaveAsync(contact, Entry(contact), CancellationToken.None);

        await act.Should().ThrowAsync<InjectedFaultException>();
        (await LoadAsync(contact)).Should().BeNull("a contact that did not exist before the write does not exist after it");
    }

    [Fact]
    public async Task DeleteAsync_ShouldKeepTheContactThenDeleteOnceOnRetry_WhenTheAuditInsertFails()
    {
        var contact = await SeedAsync();
        _audit.FailNextSave = true;

        var act = () => _writer.DeleteAsync(Tenant, contact.ContactId, Entry(contact, "contact.deleted"), CancellationToken.None);

        await act.Should().ThrowAsync<InjectedFaultException>();
        (await LoadAsync(contact)).Should().NotBeNull();
        (await RecordsOfAsync(contact)).Should().BeEmpty();

        (await _writer.DeleteAsync(Tenant, contact.ContactId, Entry(contact, "contact.deleted"), CancellationToken.None))
            .Should().BeTrue();
        (await LoadAsync(contact)).Should().BeNull();
        (await RecordsOfAsync(contact)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteAsync_ShouldKeepTheContactAndWriteNoRecord_WhenTheContactWriteFails()
    {
        var contact = await SeedAsync();
        _contacts.FailNextWrite = true;

        var act = () => _writer.DeleteAsync(Tenant, contact.ContactId, Entry(contact, "contact.deleted"), CancellationToken.None);

        await act.Should().ThrowAsync<InjectedFaultException>();
        (await LoadAsync(contact)).Should().NotBeNull();
        (await RecordsOfAsync(contact)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_ShouldKeepTheContactAndWriteNoRecord_WhenCancelledBetweenTheTwoWrites()
    {
        var contact = await SeedAsync();
        using var cancel = new CancellationTokenSource();
        _contacts.AfterNextWrite = cancel.Cancel;

        var act = () => _writer.DeleteAsync(Tenant, contact.ContactId, Entry(contact, "contact.deleted"), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await LoadAsync(contact)).Should().NotBeNull();
        (await RecordsOfAsync(contact)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_ShouldWriteNoRecord_WhenNoContactHasTheId()
    {
        var ghost = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = Now };

        var deleted = await _writer.DeleteAsync(Tenant, ghost.ContactId, Entry(ghost, "contact.deleted"), CancellationToken.None);

        deleted.Should().BeFalse();
        (await RecordsOfAsync(ghost)).Should().BeEmpty();
    }

    private sealed class InjectedFaultException() : Exception("Injected fault");

    /// <summary>The real audit store, whose next save can be made to fail once.</summary>
    private sealed class FaultingAuditStore(IAuditStore inner) : IAuditStore
    {
        public bool FailNextSave { get; set; }

        public Task SaveAsync(AuditEntry entry, CancellationToken ct)
        {
            if (!FailNextSave)
                return inner.SaveAsync(entry, ct);
            FailNextSave = false;
            return Task.FromException(new InjectedFaultException());
        }

        public Task<long> CountByActorAsync(TenantId tenantId, string actorId, CancellationToken ct) =>
            inner.CountByActorAsync(tenantId, actorId, ct);

        public Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(TenantId tenantId, string entityType, string entityId, CancellationToken ct) =>
            inner.GetByEntityAsync(tenantId, entityType, entityId, ct);

        public Task<PagedResult<AuditEntry>> SearchAsync(TenantId tenantId, AuditQuery query, CancellationToken ct) =>
            inner.SearchAsync(tenantId, query, ct);

        public IAsyncEnumerable<AuditEntry> StreamAsync(TenantId tenantId, AuditQuery query, CancellationToken ct) =>
            inner.StreamAsync(tenantId, query, ct);

        public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct) =>
            inner.DeleteOlderThanAsync(tenantId, cutoff, ct);

        public Task<int> RedactContactLinkageAsync(TenantId tenantId, IReadOnlyCollection<string> conversationIds, CancellationToken ct) =>
            inner.RedactContactLinkageAsync(tenantId, conversationIds, ct);
    }

    /// <summary>
    /// The real contact store, whose next save or delete can be made to fail once, or to run a step right after it
    /// (a cancellation between the contact write and the record).
    /// </summary>
    private sealed class FaultingContactStore(IContactStore inner) : IContactStore
    {
        public bool FailNextWrite { get; set; }

        public Action? AfterNextWrite { get; set; }

        private async Task WriteAsync(Func<Task> write)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new InjectedFaultException();
            }

            await write();
            var after = AfterNextWrite;
            AfterNextWrite = null;
            after?.Invoke();
        }

        public Task SaveAsync(Contact contact, CancellationToken ct) => WriteAsync(() => inner.SaveAsync(contact, ct));

        public Task DeleteAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
            WriteAsync(() => inner.DeleteAsync(tenantId, contactId, ct));

        public Task<Contact?> GetByIdAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
            inner.GetByIdAsync(tenantId, contactId, ct);

        public Task<Contact?> FindByAddressAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct) =>
            inner.FindByAddressAsync(tenantId, address, ct);

        public Task<IReadOnlyList<Contact>> FindAllByVoiceNumberAsync(TenantId tenantId, string canonicalNumber, int limit, CancellationToken ct) =>
            inner.FindAllByVoiceNumberAsync(tenantId, canonicalNumber, limit, ct);

        public Task<PagedResult<Contact>> SearchAsync(TenantId tenantId, string? searchTerm, PagedQuery query, CancellationToken ct) =>
            inner.SearchAsync(tenantId, searchTerm, query, ct);

        public Task<Contact?> AddIdentityAsync(
            TenantId tenantId, EntityId contactId, IReadOnlyList<ChannelAddress> addresses, string? firstNameIfUnnamed,
            DateTimeOffset updatedAt, string updatedBy, CancellationToken ct) =>
            inner.AddIdentityAsync(tenantId, contactId, addresses, firstNameIfUnnamed, updatedAt, updatedBy, ct);

        public Task<Contact> FindOrInsertByAddressAsync(TenantId tenantId, ChannelAddress key, Contact newContact, CancellationToken ct) =>
            inner.FindOrInsertByAddressAsync(tenantId, key, newContact, ct);

        public Task<Contact?> AttachWebChatSessionAsync(
            TenantId tenantId, EntityId contactId, ChannelAddress sessionAddress, string? firstNameIfUnnamed,
            DateTimeOffset updatedAt, string updatedBy, CancellationToken ct) =>
            inner.AttachWebChatSessionAsync(tenantId, contactId, sessionAddress, firstNameIfUnnamed, updatedAt, updatedBy, ct);

        public Task<Contact?> SetCustomFieldIfAbsentAsync(
            TenantId tenantId, EntityId contactId, string key, string value, DateTimeOffset updatedAt, string updatedBy,
            CancellationToken ct) =>
            inner.SetCustomFieldIfAbsentAsync(tenantId, contactId, key, value, updatedAt, updatedBy, ct);
    }
}
