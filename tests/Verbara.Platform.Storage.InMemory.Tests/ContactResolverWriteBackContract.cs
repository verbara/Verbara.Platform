using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// What <see cref="DefaultContactIdentityResolver"/> may write back to a contact it found: only the WhatsApp
/// addresses it lacked and a name when it has none, never the rest of the row it read. Each case widens the window
/// between the lookup and the write with <see cref="InterleaveAfterLookupContactStore"/>. Run against every store.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class ContactResolverWriteBackContract
{
    private static readonly ChannelAddress Bsuid = new(ChannelType.WhatsApp, "US.13491208655302741918");
    private static readonly ChannelAddress Phone = new(ChannelType.WhatsApp, "15550000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The store under test, empty for <see cref="Tenant"/>.</summary>
    protected abstract IContactStore Store { get; }

    protected abstract TenantId Tenant { get; }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private async Task<Contact> SeedPhoneContactAsync()
    {
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = Tenant,
            LastName = "Doe",
            Company = "Acme",
            CreatedAt = Now.AddDays(-30),
            UpdatedAt = Now.AddDays(-1),
            UpdatedBy = "agent-7",
        };
        contact.AddAddress(Phone);
        contact.SetCustomField("vip", "yes");
        await Store.SaveAsync(contact, CancellationToken.None);
        return contact;
    }

    private DefaultContactIdentityResolver Resolver(Func<IContactStore, Contact, Task> afterLookup) =>
        new(new InterleaveAfterLookupContactStore(Store, afterLookup), new FixedClock());

    private static readonly ContactIdentity BothIds = new(Bsuid, [Phone], "Alice");

    [Fact]
    public async Task ResolveAsync_ShouldNotReinsertContact_WhenItIsPurgedBetweenLookupAndWrite()
    {
        var seeded = await SeedPhoneContactAsync();
        var sut = Resolver((store, found) => store.DeleteAsync(found.TenantId, found.ContactId, CancellationToken.None));

        var result = await sut.ResolveAsync(Tenant, BothIds, CancellationToken.None);

        (await Store.GetByIdAsync(Tenant, seeded.ContactId, CancellationToken.None))
            .Should().BeNull("an erased contact must stay erased");
        var all = await Store.SearchAsync(Tenant, null, new PagedQuery(1, 50), CancellationToken.None);
        all.Items.Should().NotContain(c => c.Company == "Acme" || c.LastName == "Doe" || c.CustomFields.ContainsKey("vip"),
            "nothing of the erased contact may come back");
        result.ContactId.Should().NotBe(seeded.ContactId);
        result.Addresses.Should().BeEquivalentTo([Bsuid, Phone], "the message's own sender becomes a fresh contact");
        result.FirstName.Should().Be("Alice");
    }

    [Fact]
    public async Task ResolveAsync_ShouldKeepAgentEdit_WhenContactIsEditedBetweenLookupAndWrite()
    {
        var seeded = await SeedPhoneContactAsync();
        var sut = Resolver(async (store, found) =>
        {
            var edited = InterleaveAfterLookupContactStore.Copy(found);
            edited.Company = "NewCo";
            edited.SetCustomField("tier", "gold");
            await store.SaveAsync(edited, CancellationToken.None);
        });

        await sut.ResolveAsync(Tenant, BothIds, CancellationToken.None);

        var stored = await Store.GetByIdAsync(Tenant, seeded.ContactId, CancellationToken.None);
        stored!.Company.Should().Be("NewCo", "the resolver writes only addresses and a missing name");
        stored.CustomFields.Should().ContainKey("tier");
        stored.Addresses.Should().BeEquivalentTo([Phone, Bsuid]);
        stored.LastName.Should().Be("Doe");
        stored.FirstName.Should().BeNull("the contact already has a name");
    }

    [Fact]
    public async Task ResolveAsync_ShouldCreditTheSystem_WhenItChangesAnExistingContact()
    {
        var seeded = await SeedPhoneContactAsync();
        var sut = Resolver((_, _) => Task.CompletedTask);

        var result = await sut.ResolveAsync(Tenant, BothIds, CancellationToken.None);

        var stored = await Store.GetByIdAsync(Tenant, seeded.ContactId, CancellationToken.None);
        stored!.UpdatedBy.Should().Be("system", "the last human editor did not make this change");
        stored.UpdatedAt.Should().Be(Now);
        result.UpdatedBy.Should().Be("system");
    }
}
