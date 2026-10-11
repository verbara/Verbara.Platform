using System.Text.Json.Serialization;

namespace Verbara.Platform.Api.Endpoints.Shared;

// partner-channel-pilot-fixes (design D4): typed refusal bodies in Platform's ErrorResponse convention — `error`
// carries only a kebab-case machine code (Web's ApiError keys on it and localizes the message), the other members
// are the facts a client needs to render it. Frozen field for field in the change's fixtures; registered in
// ApiJsonContext. Nullable members are always written, never omitted.

/// <summary>400 <c>currency-invalid</c>: a rate-card currency (received, or stored on a legacy row) that is not ISO 4217.</summary>
internal sealed record CurrencyInvalidRefusal(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Currency)
{
    public const string Code = "currency-invalid";

    public static CurrencyInvalidRefusal For(string? currency) => new(Code, currency);
}

/// <summary>400 <c>currency-mismatch</c>: the Partner card and the host base card are priced in different currencies.</summary>
internal sealed record CurrencyMismatchRefusal(string Error, string PartnerCurrency, string HostCurrency)
{
    public const string Code = "currency-mismatch";

    public static CurrencyMismatchRefusal For(string partnerCurrency, string hostCurrency) => new(Code, partnerCurrency, hostCurrency);
}

/// <summary>409 <c>plan-exceeds-parent</c>: a tenant plan above its parent tenant's plan.</summary>
internal sealed record PlanAboveParentRefusal(string Error, string RequestedPlan, string ParentPlan, string ParentTenantId)
{
    public const string Code = "plan-exceeds-parent";

    public static PlanAboveParentRefusal For(string requestedPlan, string parentPlan, string parentTenantId) =>
        new(Code, requestedPlan, parentPlan, parentTenantId);
}

/// <summary>409 <c>dunning-partner-customer</c>: a host dunning route aimed at a Customer whose parent is a Partner.</summary>
internal sealed record DunningPartnerCustomerRefusal(string Error, string TenantId, string PartnerTenantId)
{
    public const string Code = "dunning-partner-customer";

    public static DunningPartnerCustomerRefusal For(string tenantId, string partnerTenantId) => new(Code, tenantId, partnerTenantId);
}
