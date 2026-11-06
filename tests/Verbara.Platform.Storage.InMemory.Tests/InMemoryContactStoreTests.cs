using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryContactStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static Contact MakeContact(TenantId tenantId) =>
        new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnContact_WhenAddressMatches()
    {
        var store = new InMemoryContactStore();
        var contact = MakeContact(Tenant);
        var address = new ChannelAddress(ChannelType.WhatsApp, "+15551234567");
        contact.AddAddress(address);
        await store.SaveAsync(contact, CancellationToken.None);

        var result = await store.FindByAddressAsync(Tenant, address, CancellationToken.None);

        result.Should().BeSameAs(contact);
    }

    [Fact]
    public async Task FindByAddressAsync_ShouldReturnNull_WhenNoMatch()
    {
        var store = new InMemoryContactStore();

        var result = await store.FindByAddressAsync(Tenant, new ChannelAddress(ChannelType.Sms, "+10000000000"), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveContact()
    {
        var store = new InMemoryContactStore();
        var contact = MakeContact(Tenant);
        await store.SaveAsync(contact, CancellationToken.None);

        await store.DeleteAsync(Tenant, contact.ContactId, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, contact.ContactId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddIdentityAsync_ShouldReturnNullAndInsertNothing_WhenContactDoesNotExist()
    {
        var store = new InMemoryContactStore();
        var id = EntityId.New();

        var result = await store.AddIdentityAsync(
            Tenant, id, [new ChannelAddress(ChannelType.WhatsApp, "15550000002")], "Alice",
            DateTimeOffset.UtcNow, "system", CancellationToken.None);

        result.Should().BeNull();
        (await store.GetByIdAsync(Tenant, id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AddIdentityAsync_ShouldKeepName_WhenContactAlreadyHasOne()
    {
        var store = new InMemoryContactStore();
        var contact = MakeContact(Tenant);
        contact.LastName = "Doe";
        await store.SaveAsync(contact, CancellationToken.None);

        var result = await store.AddIdentityAsync(
            Tenant, contact.ContactId, [], "Alice", DateTimeOffset.UtcNow, "system", CancellationToken.None);

        result!.FirstName.Should().BeNull();
        result.LastName.Should().Be("Doe");
    }
    // AddIdentityAsync's lookup and its change must be one step with respect to DeleteAsync and SaveAsync. The
    // addresses list is a probe: AddIdentityAsync enumerates it after its lookup and before it is done changing the
    // contact, and the probe starts the delete or save on another thread right there, then spins (no sleep) until that
    // operation has either completed or is blocked. With the write-back atomic it can only block; completing means it
    // landed between the lookup and the change.
    [Fact]
    public async Task AddIdentityAsync_ShouldNotLetDeleteIn_WhenDeleteRunsBetweenLookupAndChange()
    {
        var store = new InMemoryContactStore();
        var contact = MakeContact(Tenant);
        await store.SaveAsync(contact, CancellationToken.None);
        var probe = new GapProbe(
            new ChannelAddress(ChannelType.WhatsApp, "US.13491208655302741918"),
            () => store.DeleteAsync(Tenant, contact.ContactId, CancellationToken.None).GetAwaiter().GetResult());

        var result = await store.AddIdentityAsync(
            Tenant, contact.ContactId, probe, "Alice", DateTimeOffset.UtcNow, "system", CancellationToken.None);
        probe.Join();

        probe.CompletedInGap.Should().BeFalse("a delete must not land between the write-back's lookup and its change");
        result.Should().BeSameAs(contact);
        (await store.GetByIdAsync(Tenant, contact.ContactId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AddIdentityAsync_ShouldNotLetSaveIn_WhenSaveReplacesContactBetweenLookupAndChange()
    {
        var store = new InMemoryContactStore();
        var contact = MakeContact(Tenant);
        await store.SaveAsync(contact, CancellationToken.None);
        var replacement = new Contact
        {
            ContactId = contact.ContactId,
            TenantId = Tenant,
            CreatedAt = contact.CreatedAt,
            Company = "NewCo",
        };
        var probe = new GapProbe(
            new ChannelAddress(ChannelType.WhatsApp, "US.13491208655302741918"),
            () => store.SaveAsync(replacement, CancellationToken.None).GetAwaiter().GetResult());

        var result = await store.AddIdentityAsync(
            Tenant, contact.ContactId, probe, "Alice", DateTimeOffset.UtcNow, "system", CancellationToken.None);
        probe.Join();

        probe.CompletedInGap.Should().BeFalse("a save must not replace the contact between the write-back's lookup and its change");
        result.Should().BeSameAs(contact);
        // A read answers a copy (contacts-writes-follow-their-permissions D17), so the replacement is recognised by content.
        var stored = await store.GetByIdAsync(Tenant, contact.ContactId, CancellationToken.None);
        stored!.Company.Should().Be("NewCo", "the save ran after the write-back and replaced the contact");
        stored.Addresses.Should().BeEmpty("the replacement carries none of the write-back's addresses");
    }

    /// <summary>
    /// One-address list whose enumeration runs <c>interleaved</c> on a second thread and records whether it completed
    /// before the enumeration went on. A thread that is still running when it is seen blocked counts as not
    /// completed, so an imprecise blocked signal can only hide the bug, never fail a correct store.
    /// </summary>
    private sealed class GapProbe(ChannelAddress address, Action interleaved) : IReadOnlyList<ChannelAddress>
    {
        private Thread? _thread;

        public bool CompletedInGap { get; private set; }

        public int Count => 1;

        public ChannelAddress this[int index] => index == 0 ? address : throw new ArgumentOutOfRangeException(nameof(index));

        public void Join() => _thread?.Join();

        public IEnumerator<ChannelAddress> GetEnumerator()
        {
            if (_thread is null)
            {
                _thread = new Thread(() => interleaved());
                _thread.Start();
                var spin = new SpinWait();
                while (_thread.IsAlive && (_thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
                    spin.SpinOnce(sleep1Threshold: -1);
                CompletedInGap = !_thread.IsAlive;
            }

            yield return address;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
