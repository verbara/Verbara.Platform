using Microsoft.AspNetCore.Authorization;

namespace Asterisk.Platform.Api.Auth;

internal sealed class PlatformAdminRequirement : IAuthorizationRequirement
{
    public string? Permission { get; }

    public PlatformAdminRequirement(string? permission = null)
    {
        Permission = permission;
    }
}
