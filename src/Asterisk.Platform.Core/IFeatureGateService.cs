namespace Asterisk.Platform.Core;

public interface IFeatureGateService
{
    bool IsFeatureEnabled(string tenantId, PlanFeature feature);
    IReadOnlySet<PlanFeature> GetEnabledFeatures(string tenantId);
    int GetMaxChannels(string tenantId);
    int GetAuditRetentionDays(string tenantId);
    int GetMaxWebhookSubscriptions(string tenantId);
    int GetMaxScheduledReports(string tenantId);
}
