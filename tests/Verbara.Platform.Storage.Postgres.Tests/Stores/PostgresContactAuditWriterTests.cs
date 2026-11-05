using Npgsql;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// contacts-writes-follow-their-permissions, task 2.6 (design D17): <see cref="PostgresContactAuditWriter"/> writes a
/// contact change and its audit record in one transaction on one connection. When the audit insert fails, the contact
/// write fails, or the call is cancelled between the two statements, neither persists; a retry with a working input then
/// applies once and writes one record. Live-DB (Testcontainers).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresContactAuditWriterTests : IClassFixture<ContactStoreFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 30, 0, TimeSpan.Zero);

    private readonly ContactStoreFixture _fixture;
    private readonly PostgresContactStore _contacts;
    private readonly PostgresAuditStore _audit;
    private readonly PostgresContactAuditWriter _writer;
    private readonly TenantId _tenant = new($"t-{Guid.NewGuid():N}");

    public PostgresContactAuditWriterTests(ContactStoreFixture fixture)
    {
        _fixture = fixture;
        _contacts = new PostgresContactStore(fixture.DataSource);
        _audit = new PostgresAuditStore(fixture.DataSource);
        _writer = new PostgresContactAuditWriter(fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Contact> SeedAsync()
    {
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = _tenant,
            FirstName = "Ana",
            CreatedAt = Now.AddDays(-1),
        };
        contact.AddAddress(new ChannelAddress(ChannelType.Voice, "+15551234567"));
        contact.SetConsent(ChannelType.Voice, true);
        await _contacts.SaveAsync(contact, CancellationToken.None);
        return contact;
    }

    private async Task<Contact> LoadAsync(Contact contact) =>
        (await _contacts.GetByIdAsync(_tenant, contact.ContactId, CancellationToken.None))!;

    /// <summary>The stored contact marked do-not-contact with Sms recorded no, as the opt-out leaves it.</summary>
    private async Task<Contact> OptedOutAsync(Contact contact, string? firstName = null)
    {
        var changed = await LoadAsync(contact);
        changed.DoNotContact = true;
        changed.SetConsent(ChannelType.Sms, false);
        changed.UpdatedAt = Now;
        changed.UpdatedBy = "u-agent-0042";
        if (firstName is not null)
            changed.FirstName = firstName;
        return changed;
    }

    private AuditEntry Entry(Contact contact, string action = "contact.opted_out", EntityId? entryId = null) => new()
    {
        EntryId = entryId ?? EntityId.New(),
        TenantId = _tenant,
        Action = action,
        Category = "contact",
        Severity = "info",
        ActorId = "u-agent-0042",
        ActorType = "user",
        TargetId = contact.ContactId.Value,
        TargetType = "contact",
        Changes = new AuditChanges(
            """{"doNotContact":false,"channelConsent":{"Voice":true}}""",
            """{"doNotContact":true,"channelConsent":{"Voice":true,"Sms":false}}"""),
        OccurredAt = Now,
        RetainUntil = Now.AddYears(5),
    };

    private async Task<IReadOnlyList<AuditEntry>> RecordsOfAsync(Contact contact) =>
        await _audit.GetByEntityAsync(_tenant, "contact", contact.ContactId.Value, CancellationToken.None);

    private async Task ShouldBeUnchangedWithNoRecordAsync(Contact contact)
    {
        var stored = await LoadAsync(contact);
        stored.DoNotContact.Should().BeFalse("the contact change rolled back with its record");
        stored.ChannelConsent.Should().NotContainKey(ChannelType.Sms);
        stored.UpdatedBy.Should().BeNull();
        (await RecordsOfAsync(contact)).Should().BeEmpty("the record rolled back with the contact change");
    }

    private async Task ShouldBeAppliedOnceAsync(Contact contact, string action = "contact.opted_out")
    {
        var stored = await LoadAsync(contact);
        stored.DoNotContact.Should().BeTrue();
        stored.ChannelConsent.Should().Contain(ChannelType.Sms, false);
        stored.UpdatedBy.Should().Be("u-agent-0042");
        var record = (await RecordsOfAsync(contact)).Should().ContainSingle().Subject;
        record.Action.Should().Be(action);
        record.Category.Should().Be("contact");
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistTheContactAndItsRecordTogether_WhenBothWritesSucceed()
    {
        var contact = await SeedAsync();

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
        var record = (await RecordsOfAsync(contact)).Single();
        record.ActorId.Should().Be("u-agent-0042");
        record.ActorType.Should().Be("user");
        record.RetainUntil.Should().Be(Now.AddYears(5));
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistNeitherThenApplyOnceOnRetry_WhenTheAuditInsertFails()
    {
        var contact = await SeedAsync();
        var taken = EntityId.New();
        var other = await SeedAsync();
        await _audit.SaveAsync(Entry(other, entryId: taken), CancellationToken.None);

        var act = async () => await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact, entryId: taken), CancellationToken.None);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        await ShouldBeUnchangedWithNoRecordAsync(contact);

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistNeitherThenApplyOnceOnRetry_WhenTheContactWriteFails()
    {
        var contact = await SeedAsync();

        // Postgres refuses a NUL character in text, so the contact statement fails inside the transaction.
        var act = async () => await _writer.SaveAsync(await OptedOutAsync(contact, firstName: "An\0a"), Entry(contact), CancellationToken.None);

        await act.Should().ThrowAsync<PostgresException>();
        await ShouldBeUnchangedWithNoRecordAsync(contact);

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistNeitherThenApplyOnceOnRetry_WhenCancelledBetweenTheTwoWrites()
    {
        var contact = await SeedAsync();
        using var cancel = new CancellationTokenSource();
        var hooked = new PostgresContactAuditWriter(_fixture.DataSource, afterContactStatement: cancel.Cancel);

        var act = async () => await hooked.SaveAsync(await OptedOutAsync(contact), Entry(contact), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await ShouldBeUnchangedWithNoRecordAsync(contact);

        await _writer.SaveAsync(await OptedOutAsync(contact), Entry(contact), CancellationToken.None);

        await ShouldBeAppliedOnceAsync(contact);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheContactAndWriteItsRecordTogether_WhenTheContactExists()
    {
        var contact = await SeedAsync();

        var deleted = await _writer.DeleteAsync(_tenant, contact.ContactId, Entry(contact, "contact.deleted"), CancellationToken.None);

        deleted.Should().BeTrue();
        (await _contacts.GetByIdAsync(_tenant, contact.ContactId, CancellationToken.None)).Should().BeNull();
        (await RecordsOfAsync(contact)).Should().ContainSingle().Which.Action.Should().Be("contact.deleted");
    }

    [Fact]
    public async Task DeleteAsync_ShouldKeepTheContactThenDeleteOnceOnRetry_WhenTheAuditInsertFails()
    {
        var contact = await SeedAsync();
        var taken = EntityId.New();
        await _audit.SaveAsync(Entry(await SeedAsync(), entryId: taken), CancellationToken.None);

        var act = () => _writer.DeleteAsync(_tenant, contact.ContactId, Entry(contact, "contact.deleted", taken), CancellationToken.None);

        await act.Should().ThrowAsync<PostgresException>();
        (await _contacts.GetByIdAsync(_tenant, contact.ContactId, CancellationToken.None)).Should().NotBeNull();
        (await RecordsOfAsync(contact)).Should().BeEmpty();

        (await _writer.DeleteAsync(_tenant, contact.ContactId, Entry(contact, "contact.deleted"), CancellationToken.None))
            .Should().BeTrue();
        (await _contacts.GetByIdAsync(_tenant, contact.ContactId, CancellationToken.None)).Should().BeNull();
        (await RecordsOfAsync(contact)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteAsync_ShouldKeepTheContactAndWriteNoRecord_WhenCancelledBetweenTheTwoWrites()
    {
        var contact = await SeedAsync();
        using var cancel = new CancellationTokenSource();
        var hooked = new PostgresContactAuditWriter(_fixture.DataSource, afterContactStatement: cancel.Cancel);

        var act = () => hooked.DeleteAsync(_tenant, contact.ContactId, Entry(contact, "contact.deleted"), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _contacts.GetByIdAsync(_tenant, contact.ContactId, CancellationToken.None)).Should().NotBeNull();
        (await RecordsOfAsync(contact)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_ShouldWriteNoRecord_WhenNoContactHasTheId()
    {
        var ghost = new Contact { ContactId = EntityId.New(), TenantId = _tenant, CreatedAt = Now };

        var deleted = await _writer.DeleteAsync(_tenant, ghost.ContactId, Entry(ghost, "contact.deleted"), CancellationToken.None);

        deleted.Should().BeFalse("nothing was deleted, so nothing is recorded");
        (await RecordsOfAsync(ghost)).Should().BeEmpty();
    }
}
