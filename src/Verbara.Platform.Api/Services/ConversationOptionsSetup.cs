using System.Globalization;
using Microsoft.Extensions.Options;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Binds <see cref="ConversationOptions"/> from the <c>Conversations</c> section the way <c>Program.cs</c> binds
/// <c>Distribution</c>: explicit <c>TryParse</c>, no reflection binder, so it stays AOT-safe
/// (conversations-close-after-inactivity design D1). An out-of-range value fails startup with a message naming the
/// key (<see cref="ConversationOptionsValidator"/> + <c>ValidateOnStart</c>); a host window below WhatsApp's 24 h
/// window logs one warning when the host starts (<see cref="ConversationOptionsStartupCheck"/>).
/// </summary>
internal static class ConversationOptionsSetup
{
    public const string SectionName = "Conversations";

    public const string EnabledKey = SectionName + ":InactivityCloseEnabled";
    public const string AfterHoursKey = SectionName + ":InactivityCloseAfterHours";
    public const string SweepIntervalKey = SectionName + ":InactivityCloseSweepIntervalSeconds";
    public const string BatchSizeKey = SectionName + ":InactivityCloseBatchSize";

    public const int MinAfterHours = 1;
    public const int MinSweepIntervalSeconds = 5;
    public const int MaxSweepIntervalSeconds = 3600;
    public const int MinBatchSize = 1;
    public const int MaxBatchSize = 1000;

    /// <summary>WhatsApp's customer-service window (<c>WhatsAppSessionWindow.Length</c>), in hours.</summary>
    public const int WhatsAppWindowHours = 24;

    /// <summary>Copies every key the section sets onto <paramref name="options"/>; an unparsable value is ignored.</summary>
    public static void Bind(IConfiguration section, ConversationOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);

        if (bool.TryParse(section["InactivityCloseEnabled"], out var enabled))
            options.InactivityCloseEnabled = enabled;
        if (TryParseInt(section["InactivityCloseAfterHours"], out var hours))
            options.InactivityCloseAfterHours = hours;
        if (TryParseInt(section["InactivityCloseSweepIntervalSeconds"], out var interval))
            options.InactivityCloseSweepIntervalSeconds = interval;
        if (TryParseInt(section["InactivityCloseBatchSize"], out var batch))
            options.InactivityCloseBatchSize = batch;
    }

    /// <summary>
    /// The Api half of the inactivity close that <c>Program.cs</c> registers after <c>AddPlatformConversations</c>:
    /// the options binding (D1) and the system-close counter (D13, exported by <c>Program.cs</c>'s
    /// <c>AddMeter(ConversationCloseMetrics.MeterName)</c>).
    /// </summary>
    public static IServiceCollection AddConversationInactivityClose(this IServiceCollection services)
    {
        services.AddConversationOptionsBinding();
        services.AddSingleton<ConversationCloseMetrics>();
        return services;
    }

    /// <summary>Registers the binding, the range validation run at startup, and the startup warning.</summary>
    public static IServiceCollection AddConversationOptionsBinding(this IServiceCollection services)
    {
        services.AddOptions<ConversationOptions>()
            .Configure<IConfiguration>((options, configuration) => Bind(configuration.GetSection(SectionName), options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ConversationOptions>, ConversationOptionsValidator>();
        services.AddTransient<IStartupFilter, ConversationOptionsStartupCheck>();
        return services;
    }

    private static bool TryParseInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}

/// <summary>The ranges of design D1; each failure names its key.</summary>
internal sealed class ConversationOptionsValidator : IValidateOptions<ConversationOptions>
{
    public ValidateOptionsResult Validate(string? name, ConversationOptions options)
    {
        var failures = new List<string>();
        if (options.InactivityCloseAfterHours < ConversationOptionsSetup.MinAfterHours)
            failures.Add($"{ConversationOptionsSetup.AfterHoursKey} must be at least {ConversationOptionsSetup.MinAfterHours} " +
                $"(got {options.InactivityCloseAfterHours}); to turn the inactivity close off set {ConversationOptionsSetup.EnabledKey}=false.");
        if (options.InactivityCloseSweepIntervalSeconds is < ConversationOptionsSetup.MinSweepIntervalSeconds
            or > ConversationOptionsSetup.MaxSweepIntervalSeconds)
            failures.Add($"{ConversationOptionsSetup.SweepIntervalKey} must be between {ConversationOptionsSetup.MinSweepIntervalSeconds} " +
                $"and {ConversationOptionsSetup.MaxSweepIntervalSeconds} (got {options.InactivityCloseSweepIntervalSeconds}).");
        if (options.InactivityCloseBatchSize is < ConversationOptionsSetup.MinBatchSize or > ConversationOptionsSetup.MaxBatchSize)
            failures.Add($"{ConversationOptionsSetup.BatchSizeKey} must be between {ConversationOptionsSetup.MinBatchSize} " +
                $"and {ConversationOptionsSetup.MaxBatchSize} (got {options.InactivityCloseBatchSize}).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Says once, when the host starts, that a host inactivity window below 24 h closes WhatsApp conversations inside
/// Meta's customer-service window (design D1). Reading the options here also validates them, so an out-of-range
/// value fails the start. A startup filter, as <c>PublicBaseUrlStartupCheck</c>: it changes nothing in the pipeline.
/// </summary>
internal sealed partial class ConversationOptionsStartupCheck(
    IOptions<ConversationOptions> options, ILoggerFactory loggerFactory) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var value = options.Value;
        if (value.InactivityCloseEnabled && value.InactivityCloseAfterHours < ConversationOptionsSetup.WhatsAppWindowHours)
            LogWindowBelowWhatsApp(
                loggerFactory.CreateLogger("Verbara.Platform.Api.Services.ConversationOptions"),
                ConversationOptionsSetup.AfterHoursKey, value.InactivityCloseAfterHours,
                ConversationOptionsSetup.WhatsAppWindowHours);
        return next;
    }

    [LoggerMessage(EventId = 7600, Level = LogLevel.Warning,
        Message = "{Key} is {Hours} h, below WhatsApp's {WindowHours} h customer-service window: WhatsApp conversations " +
                  "will be closed while Meta still lets the agent reply, so a returning customer's message opens a new " +
                  "conversation in the same WhatsApp thread.")]
    private static partial void LogWindowBelowWhatsApp(ILogger logger, string key, int hours, int windowHours);
}
