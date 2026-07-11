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
/// and registers the <see cref="ImapInboundPoller"/> as a hosted service.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the CSAT email IMAP gap-fill pipeline (options, token signer, handler, poller).</summary>
    public static IServiceCollection AddPlatformMail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ImapPollerOptions>(configuration.GetSection("Imap"));
        services.Configure<CsatCaptureForwardOptions>(configuration.GetSection("CsatCapture"));

        // Reuse Pro's HMAC-SHA256 reply-token verifier via its (secret, ttl) constructor — the token
        // binding + crypto live in Pro; Platform never re-implements the HMAC (ADR-0022 boundary).
        services.AddSingleton<ICsatReplyTokenSigner>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ImapPollerOptions>>().Value;
            return new HmacCsatReplyTokenSigner(options.TokenSigningSecret, options.TokenTtl);
        });

        services.AddSingleton<ICsatEmailDispatchResolver, PostgresCsatEmailDispatchResolver>();
        services.AddSingleton<ICsatEmailCaptureForwarder, HttpCsatEmailCaptureForwarder>();
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
