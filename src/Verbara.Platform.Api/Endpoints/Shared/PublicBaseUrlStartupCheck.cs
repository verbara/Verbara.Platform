namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Says once, when the host starts, that password-reset emails and OIDC sign-in are off because no
/// public address resolves (<see cref="PublicBaseUrl.LogIfUnresolvable"/>). Each refusal also logs when
/// it happens, but an installation upgraded without the setting would otherwise learn of it only from
/// users whose email never arrives. A startup filter rather than a hosted service: it runs while the
/// request pipeline is built, and changes nothing in it.
/// </summary>
internal sealed class PublicBaseUrlStartupCheck(ILoggerFactory loggerFactory, IConfiguration configuration) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        PublicBaseUrl.LogIfUnresolvable(loggerFactory, configuration);
        return next;
    }
}
