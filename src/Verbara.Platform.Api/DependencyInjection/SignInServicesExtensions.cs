using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The sign-in services of the Api: refresh tokens, auth events, account lockout, sessions, and the one bound on
/// concurrent password hashing per process (<see cref="PasswordHashingGate"/>).
/// </summary>
public static class SignInServicesExtensions
{
    /// <summary>
    /// Registers <see cref="RefreshTokenService"/>, <see cref="AuthEventService"/>, <see cref="AccountLockoutService"/>,
    /// <see cref="SessionService"/> and the <see cref="PasswordHashingGate"/> read from
    /// <c>Identity:PasswordHashing:MaxConcurrency</c> (default half the processors): a sign-in burst queues for a
    /// hashing slot instead of occupying every thread-pool thread with Argon2id.
    /// </summary>
    public static IServiceCollection AddPlatformSignInServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<RefreshTokenService>();
        services.AddSingleton<AuthEventService>();
        services.AddSingleton<AccountLockoutService>();
        services.AddSingleton(PasswordHashingGate.FromConfiguration(configuration));
        services.AddSingleton<SessionService>();
        return services;
    }
}
