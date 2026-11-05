using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// contacts-writes-follow-their-permissions task 1.6 (design D10; third spec audit F1, F2; fourth spec audit F3):
/// <see cref="IContactStore.FindAllByVoiceNumberAsync"/> returns every contact of the tenant holding a Voice address
/// whose canonical form (<see cref="VoiceNumber.Canonical"/>) equals the number, restricted ones first, bounded, and
/// writes nothing. One shared table of stored and dialed forms runs against every store, so the stores agree.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class VoiceNumberMatchStoreContract
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The store under test.</summary>
    protected abstract IContactStore Store { get; }

    /// <summary>A tenant no other test writes to.</summary>
    protected abstract TenantId Tenant { get; }

    /// <summary>
    /// The shared table: a stored Voice address, a dialed number (reduced by <see cref="VoiceNumber.Canonical"/> before
    /// the lookup), and whether the stored contact matches. It holds fixtures/do-not-contact-refusals.v1.json
    /// <c>dialStoredFormattedAddress</c> and <c>dialCanonicalFormResidual</c>, and the blank and digitless parity cases.
    /// </summary>
    public static TheoryData<string, string, bool> Table() => new()
    {
        { "+1 (555) 123-4567", "+15551234567", true },
        { "+1 (555) 123-4567", "+1 555-123-4567", true },
        { "15551234567", "15551234567", true },
        { "15551234567", "1 (555) 123-4567", true },
        { "15551234567", "+15551234567", false },
        { "+1 (555) 123-4567", "15551234567", false },
        { " +15551234567", "15551234567", true },
        { " +15551234567", "+15551234567", false },
        { "\t+15551234567", "+15551234567", true },
        { "\r\n+15551234567", "+15551234567", true },
        { " +15551234567", "+15551234567", true },
    };

    protected Contact NewContact(string voiceAddress, bool doNotContact = false, bool? voiceConsent = null, TenantId? tenant = null)
    {
        var contact = new Contact { ContactId = EntityId.New(), TenantId = tenant ?? Tenant, CreatedAt = Now };
        contact.AddAddress(new ChannelAddress(ChannelType.Voice, voiceAddress));
        contact.DoNotContact = doNotContact;
        if (voiceConsent is { } consent)
            contact.SetConsent(ChannelType.Voice, consent);
        return contact;
    }

    protected async Task<Contact> SeedAsync(Contact contact)
    {
        await Store.SaveAsync(contact, CancellationToken.None);
        return contact;
    }

    protected Task<IReadOnlyList<Contact>> FindAsync(string canonicalNumber, int limit = 50) =>
        Store.FindAllByVoiceNumberAsync(Tenant, canonicalNumber, limit, CancellationToken.None);

    private static string[] Ids(IEnumerable<Contact> contacts) => [.. contacts.Select(c => c.ContactId.Value)];

    [Theory]
    [MemberData(nameof(Table))]
    public async Task FindAllByVoiceNumber_ShouldMatchTheCanonicalForm_WhenGivenTheSharedTable(string stored, string dialed, bool matches)
    {
        var contact = await SeedAsync(NewContact(stored));

        var found = await FindAsync(VoiceNumber.Canonical(dialed));

        if (matches)
            Ids(found).Should().Equal(contact.ContactId.Value);
        else
            found.Should().BeEmpty();
    }

    [Theory]
    [InlineData("+")]
    [InlineData("+abc")]
    [InlineData("anonymous")]
    public async Task FindAllByVoiceNumber_ShouldMatchNothing_WhenTheStoredAddressHasNoDigit(string stored)
    {
        await SeedAsync(NewContact(stored, doNotContact: true));

        (await FindAsync("+")).Should().BeEmpty("a digitless address reduces to the empty string, never to a lone plus");
        (await FindAsync(string.Empty)).Should().BeEmpty("a digitless address matches nothing");
    }

    [Fact]
    public async Task FindAllByVoiceNumber_ShouldIgnoreOtherTenantsAndOtherChannels_WhenTheDigitsAreTheSame()
    {
        await SeedAsync(NewContact("+15551234567", doNotContact: true, tenant: new TenantId($"other-{Guid.NewGuid():N}")));
        var sms = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = Now, DoNotContact = true };
        sms.AddAddress(new ChannelAddress(ChannelType.Sms, "+15551234567"));
        await SeedAsync(sms);

        (await FindAsync("+15551234567")).Should().BeEmpty();
    }

    [Fact]
    public async Task FindAllByVoiceNumber_ShouldReturnEveryHolder_WhenSeveralContactsHoldTheNumber()
    {
        var a = await SeedAsync(NewContact("+15551234567"));
        var b = await SeedAsync(NewContact("+1 (555) 123-4567"));

        Ids(await FindAsync("+15551234567")).Should().BeEquivalentTo([a.ContactId.Value, b.ContactId.Value]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindAllByVoiceNumber_ShouldReturnTheRestrictedContact_WhenTheLimitIsOneAndItWasSavedLast(bool doNotContact)
    {
        await SeedAsync(NewContact("+15551234567"));
        await SeedAsync(NewContact("+15551234567", voiceConsent: true));
        var restricted = await SeedAsync(doNotContact
            ? NewContact("+15551234567", doNotContact: true)
            : NewContact("+15551234567", voiceConsent: false));

        Ids(await FindAsync("+15551234567", limit: 1)).Should().Equal(restricted.ContactId.Value);
    }

    [Fact]
    public async Task FindAllByVoiceNumber_ShouldSortDoNotContactAheadOfVoiceConsentFalse_WhenBothHoldTheNumber()
    {
        var withdrawn = await SeedAsync(NewContact("+15551234567", voiceConsent: false));
        var marked = await SeedAsync(NewContact("+15551234567", doNotContact: true));
        var plain = await SeedAsync(NewContact("+15551234567"));

        var found = Ids(await FindAsync("+15551234567"));

        found.Should().HaveCount(3);
        found[0].Should().Be(marked.ContactId.Value);
        found[1].Should().Be(withdrawn.ContactId.Value);
        found[2].Should().Be(plain.ContactId.Value);
        Ids(await FindAsync("+15551234567", limit: 1)).Should().Equal(marked.ContactId.Value);
    }

    [Fact]
    public async Task FindAllByVoiceNumber_ShouldWriteNothing_WhenItReads()
    {
        var contact = await SeedAsync(NewContact("+1 (555) 123-4567", doNotContact: true));
        var before = await Store.SearchAsync(Tenant, null, new PagedQuery(1, 50), CancellationToken.None);

        await FindAsync("+15551234567");
        await FindAsync("+19999999999");

        var after = await Store.SearchAsync(Tenant, null, new PagedQuery(1, 50), CancellationToken.None);
        after.TotalCount.Should().Be(before.TotalCount);
        var stored = (await Store.GetByIdAsync(Tenant, contact.ContactId, CancellationToken.None))!;
        stored.Addresses.Single().Address.Should().Be("+1 (555) 123-4567", "no stored address is rewritten");
        stored.UpdatedAt.Should().BeNull();
    }
}
