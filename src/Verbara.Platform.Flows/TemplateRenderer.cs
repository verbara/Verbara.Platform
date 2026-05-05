using System.Text;

namespace Verbara.Platform.Flows;

/// <summary>
/// AOT-safe template renderer that replaces <c>{{variable}}</c> placeholders using a single-pass scan.
/// Unknown placeholders are left unchanged in the output.
/// </summary>
internal sealed class TemplateRenderer : ITemplateRenderer
{
    public string Render(string text, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, open - i);

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                // No closing braces — output remainder as-is.
                sb.Append(text, open, text.Length - open);
                break;
            }

            var key = text.Substring(open + 2, close - open - 2).Trim();
            if (variables.TryGetValue(key, out var value))
                sb.Append(value);
            else
                sb.Append("{{").Append(key).Append("}}");

            i = close + 2;
        }

        return sb.ToString();
    }
}
