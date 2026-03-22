namespace Asterisk.Platform.Flows;

/// <summary>
/// Renders a template string by substituting <c>{{variable}}</c> placeholders with values.
/// </summary>
public interface ITemplateRenderer
{
    /// <summary>
    /// Returns the rendered string. Unknown placeholders are left as-is.
    /// </summary>
    string Render(string text, IReadOnlyDictionary<string, string> variables);
}
