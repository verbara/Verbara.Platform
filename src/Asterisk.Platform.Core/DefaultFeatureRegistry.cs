namespace Asterisk.Platform.Core;

public sealed class DefaultFeatureRegistry : IFeatureRegistry
{
    private readonly Dictionary<string, bool> _features = new()
    {
        ["conversations"] = true,
        ["queues"] = true,
        ["agents"] = true,
        ["teams"] = true,
        ["channels"] = true,
        ["contacts"] = true,
        ["flows"] = true,
        ["bot"] = true,
        ["knowledgeBase"] = true,
        ["automation"] = true,
        ["surveys"] = true,
        ["audit"] = true,
        ["dialer"] = false,
        ["analytics"] = false,
        ["agentAssist"] = false,
        ["callAnalytics"] = false,
        ["eventStore"] = false,
        ["cluster"] = false,
        ["multiTenant"] = false,
        ["routing"] = false,
    };

    public IReadOnlyDictionary<string, bool> GetFeatures() => _features;
}
