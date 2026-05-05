using Microsoft.AspNetCore.Authorization;

namespace Verbara.Platform.Api.Auth;

internal sealed class PlatformAdminRequirement : IAuthorizationRequirement
{
    public string? Permission { get; }

    public PlatformAdminRequirement(string? permission = null)
    {
        Permission = permission;
    }
}
