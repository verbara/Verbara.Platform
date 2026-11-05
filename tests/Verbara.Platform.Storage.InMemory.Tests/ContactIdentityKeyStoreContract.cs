using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// The targeted contact writes of a WebChat session create: the find-or-insert of an identity key, the attach of a
/// session address with its bound, the e-mail written only while absent, and the search by that e-mail. Run against
/// every store.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class ContactIdentityKeyStoreContract
{
    private const string KeyValue = "webchat-visitor:rcfqwP4uUDSA6hgRqgUIE3hCFFsXvDuxhE3MgUu0Y1M";
    private const string EmailField = "webchatEmail";
    private const string Actor = "system";

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChannelAddress Key = new(ChannelType.WebChat, KeyValue);

    /// <summary>The store under test, empty for <see cref="Tenant"/>.</summary>
    protected abstract IContactStore Store { get; }

    protected abstract TenantId Tenant { get; }

    private static ChannelAddress Session(int i) => new(ChannelType.WebChat, $"webchat-{i:x32}");

    private Contact NewContact(params ChannelAddress[] addresses)
    {
        var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = Now.AddDays(-1) };
        foreach (var address in addresses)
            contact.AddAddress(address);
        return contact;
    }

    private async Task<Contact> SeedAsync(params ChannelAddress[] addresses)
    {
        var contact = NewContact(addresses);
        await Store.SaveAsync(contact, CancellationToken.None);
        return contact;
    }

    private Task<Contact?> AttachAsync(EntityId contactId, ChannelAddress session, string? name = null) =>
        Store.AttachWebChatSessionAsync(Tenant, contactId, session, name, Now, Actor, CancellationToken.None);

    private Task<Contact?> SetEmailAsync(EntityId contactId, string value) =>
        Store.SetCustomFieldIfAbsentAsync(Tenant, contactId, EmailField, value, Now, Actor, CancellationToken.None);

    private async Task<Contact> ReadAsync(EntityId contactId) =>
        (await Store.GetByIdAsync(Tenant, contactId, CancellationToken.None))!;

    private async Task<IReadOnlyList<Contact>> AllAsync() =>
        (await Store.SearchAsync(Tenant, null, new PagedQuery(1, 50), CancellationToken.None)).Items;

    // A whole-row save of what the caller read: what an agent's edit writes.
    private async Task EditLastNameAsync(Contact read, string lastName)
    {
        var edited = new Contact { ContactId = read.ContactId, TenantId = read.TenantId, CreatedAt = read.CreatedAt, LastName = lastName };
        foreach (var address in read.Addresses)
            edited.AddAddress(address);
        await Store.SaveAsync(edited, CancellationToken.None);
    }

    [Fact]
    public async Task FindOrInsertByAddressAsync_ShouldInsertTheNewContact_WhenNoContactHoldsTheKey()
    {
        var candidate = NewContact(Key, Session(1));
        candidate.FirstName = "Ana";
        candidate.SetCustomField(EmailField, "ana@example.com");

        var result = await Store.FindOrInsertByAddressAsync(Tenant, Key, candidate, CancellationToken.None);

        result.ContactId.Should().Be(candidate.ContactId);
        var stored = await ReadAsync(candidate.ContactId);
        stored.Addresses.Should().Equal([Key, Session(1)]);
        stored.FirstName.Should().Be("Ana");
        stored.CustomFields.Should().Contain(EmailField, "ana@example.com");
    }

    [Fact]
    public async Task FindOrInsertByAddressAsync_ShouldReturnTheHolderAndInsertNothing_WhenAContactHoldsTheKey()
    {
        var holder = await SeedAsync(Key, Session(1));

        var result = await Store.FindOrInsertByAddressAsync(Tenant, Key, NewContact(Key, Session(2)), CancellationToken.None);

        result.ContactId.Should().Be(holder.ContactId);
        (await AllAsync()).Should().ContainSingle();
        (await ReadAsync(holder.ContactId)).Addresses.Should().Equal([Key, Session(1)], "the find writes nothing");
    }

    [Fact]
    public async Task FindOrInsertByAddressAsync_ShouldInsert_WhenAnotherChannelHoldsTheSameText()
    {
        await SeedAsync(new ChannelAddress(ChannelType.WhatsApp, KeyValue));
        var candidate = NewContact(Key);

        var result = await Store.FindOrInsertByAddressAsync(Tenant, Key, candidate, CancellationToken.None);

        result.ContactId.Should().Be(candidate.ContactId);
    }

    [Fact]
    public async Task FindOrInsertByAddressAsync_ShouldResolveOneContact_WhenCallersRunConcurrently()
    {
        var calls = Enumerable.Range(1, 8).Select(i => Task.Run(() =>
            Store.FindOrInsertByAddressAsync(Tenant, Key, NewContact(Key, Session(i)), CancellationToken.None)));

        var results = await Task.WhenAll(calls);

        results.Select(c => c.ContactId).Distinct().Should().ContainSingle();
        (await AllAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldAppendAndFillTheName_WhenContactHasNone()
    {
        var contact = await SeedAsync(Key, Session(1));

        var result = await AttachAsync(contact.ContactId, Session(2), "Ana");

        result.Should().NotBeNull();
        result!.Addresses.Should().Equal([Key, Session(1), Session(2)]);
        result.FirstName.Should().Be("Ana");
        result.UpdatedBy.Should().Be(Actor);
        (await ReadAsync(contact.ContactId)).Addresses.Should().Equal([Key, Session(1), Session(2)]);
    }

    [Theory]
    [InlineData("Primero", null)]
    [InlineData(null, "Apellido")]
    public async Task AttachWebChatSessionAsync_ShouldKeepTheName_WhenContactHasOne(string? firstName, string? lastName)
    {
        var contact = NewContact(Key);
        contact.FirstName = firstName;
        contact.LastName = lastName;
        await Store.SaveAsync(contact, CancellationToken.None);

        var result = await AttachAsync(contact.ContactId, Session(1), "Ana");

        result!.FirstName.Should().Be(firstName);
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldDropOnlyTheOldestSessionAddress_WhenTheContactHoldsFifty()
    {
        var phone = new ChannelAddress(ChannelType.WhatsApp, "15550000002");
        var waLookalike = new ChannelAddress(ChannelType.WhatsApp, $"webchat-{1000:x32}");
        var mixedCase = new ChannelAddress(ChannelType.WebChat, "WebChat-External:x");
        var legacy = new ChannelAddress(ChannelType.WebChat, "webchat-short");
        var contact = await SeedAsync([Key, phone, waLookalike, mixedCase, legacy, .. Enumerable.Range(1, 50).Select(Session)]);

        var result = await AttachAsync(contact.ContactId, Session(51));

        ChannelAddress[] expected = [Key, phone, waLookalike, mixedCase, legacy, .. Enumerable.Range(2, 50).Select(Session)];
        result!.Addresses.Should().Equal(expected, "the oldest session address goes, and nothing that is not a session address does");
        (await ReadAsync(contact.ContactId)).Addresses.Should().Equal(expected);
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldDropEveryAddressBeyondTheBound_WhenTheContactHoldsMoreThanFifty()
    {
        var contact = await SeedAsync([Key, .. Enumerable.Range(1, 55).Select(Session)]);

        var result = await AttachAsync(contact.ContactId, Session(56));

        result!.Addresses.Should().Equal([Key, .. Enumerable.Range(7, 50).Select(Session)]);
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldReturnNullAndInsertNothing_WhenTheContactIsGone()
    {
        var result = await AttachAsync(EntityId.New(), Session(1), "Ana");

        result.Should().BeNull();
        (await AllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldKeepAnEdit_WhenItLandedAfterTheCallerRead()
    {
        var read = await SeedAsync(Key);
        await EditLastNameAsync(read, "Editada");

        await AttachAsync(read.ContactId, Session(1));

        (await ReadAsync(read.ContactId)).LastName.Should().Be("Editada");
    }

    [Fact]
    public async Task AttachWebChatSessionAsync_ShouldNotChangeWhatACallerAlreadyRead_WhenItWrites()
    {
        var read = await SeedAsync(Key);
        var before = await ReadAsync(read.ContactId);

        await AttachAsync(read.ContactId, Session(1));

        before.Addresses.Should().Equal([Key], "a read is a snapshot, on every store");
    }

    [Fact]
    public async Task SetCustomFieldIfAbsentAsync_ShouldWriteTheField_WhenItIsAbsent()
    {
        var contact = await SeedAsync(Key);

        var result = await SetEmailAsync(contact.ContactId, "ana@example.com");

        result!.CustomFields.Should().Contain(EmailField, "ana@example.com");
        result.UpdatedBy.Should().Be(Actor);
        (await ReadAsync(contact.ContactId)).CustomFields.Should().Contain(EmailField, "ana@example.com");
    }

    [Fact]
    public async Task SetCustomFieldIfAbsentAsync_ShouldWriteNothingAndReturnNull_WhenTheFieldIsSet()
    {
        var contact = await SeedAsync(Key);
        await SetEmailAsync(contact.ContactId, "ana@example.com");
        var updatedAt = (await ReadAsync(contact.ContactId)).UpdatedAt;

        var result = await Store.SetCustomFieldIfAbsentAsync(
            Tenant, contact.ContactId, EmailField, "other@example.com", Now.AddHours(1), "other", CancellationToken.None);

        result.Should().BeNull();
        var stored = await ReadAsync(contact.ContactId);
        stored.CustomFields.Should().Contain(EmailField, "ana@example.com");
        stored.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public async Task SetCustomFieldIfAbsentAsync_ShouldReturnNullAndInsertNothing_WhenTheContactIsGone()
    {
        (await SetEmailAsync(EntityId.New(), "ana@example.com")).Should().BeNull();
        (await AllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SetCustomFieldIfAbsentAsync_ShouldKeepAnEdit_WhenItLandedAfterTheCallerRead()
    {
        var read = await SeedAsync(Key);
        await EditLastNameAsync(read, "Editada");

        await SetEmailAsync(read.ContactId, "ana@example.com");

        var stored = await ReadAsync(read.ContactId);
        stored.LastName.Should().Be("Editada");
        stored.CustomFields.Should().Contain(EmailField, "ana@example.com");
    }

    [Fact]
    public async Task SearchAsync_ShouldMatchTheWebChatEmail_WhenTheTermIsPartOfIt()
    {
        var withEmail = await SeedAsync(Session(1));
        await SetEmailAsync(withEmail.ContactId, "Ana@Example.com");
        var other = NewContact(Session(2));
        other.SetCustomField("note", "ana@example.com");
        await Store.SaveAsync(other, CancellationToken.None);

        var page = await Store.SearchAsync(Tenant, "ana@example", new PagedQuery(1, 50), CancellationToken.None);

        page.Items.Select(c => c.ContactId).Should().Equal([withEmail.ContactId], "only this custom field is searched");
        page.TotalCount.Should().Be(1);
    }
}
