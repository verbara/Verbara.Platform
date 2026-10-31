using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Verbara.Platform.Governance.Tests;

/// <summary>A place that suppresses an Sdk deprecation diagnostic.</summary>
internal sealed record VsdkSuppression(string File, int Line, string Text)
{
    public override string ToString() => $"{File}:{Line}: {Text}";
}

/// <summary>
/// Finds a <c>VSDK</c> diagnostic id in the three places that can silence one: an MSBuild <c>NoWarn</c> (element or
/// metadata attribute, on any number of lines), a <c>#pragma warning</c> line, and a <c>dotnet_diagnostic</c> line of
/// an analyzer config. It reads text only, so it stays a plain function of its input, and a malformed project file
/// throws rather than counting as clean.
/// </summary>
internal static partial class VsdkSuppressionScanner
{
    private const string NoWarn = "NoWarn";

    public static IEnumerable<VsdkSuppression> Scan(string source, string file) =>
        Path.GetExtension(file).ToUpperInvariant() switch
        {
            ".CS" => ScanCSharp(source, file),
            ".CSPROJ" or ".PROPS" or ".TARGETS" => ScanMsBuild(source, file),
            ".EDITORCONFIG" or ".GLOBALCONFIG" => ScanAnalyzerConfig(source, file),
            _ => throw new ArgumentException($"'{file}' is not a kind of file this guard reads.", nameof(file)),
        };

    private static IEnumerable<VsdkSuppression> ScanMsBuild(string source, string file)
    {
        var document = XDocument.Parse(source, LoadOptions.SetLineInfo);
        foreach (var element in document.Descendants())
        {
            // XElement.Value holds no comment, so a commented-out NoWarn silences nothing and is not reported.
            if (IsNoWarn(element.Name) && VsdkId().IsMatch(element.Value))
                yield return new VsdkSuppression(file, LineOf(element), element.Value.Trim());

            foreach (var attribute in element.Attributes().Where(a => IsNoWarn(a.Name) && VsdkId().IsMatch(a.Value)))
                yield return new VsdkSuppression(file, LineOf(attribute), attribute.Value.Trim());
        }
    }

    private static IEnumerable<VsdkSuppression> ScanCSharp(string source, string file) =>
        ScanLines(source, file, static line =>
        {
            if (!PragmaWarning().IsMatch(line))
                return false;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return VsdkId().IsMatch(comment < 0 ? line : line[..comment]);
        });

    private static IEnumerable<VsdkSuppression> ScanAnalyzerConfig(string source, string file) =>
        ScanLines(source, file, static line =>
            line.TrimStart().StartsWith("dotnet_diagnostic", StringComparison.OrdinalIgnoreCase) && VsdkId().IsMatch(line));

    private static IEnumerable<VsdkSuppression> ScanLines(string source, string file, Func<string, bool> suppresses)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (suppresses(lines[i]))
                yield return new VsdkSuppression(file, i + 1, lines[i].Trim());
        }
    }

    private static bool IsNoWarn(XName name) => string.Equals(name.LocalName, NoWarn, StringComparison.OrdinalIgnoreCase);

    private static int LineOf(IXmlLineInfo node) => node.LineNumber;

    [GeneratedRegex(@"\bVSDK\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VsdkId();

    [GeneratedRegex(@"^\s*#\s*pragma\s+warning\b", RegexOptions.CultureInvariant)]
    private static partial Regex PragmaWarning();
}
