namespace Verbara.Platform.Core;

public interface IFeatureRegistry
{
    IReadOnlyDictionary<string, bool> GetFeatures();
}
