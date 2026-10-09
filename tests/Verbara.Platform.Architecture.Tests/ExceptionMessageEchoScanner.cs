using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// One place where a caught exception's <c>Message</c> flows into an HTTP error response.
/// <see cref="Path"/> is a display path, <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record ExceptionMessageEchoMatch(string Path, int Line, string Expression);

/// <summary>
/// v2.27.0 block F (D13) — pure, I/O-free detector that parses C# with Roslyn and reports every
/// <c>&lt;catch variable&gt;.Message</c> (also <c>ex.InnerException.Message</c> and the like) inside a
/// <c>catch</c> block that is built into an error response: an argument of a <c>Results.*</c> /
/// <c>TypedResults.*</c> call, the construction of an <c>ErrorResponse</c>, <c>ErrorDetailResponse</c>
/// or <c>ProblemDetails</c>, or an assignment to <c>Detail</c>. Detection is syntactic, so a mention in
/// a comment or string literal never matches, and a <c>.Message</c> on something that is not the catch
/// variable (a domain object's <c>Message</c> property) is not flagged.
/// </summary>
/// <remarks>
/// Logging the message (<c>logger.LogWarning(ex, ...)</c>) and recording it in a non-error DTO are not
/// in scope. A message first copied into a local and then returned is not caught: the gate stops the
/// direct form, which is the one that keeps reappearing.
/// </remarks>
internal static class ExceptionMessageEchoScanner
{
    private static readonly HashSet<string> ResponseTypes = new(StringComparer.Ordinal)
    {
        "ErrorResponse",
        "ErrorDetailResponse",
        "ProblemDetails",
        "HttpValidationProblemDetails",
    };

    private static readonly HashSet<string> ResultFactories = new(StringComparer.Ordinal)
    {
        "Results",
        "TypedResults",
    };

    public static IReadOnlyList<ExceptionMessageEchoMatch> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var matches = new List<ExceptionMessageEchoMatch>();

        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var variable = catchClause.Declaration?.Identifier.Text;
            if (string.IsNullOrEmpty(variable))
                continue;

            foreach (var access in catchClause.Block.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (access.Name.Identifier.Text != "Message" || RootIdentifier(access.Expression) != variable)
                    continue;

                if (!FlowsIntoErrorResponse(access, catchClause.Block))
                    continue;

                var line = access.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                matches.Add(new ExceptionMessageEchoMatch(path, line, access.ToString()));
            }
        }

        return matches;
    }

    /// <summary>The leftmost identifier of a member chain (<c>ex</c> in <c>ex.InnerException!.Message</c>).</summary>
    private static string? RootIdentifier(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax inner => RootIdentifier(inner.Expression),
        PostfixUnaryExpressionSyntax postfix => RootIdentifier(postfix.Operand),
        ParenthesizedExpressionSyntax parenthesized => RootIdentifier(parenthesized.Expression),
        ConditionalAccessExpressionSyntax conditional => RootIdentifier(conditional.Expression),
        _ => null,
    };

    private static bool FlowsIntoErrorResponse(SyntaxNode node, SyntaxNode stopAt)
    {
        for (var current = node.Parent; current is not null && current != stopAt; current = current.Parent)
        {
            switch (current)
            {
                case ObjectCreationExpressionSyntax creation when ResponseTypes.Contains(TrailingName(creation.Type) ?? ""):
                    return true;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var receiver } }
                    when ResultFactories.Contains(TrailingName(receiver) ?? ""):
                    return true;
                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: "Detail" } }:
                    return true;
                case AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.Text: "Detail" } }:
                    return true;
            }
        }

        return false;
    }

    private static string? TrailingName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
        _ => null,
    };
}
