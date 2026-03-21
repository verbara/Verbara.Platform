namespace Asterisk.Platform.Channels.Email;

/// <summary>Options for the Email channel connector.</summary>
public sealed class EmailOptions
{
    /// <summary>SMTP server hostname.</summary>
    public required string SmtpHost { get; set; }

    /// <summary>SMTP port. Default: 587 (STARTTLS).</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>SMTP authentication username.</summary>
    public required string SmtpUsername { get; set; }

    /// <summary>SMTP authentication password.</summary>
    public required string SmtpPassword { get; set; }

    /// <summary>Whether to use SSL/TLS. Default: true.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>Default From email address.</summary>
    public required string DefaultFromAddress { get; set; }

    /// <summary>Optional display name for the From header.</summary>
    public string? DefaultFromName { get; set; }

    /// <summary>Maximum attachment size in megabytes. Default: 25.</summary>
    public int MaxAttachmentSizeMb { get; set; } = 25;
}
