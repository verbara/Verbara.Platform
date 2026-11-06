using System.Globalization;
using Microsoft.Extensions.Options;
using Verbara.Platform.Routing.Inbound;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Binds <see cref="LastAgentRoutingOptions"/> from the <c>InboundRouting:LastAgent</c> section the way
/// <see cref="ConversationOptionsSetup"/> binds <c>Conversations</c>: explicit <c>TryParse</c>, no reflection binder, so
/// it stays AOT-safe (conversations-return-to-last-agent design D1). An unparsable or out-of-range value fails startup
/// with a message naming the key (<see cref="LastAgentRoutingOptionsValidator"/> + <c>ValidateOnStart</c>); an empty
/// value counts as unset and keeps the default.
/// </summary>
internal static class LastAgentRoutingOptionsSetup
{
    public const string SectionName = "InboundRouting:LastAgent";

    public const string EnabledKey = SectionName + ":Enabled";
    public const string WindowHoursKey = SectionName + ":WindowHours";

    /// <summary>
    /// Copies every key the section sets onto <paramref name="options"/>. An unparsable value leaves the default in
    /// place here and is reported by <see cref="FindUnparsable"/>, which the validator runs at startup.
    /// </summary>
    public static void Bind(IConfiguration section, LastAgentRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);

        if (bool.TryParse(section["Enabled"], out var enabled))
            options.Enabled = enabled;
        if (TryParseInt(section["WindowHours"], out var hours))
            options.WindowHours = hours;
    }

    /// <summary>One failure per key the section sets to a value <see cref="Bind"/> cannot read.</summary>
    public static IReadOnlyList<string> FindUnparsable(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var failures = new List<string>();
        var enabled = section["Enabled"];
        if (!string.IsNullOrWhiteSpace(enabled) && !bool.TryParse(enabled, out _))
            failures.Add($"{EnabledKey} must be true or false (got '{enabled}').");
        var hours = section["WindowHours"];
        if (!string.IsNullOrWhiteSpace(hours) && !TryParseInt(hours, out _))
            failures.Add($"{WindowHoursKey} must be a whole number (got '{hours}').");
        return failures;
    }

    /// <summary>Registers the binding and the validation run at startup.</summary>
    public static IServiceCollection AddLastAgentRoutingOptionsBinding(this IServiceCollection services)
    {
        services.AddOptions<LastAgentRoutingOptions>()
            .Configure<IConfiguration>((options, configuration) => Bind(configuration.GetSection(SectionName), options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LastAgentRoutingOptions>>(provider =>
            new LastAgentRoutingOptionsValidator(provider.GetRequiredService<IConfiguration>().GetSection(SectionName)));
        return services;
    }

    private static bool TryParseInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}

/// <summary>
/// The range of design D1 (1 to 720 hours), plus every value of <paramref name="section"/> the binder could not read;
/// each failure names its key.
/// </summary>
internal sealed class LastAgentRoutingOptionsValidator(IConfiguration section) : IValidateOptions<LastAgentRoutingOptions>
{
    public ValidateOptionsResult Validate(string? name, LastAgentRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(LastAgentRoutingOptionsSetup.FindUnparsable(section));
        if (options.WindowHours is < LastAgentRoutingOptions.MinWindowHours or > LastAgentRoutingOptions.MaxWindowHours)
            failures.Add($"{LastAgentRoutingOptionsSetup.WindowHoursKey} must be between {LastAgentRoutingOptions.MinWindowHours} " +
                $"and {LastAgentRoutingOptions.MaxWindowHours} (got {options.WindowHours}).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
