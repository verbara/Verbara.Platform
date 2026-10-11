using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Core;

/// <summary>What <see cref="TenantPlanResolution.Resolve"/> decided for a tenant being created.</summary>
public enum TenantPlanResolutionKind
{
    /// <summary>No plan was requested: the tenant inherits its parent's plan.</summary>
    Inherited,

    /// <summary>A plan name at or below the parent's plan was requested and is kept.</summary>
    Kept,

    /// <summary>The requested value is not a plan name (<c>Starter</c>, <c>Pro</c>, <c>Enterprise</c>): refuse with 400.</summary>
    UnknownName,

    /// <summary>The requested plan is above the parent's plan: refuse with 409 <c>plan-exceeds-parent</c>.</summary>
    AboveParent,
}

/// <summary>
/// The plan a tenant is born with (partner-channel-pilot-fixes, partner-channel-tenancy, design D15): every creation
/// path — <c>POST /partner/customers</c>, <c>POST /management/tenants</c> and the Customer <c>/api/setup</c> creates —
/// shares this one rule, so it cannot drift between them. An absent plan inherits the parent's plan at creation time;
/// a plan name at or below it is kept; an unknown name or a plan above the parent's is refused.
/// </summary>
public readonly record struct TenantPlanResolution(TenantPlanResolutionKind Kind, TenantPlan Plan, TenantPlan ParentPlan)
{
    /// <summary>True when the tenant may be created with <see cref="Plan"/>.</summary>
    public bool IsAccepted => Kind is TenantPlanResolutionKind.Inherited or TenantPlanResolutionKind.Kept;

    /// <summary>Resolves the plan of a tenant created under <paramref name="parent"/> with <paramref name="requested"/> (null when absent).</summary>
    public static TenantPlanResolution Resolve(Tenant parent, string? requested)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var parentPlan = parent.GetPlan();
        if (requested is null)
            return new(TenantPlanResolutionKind.Inherited, parentPlan, parentPlan);
        if (!TryParseName(requested, out var plan))
            return new(TenantPlanResolutionKind.UnknownName, parentPlan, parentPlan);
        return plan > parentPlan
            ? new(TenantPlanResolutionKind.AboveParent, plan, parentPlan)
            : new(TenantPlanResolutionKind.Kept, plan, parentPlan);
    }

    /// <summary>
    /// Parses a plan <em>name</em>, ignoring case and surrounding spaces. Numbers (<c>"1"</c>, <c>"7"</c>) are not
    /// names and are refused, unlike <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>.
    /// </summary>
    public static bool TryParseName(string? value, out TenantPlan plan)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "STARTER":
                plan = TenantPlan.Starter;
                return true;
            case "PRO":
                plan = TenantPlan.Pro;
                return true;
            case "ENTERPRISE":
                plan = TenantPlan.Enterprise;
                return true;
            default:
                plan = default;
                return false;
        }
    }
}
