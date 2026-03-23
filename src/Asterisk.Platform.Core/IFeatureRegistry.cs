namespace Asterisk.Platform.Core;

public interface IFeatureRegistry
{
    IReadOnlyDictionary<string, bool> GetFeatures();
}
