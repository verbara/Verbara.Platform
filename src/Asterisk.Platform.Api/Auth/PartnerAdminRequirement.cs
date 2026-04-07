using Microsoft.AspNetCore.Authorization;

namespace Asterisk.Platform.Api.Auth;

internal sealed class PartnerAdminRequirement : IAuthorizationRequirement
{
    public string? Permission { get; }

    public PartnerAdminRequirement(string? permission = null)
    {
        Permission = permission;
    }
}
