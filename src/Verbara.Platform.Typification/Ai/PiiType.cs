using System.Text.Json.Serialization;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// A category of personally-identifiable information detected by <see cref="PiiScreen"/>
/// in an AI-extracted typification field value (D1, P2b).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PiiType>))]
public enum PiiType
{
    /// <summary>A payment card number (13–19 digits, Luhn-validated).</summary>
    Card,

    /// <summary>A national identity / social-security number (e.g. <c>123-45-6789</c>).</summary>
    NationalId,

    /// <summary>A telephone number (optionally with a leading country/area code).</summary>
    Phone,

    /// <summary>An email address.</summary>
    Email,
}
