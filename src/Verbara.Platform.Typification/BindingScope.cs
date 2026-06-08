namespace Verbara.Platform.Typification;

/// <summary>
/// Scope at which a <see cref="SchemaBinding"/> applies. Resolution is
/// most-specific-wins: Queue+Channel → Queue → Campaign → Channel → Direction → Tenant default.
/// </summary>
public enum BindingScope
{
    Tenant = 0,
    Queue = 1,
    Campaign = 2,
    Channel = 3,
    Direction = 4,
}
