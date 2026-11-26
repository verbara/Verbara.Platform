using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Pro.CsatRunner.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Mail;

/// <summary>
/// DI registration for the CSAT email gap-fill pipeline (csat-runner Phase C). Binds the
/// <c>Imap</c> + <c>CsatCapture</c> configuration, reuses Pro's HMAC reply-token verifier
/// (<see cref="ICsatReplyTokenSigner"/>) rather than re-hand-rolling the HMAC (ADR-0022 boundary),
/// wires the <see cref="CsatReplyMailHandler"/> and its dispatch-resolver / HTTP forwarder seams,
/// registers the <see cref="ImapInboundPoller"/> as a hosted service, and refuses to start with the
/// reply mailbox on and a setting it needs missing (<see cref="CsatReplyMailboxSettingsValidator"/>).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the CSAT email IMAP gap-fill pipeline (options, token signer, handler, poller).</summary>
    public static IServiceCollection AddPlatformMail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ImapPollerOptions>(configuration.GetSection("Imap"));
        services.Configure<CsatCaptureForwardOptions>(configuration.GetSection("CsatCapture"));

        // The Api checks the forwarded reply against its own Services:ServiceKey, which every service of a stack
        // shares, so an unset CsatCapture:ServiceKey sends that key and the operator sets it once.
        services.PostConfigure<CsatCaptureForwardOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.ServiceKey))
                options.ServiceKey = configuration["Services:ServiceKey"] ?? string.Empty;
        });

        // With the reply mailbox on, refuse to start without the settings reading and forwarding replies need.
        services.AddSingleton<IValidateOptions<ImapPollerOptions>, CsatReplyMailboxSettingsValidator>();
        services.AddOptions<ImapPollerOptions>().ValidateOnStart();

        // Reuse Pro's HMAC-SHA256 reply-token verifier via its (secret, ttl) constructor — the token
        // binding + crypto live in Pro; Platform never re-implements the HMAC (ADR-0022 boundary).
        services.AddSingleton<ICsatReplyTokenSigner>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ImapPollerOptions>>().Value;
            return new HmacCsatReplyTokenSigner(options.TokenSigningSecret, options.TokenTtl);
        });

        services.AddSingleton<ICsatEmailDispatchResolver, PostgresCsatEmailDispatchResolver>();
        services.AddSingleton<ICsatEmailCaptureForwarder, HttpCsatEmailCaptureForwarder>();
        services.AddSingleton<IImapPollPositionStore, PostgresImapPollPositionStore>();
        services.AddHttpClient(HttpCsatEmailCaptureForwarder.HttpClientName);

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ImapPollerOptions>>().Value;
            return new CsatReplyMailHandler(
                sp.GetRequiredService<ICsatReplyTokenSigner>(),
                sp.GetRequiredService<ICsatEmailDispatchResolver>(),
                sp.GetRequiredService<ICsatEmailCaptureForwarder>(),
                autoReplyEnabled: options.AutoReplyEnabled,
                autoReply: sp.GetService<Verbara.Platform.Core.Email.IEmailService>(),
                timeProvider: sp.GetService<TimeProvider>(),
                logger: sp.GetService<ILogger<CsatReplyMailHandler>>());
        });

        services.AddHostedService<ImapInboundPoller>();

        return services;
    }
}
