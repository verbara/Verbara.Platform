using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Core.Branding;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Unit tests for <see cref="EffectiveBranding.ResolveAsync"/>: field-by-field inheritance over at most
/// two ancestors, blank treated as unset, the subdomain never inherited, a Deleted ancestor skipped.
/// The doubles below accept any parent pointer (cycles, a third ancestor) and record every read.
/// </summary>
public sealed class EffectiveBrandingTests
{
    private readonly RecordingTenantStore _tenants = new();
    private readonly RecordingBrandingStore _brandings = new();

    // Reads the record first, as every production caller does, then resolves with it (null when unknown).
    // That read is recorded, so a test's tenant-store reads include it.
    private async Task<TenantBranding?> ResolveAsync(string tenantId) =>
        await ResolveAsync(tenantId, await _tenants.GetAsync(tenantId));

    private Task<TenantBranding?> ResolveAsync(string tenantId, Tenant? tenant) =>
        EffectiveBranding.ResolveAsync(tenantId, tenant, _tenants, _brandings, CancellationToken.None).AsTask();

    private void SeedChain(TenantStatus partnerStatus = TenantStatus.Active, TenantStatus customerStatus = TenantStatus.Active)
    {
        _tenants.Add("h", parent: null, TenantType.Platform);
        _tenants.Add("p", parent: "h", TenantType.Partner, partnerStatus);
        _tenants.Add("c", parent: "p", TenantType.Customer, customerStatus);
    }

    [Fact]
    public async Task ResolveAsync_ShouldTakePartnerBrand_WhenCustomerHasNoRow()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "p", DisplayName = "Partner Brand", PrimaryColor = "#FF0000" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("c");
        result.DisplayName.Should().Be("Partner Brand");
        result.PrimaryColor.Should().Be("#FF0000");
    }

    [Fact]
    public async Task ResolveAsync_ShouldTakePlatformBrand_WhenPartnerHasNoRow()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "h", LogoUrl = "https://cdn.example/logo.png" });

        var result = await ResolveAsync("p");

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("p");
        result.LogoUrl.Should().Be("https://cdn.example/logo.png");
    }

    [Fact]
    public async Task ResolveAsync_ShouldKeepOwnValuesAndInheritTheRest_WhenCustomerRowIsPartial()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "h", FaviconUrl = "F", PrimaryColor = "#0000FF" });
        _brandings.Add(new TenantBranding { TenantId = "p", PrimaryColor = "#FF0000", AccentColor = "#00AA00" });
        _brandings.Add(new TenantBranding { TenantId = "c", PrimaryColor = "#00FF00" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.PrimaryColor.Should().Be("#00FF00");
        result.AccentColor.Should().Be("#00AA00");
        result.FaviconUrl.Should().Be("F");
    }

    [Fact]
    public async Task ResolveAsync_ShouldTakeParentValueOverGrandparent_WhenBothSetFieldAndChildHasNoRow()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "h", DisplayName = "Licensee", PrimaryColor = "#0000FF", LogoUrl = "L" });
        _brandings.Add(new TenantBranding { TenantId = "p", DisplayName = "Partner Brand", PrimaryColor = "#FF0000" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Partner Brand");
        result.PrimaryColor.Should().Be("#FF0000");
        result.LogoUrl.Should().Be("L");
    }

    [Fact]
    public async Task ResolveAsync_ShouldInheritEveryFieldButSubdomain_WhenParentSetsAllAndChildHasNoRow()
    {
        SeedChain();
        _brandings.Add(new TenantBranding
        {
            TenantId = "p",
            DisplayName = "Partner Brand",
            LogoUrl = "https://cdn.example/logo.png",
            FaviconUrl = "https://cdn.example/favicon.ico",
            PrimaryColor = "#FF0000",
            SecondaryColor = "#00FF00",
            AccentColor = "#0000FF",
            Locale = "es-419",
            Timezone = "America/Bogota",
            Subdomain = "partner-x",
            SupportEmail = "help@partner.example",
            SupportUrl = "https://partner.example/help",
            EmailFromName = "Partner Support",
            EmailFromAddress = "noreply@partner.example",
        });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("c");
        result.DisplayName.Should().Be("Partner Brand");
        result.LogoUrl.Should().Be("https://cdn.example/logo.png");
        result.FaviconUrl.Should().Be("https://cdn.example/favicon.ico");
        result.PrimaryColor.Should().Be("#FF0000");
        result.SecondaryColor.Should().Be("#00FF00");
        result.AccentColor.Should().Be("#0000FF");
        result.Locale.Should().Be("es-419");
        result.Timezone.Should().Be("America/Bogota");
        result.SupportEmail.Should().Be("help@partner.example");
        result.SupportUrl.Should().Be("https://partner.example/help");
        result.EmailFromName.Should().Be("Partner Support");
        result.EmailFromAddress.Should().Be("noreply@partner.example");
        result.Subdomain.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task ResolveAsync_ShouldInheritAncestorValue_WhenOwnFieldIsBlank(string blank)
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "p", DisplayName = "Partner Brand", SupportUrl = "S" });
        _brandings.Add(new TenantBranding { TenantId = "c", DisplayName = blank, SupportUrl = blank, Locale = "es-419" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Partner Brand");
        result.SupportUrl.Should().Be("S");
        result.Locale.Should().Be("es-419");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNullField_WhenFieldIsBlankOnEveryRow()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "h", DisplayName = " ", LogoUrl = "" });
        _brandings.Add(new TenantBranding { TenantId = "p", DisplayName = "", LogoUrl = "  " });
        _brandings.Add(new TenantBranding { TenantId = "c", DisplayName = "", LogoUrl = null, EmailFromName = "" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().BeNull();
        result.LogoUrl.Should().BeNull();
        result.EmailFromName.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenNoRowExistsInChain()
    {
        SeedChain();

        var result = await ResolveAsync("c");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenTenantIsUnknownAndHasNoRow()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "h", DisplayName = "Licensee" });

        var result = await ResolveAsync("no-such-tenant");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReadTenantRecordOnce_WhenCallerFoundNoRecord()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "h", DisplayName = "Licensee" });
        _brandings.Add(new TenantBranding { TenantId = "no-such-tenant", DisplayName = "Orphan" });

        var record = await _tenants.GetAsync("no-such-tenant");
        var result = await ResolveAsync("no-such-tenant", record);

        record.Should().BeNull();
        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Orphan");
        _tenants.Reads.Should().Equal("no-such-tenant");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnOwnRowWithoutParentReads_WhenTenantIsNotInStore()
    {
        _tenants.Add("h", parent: null, TenantType.Platform);
        _brandings.Add(new TenantBranding { TenantId = "h", DisplayName = "Licensee", LogoUrl = "L" });
        _brandings.Add(new TenantBranding
        {
            TenantId = "t",
            DisplayName = "Own",
            PrimaryColor = "#123456",
            Subdomain = "own",
            SupportEmail = "help@own.example",
        });

        var result = await ResolveAsync("t");

        result.Should().NotBeNull();
        result!.TenantId.Should().Be("t");
        result.DisplayName.Should().Be("Own");
        result.PrimaryColor.Should().Be("#123456");
        result.Subdomain.Should().Be("own");
        result.SupportEmail.Should().Be("help@own.example");
        result.LogoUrl.Should().BeNull();
        _tenants.Reads.Should().Equal("t");
        _brandings.Reads.Should().Equal("t");
    }

    [Fact]
    public async Task ResolveAsync_ShouldNeverTakeSubdomainFromAncestor_WhenOwnRowHasNone()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "h", Subdomain = "licensee" });
        _brandings.Add(new TenantBranding { TenantId = "p", Subdomain = "partner-x", DisplayName = "Partner Brand" });
        _brandings.Add(new TenantBranding { TenantId = "c", Subdomain = "  ", Locale = "pt-BR" });

        var customer = await ResolveAsync("c");
        var partner = await ResolveAsync("p");

        customer.Should().NotBeNull();
        customer!.Subdomain.Should().BeNull();
        customer.DisplayName.Should().Be("Partner Brand");
        partner.Should().NotBeNull();
        partner!.Subdomain.Should().Be("partner-x");
    }

    [Fact]
    public async Task ResolveAsync_ShouldStopAfterTwoHops_WhenCorruptPointerAddsThirdAncestor()
    {
        _tenants.Add("x", parent: null, TenantType.Platform);
        _tenants.Add("h", parent: "x", TenantType.Platform);
        _tenants.Add("p", parent: "h", TenantType.Partner);
        _tenants.Add("c", parent: "p", TenantType.Customer);
        _brandings.Add(new TenantBranding { TenantId = "x", DisplayName = "Too Far", LogoUrl = "X" });
        _brandings.Add(new TenantBranding { TenantId = "h", FaviconUrl = "F" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.FaviconUrl.Should().Be("F");
        result.DisplayName.Should().BeNull();
        result.LogoUrl.Should().BeNull();
        _tenants.Reads.Should().NotContain("x");
        _brandings.Reads.Should().NotContain("x");
        EffectiveBranding.MaxBrandingHops.Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_ShouldTerminateAndReadEachRowOnce_WhenParentPointerIsSelf()
    {
        _tenants.Add("c", parent: "c", TenantType.Customer);
        _brandings.Add(new TenantBranding { TenantId = "c", DisplayName = "Loop" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Loop");
        _brandings.Reads.Should().Equal("c");
    }

    [Fact]
    public async Task ResolveAsync_ShouldTerminateAndReadEachRowOnce_WhenParentPointersFormCycle()
    {
        _tenants.Add("c", parent: "p", TenantType.Customer);
        _tenants.Add("p", parent: "c", TenantType.Partner);
        _brandings.Add(new TenantBranding { TenantId = "c", DisplayName = "Customer" });
        _brandings.Add(new TenantBranding { TenantId = "p", LogoUrl = "P" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Customer");
        result.LogoUrl.Should().Be("P");
        _brandings.Reads.Should().OnlyHaveUniqueItems();
        _brandings.Reads.Should().BeEquivalentTo(["c", "p"]);
    }

    [Fact]
    public async Task ResolveAsync_ShouldStopAndNotReadItsRow_WhenParentIsUnknownToTenantStore()
    {
        _tenants.Add("c", parent: "ghost", TenantType.Customer);
        _brandings.Add(new TenantBranding { TenantId = "ghost", DisplayName = "Ghost" });
        _brandings.Add(new TenantBranding { TenantId = "c", LogoUrl = "C" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.LogoUrl.Should().Be("C");
        result.DisplayName.Should().BeNull();
        _brandings.Reads.Should().NotContain("ghost");
    }

    [Fact]
    public async Task ResolveAsync_ShouldSkipDeletedPartnerAndTakePlatformBrand_WhenCustomerIsWarningWithNoRow()
    {
        SeedChain(partnerStatus: TenantStatus.Deleted, customerStatus: TenantStatus.Warning);
        _brandings.Add(new TenantBranding { TenantId = "h", DisplayName = "Licensee", LogoUrl = "L" });
        _brandings.Add(new TenantBranding
        {
            TenantId = "p",
            DisplayName = "Old Partner",
            PrimaryColor = "#DEAD00",
            SupportUrl = "https://old.example",
        });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Licensee");
        result.LogoUrl.Should().Be("L");
        result.PrimaryColor.Should().BeNull();
        result.SupportUrl.Should().BeNull();
        _brandings.Reads.Should().NotContain("p");
    }

    [Fact]
    public async Task ResolveAsync_ShouldCountDeletedAncestorAsHop_WhenThirdAncestorHasRow()
    {
        _tenants.Add("x", parent: null, TenantType.Platform);
        _tenants.Add("h", parent: "x", TenantType.Platform);
        _tenants.Add("p", parent: "h", TenantType.Partner, TenantStatus.Deleted);
        _tenants.Add("c", parent: "p", TenantType.Customer, TenantStatus.Warning);
        _brandings.Add(new TenantBranding { TenantId = "x", DisplayName = "Too Far" });
        _brandings.Add(new TenantBranding { TenantId = "h", LogoUrl = "L" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.LogoUrl.Should().Be("L");
        result.DisplayName.Should().BeNull();
        _tenants.Reads.Should().NotContain("x");
        _brandings.Reads.Should().NotContain("x");
    }

    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.PendingDeletion)]
    [InlineData(TenantStatus.Warning)]
    [InlineData(TenantStatus.Degraded)]
    public async Task ResolveAsync_ShouldReadAncestorRowLikeActive_WhenAncestorIsNotDeleted(TenantStatus status)
    {
        SeedChain(partnerStatus: status);
        _brandings.Add(new TenantBranding { TenantId = "p", DisplayName = "Partner Brand" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Partner Brand");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReadOwnRow_WhenRequestedTenantIsDeleted()
    {
        SeedChain(customerStatus: TenantStatus.Deleted);
        _brandings.Add(new TenantBranding { TenantId = "c", DisplayName = "Own" });

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Own");
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotReadRequestedTenantRecord_WhenTenantIsPassed()
    {
        SeedChain();
        _brandings.Add(new TenantBranding { TenantId = "p", DisplayName = "Partner Brand" });
        var customer = await _tenants.GetAsync("c");
        _tenants.Reads.Clear();

        var result = await ResolveAsync("c", customer);

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Partner Brand");
        _tenants.Reads.Should().NotContain("c");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNewInstanceAndLeaveStoredRowsUnchanged_WhenInheriting()
    {
        SeedChain();
        var partnerRow = new TenantBranding { TenantId = "p", DisplayName = "Partner Brand", LogoUrl = "P" };
        var customerRow = new TenantBranding { TenantId = "c", DisplayName = "", Subdomain = "acme" };
        _brandings.Add(partnerRow);
        _brandings.Add(customerRow);

        var result = await ResolveAsync("c");

        result.Should().NotBeNull();
        result.Should().NotBeSameAs(customerRow);
        result.Should().NotBeSameAs(partnerRow);
        result!.DisplayName.Should().Be("Partner Brand");
        result.Subdomain.Should().Be("acme");
        customerRow.DisplayName.Should().Be("");
        customerRow.LogoUrl.Should().BeNull();
        partnerRow.Subdomain.Should().BeNull();
    }

    private sealed class RecordingTenantStore : ITenantStore
    {
        private readonly Dictionary<string, Tenant> _tenants = new(StringComparer.Ordinal);

        public List<string> Reads { get; } = [];

        public void Add(string id, string? parent, TenantType type, TenantStatus status = TenantStatus.Active) =>
            _tenants[id] = new Tenant { TenantId = id, Name = id, ParentTenantId = parent, Type = type, Status = status };

        public ValueTask<Tenant?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            Reads.Add(tenantId);
            return ValueTask.FromResult(_tenants.GetValueOrDefault(tenantId));
        }

        public ValueTask<IReadOnlyList<Tenant>> GetAllActiveAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UpsertAsync(Tenant tenant, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UpdateStatusAsync(string tenantId, TenantStatus status, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Tenant?> GetHostTenantAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<Tenant>> GetChildrenAsync(string parentTenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingBrandingStore : ITenantBrandingStore
    {
        private readonly Dictionary<string, TenantBranding> _rows = new(StringComparer.Ordinal);

        public List<string> Reads { get; } = [];

        public void Add(TenantBranding row) => _rows[row.TenantId] = row;

        public ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default)
        {
            Reads.Add(tenantId);
            return ValueTask.FromResult(_rows.GetValueOrDefault(tenantId));
        }

        public ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
