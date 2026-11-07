using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Pro.AgentAssist.Engine;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The seams the Agent Assist supervisor and session routes read through: the live sessions and the stored records.
/// </summary>
internal static class AgentAssistReadSeamsExtensions
{
    /// <summary>
    /// Registers <see cref="IAgentAssistLiveSessions"/> over the optional Pro <see cref="AgentAssistSupervisor"/> (absent
    /// without Pro Agent Assist) and <see cref="IAgentAssistRecordReader"/>, which resolves the record stores when a record
    /// is read: they are registered only with an Analytics connection string.
    /// </summary>
    public static IServiceCollection AddAgentAssistReadSeams(this IServiceCollection services)
    {
        services.AddSingleton<IAgentAssistLiveSessions>(sp =>
            new AgentAssistLiveSessions(sp.GetService<AgentAssistSupervisor>()));
        services.AddSingleton<IAgentAssistRecordReader>(sp => new AgentAssistRecordReader(sp));
        return services;
    }
}
