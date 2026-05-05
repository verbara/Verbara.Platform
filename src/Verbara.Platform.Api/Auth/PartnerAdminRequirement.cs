using Microsoft.AspNetCore.Authorization;

namespace Verbara.Platform.Api.Auth;

internal sealed class PartnerAdminRequirement : IAuthorizationRequirement
{
    public string? Permission { get; }

    public PartnerAdminRequirement(string? permission = null)
    {
        Permission = permission;
    }
}
