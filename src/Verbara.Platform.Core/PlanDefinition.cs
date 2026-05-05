namespace Verbara.Platform.Core;

public static class PlanDefinition
{
    private static readonly IReadOnlySet<PlanFeature> StarterFeatures =
        new HashSet<PlanFeature>().AsReadOnly();

    private static readonly IReadOnlySet<PlanFeature> ProFeatures = new HashSet<PlanFeature>
    {
        PlanFeature.Dialer,
        PlanFeature.BotBasic,
        PlanFeature.AnalyticsExport,
        PlanFeature.Flows,
        PlanFeature.Webhooks,
        PlanFeature.ScheduledReports,
        PlanFeature.KnowledgeBase,
        PlanFeature.Recordings,
    }.AsReadOnly();

    private static readonly IReadOnlySet<PlanFeature> EnterpriseFeatures =
        new HashSet<PlanFeature>(Enum.GetValues<PlanFeature>()).AsReadOnly();

    public static IReadOnlySet<PlanFeature> GetFeatures(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => ProFeatures,
        TenantPlan.Enterprise => EnterpriseFeatures,
        _ => StarterFeatures,
    };

    public static RateLimitTier GetDefaultTier(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => RateLimitTier.Professional,
        TenantPlan.Enterprise => RateLimitTier.Enterprise,
        _ => RateLimitTier.Standard,
    };

    public static int GetMaxChannels(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 7,
        TenantPlan.Enterprise => 11,
        _ => 3,
    };

    public static int GetAuditRetentionDays(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 30,
        TenantPlan.Enterprise => 90,
        _ => 7,
    };

    public static int GetMaxWebhookSubscriptions(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 5,
        TenantPlan.Enterprise => int.MaxValue,
        _ => 0,
    };

    public static int GetMaxScheduledReports(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 5,
        TenantPlan.Enterprise => int.MaxValue,
        _ => 0,
    };
}
