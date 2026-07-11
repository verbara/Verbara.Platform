using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>
/// Canonical default CSAT prompt templates (csat-runner Phase E) seeded per locale
/// (<c>en-US</c> / <c>es-419</c> / <c>pt-BR</c>) per per-tenant-templatable channel
/// (<c>email</c> / <c>sms</c> / <c>voice</c>) on tenant create
/// (<c>TenantProvisioningService</c>). Webchat is intentionally excluded — it uses the
/// Web embed's i18n strings and carries no per-tenant template.
/// </summary>
/// <remarks>
/// Every seeded row is <c>IsDefault = true</c> so the <c>CsatTemplateProvider</c> fallback
/// chain (tenant-locale → tenant-default-locale → global-default-locale →
/// global-default-en-US) always resolves a non-null template: the last resort is the
/// <see cref="GlobalDefaultLocale"/> default for the requested channel, which this seed set
/// guarantees exists for every channel.
/// </remarks>
public static class CsatDefaultTemplates
{
    /// <summary>The global-default locale (BCP-47) — the terminal fallback of the resolution chain.</summary>
    public const string GlobalDefaultLocale = "en-US";

    /// <summary>The locales seeded on tenant create.</summary>
    public static readonly IReadOnlyList<string> SeededLocales = ["en-US", "es-419", "pt-BR"];

    /// <summary>The channels that carry a per-tenant template (webchat excluded).</summary>
    public static readonly IReadOnlyList<string> TemplatableChannels = ["email", "sms", "voice"];

    /// <summary>
    /// Materialises the default template set for a tenant: one <see cref="CsatTemplateEntry"/>
    /// per (locale × channel) with a deterministic template id
    /// (<c>csat-default-{channel}-{locale}</c>) so provisioning is idempotent (the store upserts
    /// on <c>(tenant_id, template_id)</c>).
    /// </summary>
    public static IReadOnlyList<CsatTemplateEntry> ForTenant(TenantId tenantId, DateTimeOffset now)
    {
        var entries = new List<CsatTemplateEntry>(SeededLocales.Count * TemplatableChannels.Count);
        foreach (var locale in SeededLocales)
        {
            foreach (var channel in TemplatableChannels)
            {
                var (subject, body) = Copy(channel, locale);
                entries.Add(new CsatTemplateEntry
                {
                    TemplateId = EntityId.From($"csat-default-{channel}-{locale}"),
                    TenantId = tenantId,
                    Channel = channel,
                    Locale = locale,
                    Subject = subject,
                    Body = body,
                    IsDefault = true,
                    CreatedAt = now,
                });
            }
        }

        return entries;
    }

    // Localized copy. Email carries a subject; SMS and voice do not (Subject null).
    // The voice body is the TTS prompt the deferred voice adapter will read.
    private static (string? Subject, string Body) Copy(string channel, string locale) =>
        (channel, locale) switch
        {
            ("email", "es-419") => (
                "¿Cómo estuvo tu atención?",
                "Gracias por comunicarte con nosotros. Responde a este correo con un número del 1 al 5, donde 5 es muy satisfecho, para calificar tu atención."),
            ("email", "pt-BR") => (
                "Como foi o seu atendimento?",
                "Obrigado por entrar em contato. Responda a este e-mail com um número de 1 a 5, sendo 5 muito satisfeito, para avaliar o seu atendimento."),
            ("email", _) => (
                "How was your support experience?",
                "Thanks for contacting us. Reply to this email with a number from 1 to 5 — where 5 is very satisfied — to rate your support experience."),

            ("sms", "es-419") => (
                null,
                "Gracias por tu llamada. Responde con un número del 1 al 5 (5 = muy satisfecho) para calificar tu atención."),
            ("sms", "pt-BR") => (
                null,
                "Obrigado pelo contato. Responda com um número de 1 a 5 (5 = muito satisfeito) para avaliar o seu atendimento."),
            ("sms", _) => (
                null,
                "Thanks for reaching out. Reply with a number from 1 to 5 (5 = very satisfied) to rate your support."),

            ("voice", "es-419") => (
                null,
                "Gracias por su llamada. En una escala del uno al cinco, donde cinco es muy satisfecho, ¿cómo calificaría la atención que recibió?"),
            ("voice", "pt-BR") => (
                null,
                "Obrigado pela sua ligação. Em uma escala de um a cinco, sendo cinco muito satisfeito, como você avaliaria o atendimento que recebeu?"),
            ("voice", _) => (
                null,
                "Thank you for your call. On a scale of one to five, where five is very satisfied, how would you rate the support you received?"),

            _ => (null, "Please rate your experience from 1 to 5."),
        };
}
