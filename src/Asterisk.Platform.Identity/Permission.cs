using System.Diagnostics.CodeAnalysis;

namespace Asterisk.Platform.Identity;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Permission is the domain concept name")]
[Flags]
public enum Permission
{
    None = 0,
    HandleConversations = 1 << 0,
    ViewReports = 1 << 1,
    ManageQueues = 1 << 2,
    ManageUsers = 1 << 3,
    ManageCampaigns = 1 << 4,
    ManageFlows = 1 << 5,
    ManageIntegrations = 1 << 6,
    ManageChannels = 1 << 7,
    ManageTenantSettings = 1 << 8,
    ViewAuditLog = 1 << 9,
}
