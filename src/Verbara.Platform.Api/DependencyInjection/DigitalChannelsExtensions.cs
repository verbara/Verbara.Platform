using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Sms;
using Verbara.Platform.Channels.Sms.Providers;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Sdk.OpenTelemetry;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// whatsapp-works-for-real D2 — the one composition-root entry point for digital channels. The host
/// registers exactly WebChat and WhatsApp (operator decision DQ1); each module's <c>AddXxx</c> exposes its
/// webhook handler and connector to the lazily composed <c>ChannelRegistry</c> (D1), so registering a
/// module here is the only act that makes a channel reachable. Messenger, Instagram, SMS, Telegram,
/// Twitter, RCS and Email stay unregistered until their signature checks fail closed (the
/// registered-handler unsigned-body gate in the Api tests turns red otherwise).
/// </summary>
public static class DigitalChannelsExtensions
{
    /// <summary>
    /// Registers WebChat and WhatsApp, plus the Twilio <see cref="ISmsProvider"/> when
    /// <c>Twilio:AccountSid</c> is configured (moved from <c>Program.cs</c>; the provider serves its
    /// existing consumers — the SMS channel handler and connector are NOT registered). Nothing here
    /// resolves a connector, a provider client or a channel configuration at host start.
    /// </summary>
    public static IServiceCollection AddDigitalChannels(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddWebChat();

        // Every WhatsApp credential is per tenant (TenantChannelConfig, keys in WhatsAppCredentialKeys):
        // no process-wide option is bound, so a tenant without its own AppSecret is ignored (fail-closed).
        services.AddWhatsApp(static _ => { });

        // What a verified webhook delivery does once stored: idempotent side effects, isolated per message.
        services.AddScoped<WebhookInboundProcessor>();

        // ─── Twilio SMS provider (conditional on config) ───────────────────────
        var twilioSection = configuration.GetSection("Twilio");
        if (!string.IsNullOrEmpty(twilioSection["AccountSid"]))
        {
            services.Configure<TwilioOptions>(o =>
            {
                o.AccountSid = twilioSection["AccountSid"]!;
                o.AuthToken = twilioSection["AuthToken"]!;
            });
            services.AddHttpClient("twilio");
            services.AddSingleton<ISmsProvider, TwilioSmsProvider>();
            // Transient-retry policy for Twilio HTTP calls (v1.9.1 Frente A).
            services.AddTwilioResiliencePolicy();
        }

        return services;
    }

    /// <summary>
    /// The meters the digital channels record on, so the Prometheus/OTLP exporters carry them:
    /// <see cref="DeliveryStatusHandler.MeterName"/> (<c>channels.delivery_status.unknown_id</c>) and
    /// <see cref="WhatsAppWebhookHandler.MeterName"/> (<c>whatsapp.webhook.rejected</c> — why a tenant's WhatsApp
    /// traffic is being ignored).
    /// </summary>
    public static IReadOnlyList<string> MeterNames { get; } =
    [
        DeliveryStatusHandler.MeterName,
        WhatsAppWebhookHandler.MeterName,
    ];

    /// <summary>Enrols every meter in <see cref="MeterNames"/> on the host's OpenTelemetry builder.</summary>
    public static VerbaraOpenTelemetryBuilder AddDigitalChannelMeters(this VerbaraOpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var meterName in MeterNames)
            builder.AddMeter(meterName);
        return builder;
    }
}
