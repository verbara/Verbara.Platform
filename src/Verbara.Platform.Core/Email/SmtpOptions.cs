namespace Verbara.Platform.Core.Email;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "reports@platform.local";
    public string FromName { get; set; } = "Platform Reports";
}
