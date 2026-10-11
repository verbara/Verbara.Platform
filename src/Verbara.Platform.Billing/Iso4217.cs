using System.Collections.Frozen;

namespace Verbara.Platform.Billing;

/// <summary>
/// The active ISO 4217 alphabetic currency codes a rate card may carry (partner-channel-pilot-fixes, capability
/// rate-card-settlement-currency, design D2). A static, hand-maintained set: AOT-safe (no ICU culture data), and
/// fail-closed — a code this list does not know yet is refused until a one-line edit adds it.
/// </summary>
/// <remarks>
/// The set is ISO 4217 List One's currency and fund codes as of 2026-10-11: XCG replaced ANG, ZWG replaced ZWL,
/// SLE replaced SLL, and BGN was withdrawn when Bulgaria adopted the euro on 2026-01-01. The non-currency codes of
/// List One are left out on purpose, because no rate card is priced in them: the precious metals (XAU, XAG, XPD,
/// XPT), the SDR and bond-market units (XDR, XSU, XUA, XBA-XBD), the testing code XTS and "no currency" XXX.
/// </remarks>
public static class Iso4217
{
    private static readonly FrozenSet<string> Codes = new[]
    {
        "AED", "AFN", "ALL", "AMD", "AOA", "ARS", "AUD", "AWG", "AZN",
        "BAM", "BBD", "BDT", "BHD", "BIF", "BMD", "BND", "BOB", "BOV", "BRL", "BSD", "BTN", "BWP", "BYN", "BZD",
        "CAD", "CDF", "CHE", "CHF", "CHW", "CLF", "CLP", "CNY", "COP", "COU", "CRC", "CUP", "CVE", "CZK",
        "DJF", "DKK", "DOP", "DZD",
        "EGP", "ERN", "ETB", "EUR",
        "FJD", "FKP",
        "GBP", "GEL", "GHS", "GIP", "GMD", "GNF", "GTQ", "GYD",
        "HKD", "HNL", "HTG", "HUF",
        "IDR", "ILS", "INR", "IQD", "IRR", "ISK",
        "JMD", "JOD", "JPY",
        "KES", "KGS", "KHR", "KMF", "KPW", "KRW", "KWD", "KYD", "KZT",
        "LAK", "LBP", "LKR", "LRD", "LSL", "LYD",
        "MAD", "MDL", "MGA", "MKD", "MMK", "MNT", "MOP", "MRU", "MUR", "MVR", "MWK", "MXN", "MXV", "MYR", "MZN",
        "NAD", "NGN", "NIO", "NOK", "NPR", "NZD",
        "OMR",
        "PAB", "PEN", "PGK", "PHP", "PKR", "PLN", "PYG",
        "QAR",
        "RON", "RSD", "RUB", "RWF",
        "SAR", "SBD", "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SOS", "SRD", "SSP", "STN", "SVC", "SYP", "SZL",
        "THB", "TJS", "TMT", "TND", "TOP", "TRY", "TTD", "TWD", "TZS",
        "UAH", "UGX", "USD", "USN", "UYI", "UYU", "UYW", "UZS",
        "VED", "VES", "VND", "VUV",
        "WST",
        "XAF", "XCD", "XCG", "XOF", "XPF",
        "YER",
        "ZAR", "ZMW", "ZWG",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Trims <paramref name="value"/> and upper-cases it with the invariant culture; true, with that canonical code,
    /// when it is an active ISO 4217 alphabetic code. False (and an empty <paramref name="canonical"/>) otherwise:
    /// null, empty, malformed or unknown.
    /// </summary>
    public static bool TryCanonicalize(string? value, out string canonical)
    {
        var candidate = value?.Trim().ToUpperInvariant();
        if (candidate is { Length: 3 } && Codes.Contains(candidate))
        {
            canonical = candidate;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>True when <paramref name="value"/> canonicalises to an active ISO 4217 code.</summary>
    public static bool IsValid(string? value) => TryCanonicalize(value, out _);
}
