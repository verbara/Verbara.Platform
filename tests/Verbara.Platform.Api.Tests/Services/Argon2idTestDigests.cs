using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// Argon2id fixtures shared by the password and recovery-code suites
/// (openspec <c>harden-recovery-code-digest</c>, Phase A).
/// </summary>
/// <remarks>
/// <para>
/// A digest is forged with <see cref="PasswordService.HashPassword"/>, which is Isopoh at the production
/// parameters (m = 12 MiB, t = 3, p = 1). Recovery codes and passwords share those parameters by design
/// (design D1), so this is the same digest the mint paths write once they are Argon2id. No test-only cost knob
/// exists, on purpose (design, Risks).
/// </para>
/// <para>
/// The corrupt shapes are the RC0 probe set run against Isopoh 2.0.0: <c>Argon2.Verify</c> returns
/// <see langword="false"/> for a prefix-only, truncated or bad-base64 value, throws
/// <see cref="ArgumentException"/> for <c>t=0</c>, <c>p=0</c> and an empty salt, and honours a stored
/// <c>m=262144</c> (2,132 MiB allocated by one verify).
/// </para>
/// </remarks>
internal static class Argon2idTestDigests
{
    /// <summary>The encoded prefix every Argon2id digest at the production parameters starts with.</summary>
    public const string ProductionPrefix = "$argon2id$v=19$m=12288,t=3,p=1$";

    /// <summary>The plaintext <see cref="Good"/> was derived from (already normalised: trimmed, upper case).</summary>
    public const string GoodPlaintext = "ABCD2345";

    private static readonly Lazy<string> s_good = new(() => PasswordService.HashPassword(GoodPlaintext));

    /// <summary>A valid Argon2id digest of <see cref="GoodPlaintext"/>, computed once per test run.</summary>
    public static string Good => s_good.Value;

    /// <summary>The RC0 corrupt-digest probe cases, by name, for <c>[Theory]</c> data.</summary>
    public static TheoryData<string> CorruptCases =>
    [
        "prefix only",
        "truncated",
        "bad base64",
        "t=0",
        "p=0",
        "empty salt",
        "m=262144",
    ];

    /// <summary>The cases on which Isopoh 2.0.0 throws <see cref="ArgumentException"/>.</summary>
    public static TheoryData<string> ThrowingCases =>
    [
        "t=0",
        "p=0",
        "empty salt",
    ];

    /// <summary>
    /// Builds the named corrupt variant of <see cref="Good"/>. Parameter edits touch only the parameter segment
    /// (<c>m=…,t=…,p=…</c>), never the salt or tag, so a base64 run that happens to contain <c>t=3</c> cannot be
    /// rewritten by accident.
    /// </summary>
    public static string Corrupt(string kind)
    {
        var good = Good;

        // "$argon2id$v=19$m=12288,t=3,p=1$<salt>$<tag>" → ["", "argon2id", "v=19", "m=…,t=…,p=…", salt, tag]
        var parts = good.Split('$');

        return kind switch
        {
            "prefix only" => "$argon2id$",
            "truncated" => good[..(good.Length / 2)],
            "bad base64" => good[..^4] + "!!!!",
            "t=0" => WithParameters(parts, "m=12288,t=0,p=1"),
            "p=0" => WithParameters(parts, "m=12288,t=3,p=0"),
            "m=262144" => WithParameters(parts, "m=262144,t=3,p=1"),
            "empty salt" => string.Join('$', parts[0], parts[1], parts[2], parts[3], string.Empty, parts[5]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown corrupt-digest case"),
        };
    }

    private static string WithParameters(string[] parts, string parameters) =>
        string.Join('$', parts[0], parts[1], parts[2], parameters, parts[4], parts[5]);
}
