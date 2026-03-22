using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Surveys;

/// <summary>DI registration extensions for Platform.Surveys services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers survey analytics and delivery services.
    /// Callers must separately register implementations of
    /// <see cref="ISurveyStore"/>, <see cref="ISurveyResponseStore"/>, and
    /// <see cref="Conversations.Services.IConversationService"/>.
    /// </summary>
    public static IServiceCollection AddPlatformSurveys(this IServiceCollection services)
    {
        services.AddSingleton<ISurveyAnalytics, InMemorySurveyAnalytics>();
        services.AddSingleton<ISurveyDeliveryService, DefaultSurveyDeliveryService>();
        return services;
    }
}
