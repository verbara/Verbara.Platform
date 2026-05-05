using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.KnowledgeBase;

/// <summary>
/// DI registration extensions for Platform.KnowledgeBase services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the knowledge-base search service.
    /// An <see cref="IArticleStore"/> implementation must be registered separately.
    /// </summary>
    public static IServiceCollection AddPlatformKnowledgeBase(this IServiceCollection services)
    {
        services.AddSingleton<IKnowledgeSearch, KeywordKnowledgeSearch>();
        return services;
    }
}
