using Microsoft.Extensions.Options;

namespace Verbara.Platform.Mail.Services;

/// <summary>
/// Refuses to start the mail service with the CSAT reply mailbox on (<see cref="SwitchKey"/> true) and a setting
/// that reading and forwarding replies needs missing. Without this the service starts, and then either drains no
/// mailbox at all or fails every poll on the first reply it correlates (no address to forward it to), so no reply
/// is ever recorded. Each failure names the configuration key and its environment-variable form.
/// <para>
/// <c>scripts/check-env-example-startup-settings.py</c> reads <see cref="SwitchKey"/> and the key passed to every
/// <c>Require</c> call below (a <c>{i}</c> index read as <c>0</c>) and checks that an env example lists them next
/// to the switch; <c>RequireEither</c> keys, which fall back to a setting already required, are not listed there.
/// </para>
/// </summary>
internal sealed class CsatReplyMailboxSettingsValidator(IOptions<CsatCaptureForwardOptions> capture)
    : IValidateOptions<ImapPollerOptions>
{
    /// <summary>The setting that turns the reply mailbox on; the requirements below apply only while it is true.</summary>
    internal const string SwitchKey = "Imap:Enabled";

    public ValidateOptionsResult Validate(string? name, ImapPollerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return ValidateOptionsResult.Skip;

        var failures = new List<string>();
        var forward = capture.Value;

        Require(failures, "CsatCapture:ApiBaseAddress", forward.ApiBaseAddress);
        if (!string.IsNullOrWhiteSpace(forward.ApiBaseAddress)
            && (!Uri.TryCreate(forward.ApiBaseAddress, UriKind.Absolute, out var address)
                || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)))
        {
            failures.Add($"CsatCapture:ApiBaseAddress (CsatCapture__ApiBaseAddress) must be an absolute http or https " +
                         $"address of the Api, such as http://platform-api:5000, while {SwitchKey} is true");
        }

        // The secret the Api signs each CSAT email's reply address with: without it no reply can be checked. Only
        // the reply mailbox needs it (DeferredCsatReplyTokenSigner builds the signer at first use, #690).
        Require(failures, "Imap:TokenSigningSecret", options.TokenSigningSecret);

        // Falls back to Services:ServiceKey (ServiceCollectionExtensions), the key the Api checks on this call.
        RequireEither(failures, "CsatCapture:ServiceKey", "Services:ServiceKey", forward.ServiceKey);

        // At least one mailbox: with none, the poller would run and read nothing.
        var count = Math.Max(options.Mailboxes.Count, 1);
        for (var i = 0; i < count; i++)
        {
            var mailbox = i < options.Mailboxes.Count ? options.Mailboxes[i] : null;
            Require(failures, $"Imap:Mailboxes:{i}:TenantId", mailbox?.TenantId);
            Require(failures, $"Imap:Mailboxes:{i}:Host", mailbox?.Host);
            Require(failures, $"Imap:Mailboxes:{i}:Username", mailbox?.Username);
            Require(failures, $"Imap:Mailboxes:{i}:Password", mailbox?.Password);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            failures.Add($"{key} ({EnvName(key)}) is required while {SwitchKey} is true");
    }

    private static void RequireEither(List<string> failures, string key, string fallbackKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            failures.Add($"{key} ({EnvName(key)}), or {fallbackKey} ({EnvName(fallbackKey)}) which it defaults to, " +
                         $"is required while {SwitchKey} is true");
    }

    private static string EnvName(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}
