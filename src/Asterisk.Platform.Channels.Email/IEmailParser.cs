namespace Asterisk.Platform.Channels.Email;

/// <summary>Parses raw MIME email bytes into a structured <see cref="ParsedEmail"/>.</summary>
public interface IEmailParser
{
    /// <summary>Parses raw MIME content into a <see cref="ParsedEmail"/>.</summary>
    ParsedEmail Parse(ReadOnlyMemory<byte> rawEmail);
}
