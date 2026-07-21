using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// A single per-row store read (<c>GetAsync</c> / <c>GetByIdAsync</c>) awaited inside a loop — the
/// N+1 enrichment shape ADR-0012 Ola-3 Gate 3 replaced with batch primitives. <see cref="Path"/> is
/// a display path (repo-relative when produced by the tree scan), <see cref="Line"/> is 1-based,
/// <see cref="EnclosingMethod"/> is the name of the method the await sits in, <see cref="Method"/> is
/// the single-item store verb that was called.
/// </summary>
internal sealed record EnrichmentLoopMatch(string Path, int Line, string EnclosingMethod, string Method);

/// <summary>
/// ADR-0012 Ola-3 Gate 3 — pure, I/O-free detector that parses C# with Roslyn and reports every
/// <c>await &lt;recv&gt;.GetAsync(...)</c> / <c>await &lt;recv&gt;.GetByIdAsync(...)</c> where the
/// receiver identifier ends with <c>Store</c>/<c>store</c> AND the await sits inside a loop
/// (<c>foreach</c>/<c>for</c>/<c>while</c>/<c>do</c>) within the enclosing method — the per-row N+1
/// enrichment shape the batch <c>GetByIdsAsync</c>/<c>GetBySessionIdsAsync</c> primitives collapse.
/// </summary>
/// <remarks>
/// Detection is syntactic (real invocation nodes whose receiver name and method name match), so a
/// mention inside a comment, XML-doc, or string literal can never produce a false positive. The batch
/// verbs (<c>GetByIdsAsync</c>, <c>GetBySessionIdsAsync</c>) and bulk verbs (<c>Query*</c>,
/// <c>List*</c>, <c>Stream*</c>) are deliberately NOT flagged — only the single-item point reads are
/// N+1 hazards. Writes (<c>Save</c>/<c>Delete</c>/<c>Update</c>/<c>Upsert</c>) are out of scope. A
/// statement carrying a <c>// enrichment-n1-ok: &lt;reason&gt;</c> trailing comment (non-empty reason)
/// is suppressed.
/// </remarks>
internal static class EnrichmentLoopScanner
{
    // ONLY the single-item point reads. NOT GetByIds/GetBySessionIds batch verbs, NOT Query*/List*/
    // Stream*, NOT Save/Delete/Update/Upsert.
    private static readonly string[] SingleItemVerbs = ["GetAsync", "GetByIdAsync"];

    // A non-empty reason after the marker is required to suppress ("enrichment-n1-ok:" alone does not).
    private static readonly Regex SuppressionMarker =
        new(@"//\s*enrichment-n1-ok:\s*\S", RegexOptions.Compiled);

    public static IReadOnlyList<EnrichmentLoopMatch> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var matches = new List<EnrichmentLoopMatch>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax mae)
                continue;

            // The invoked member must be one of the single-item point-read verbs.
            var method = mae.Name.Identifier.Text;
            if (!SingleItemVerbs.Contains(method))
                continue;

            // The receiver's trailing identifier must end in "store" (case-insensitive) —
            // qaStore, cdrStore, agentStore, _store, this._userStore, …
            var receiver = TrailingMember(mae.Expression);
            if (receiver is null || !receiver.EndsWith("store", StringComparison.OrdinalIgnoreCase))
                continue;

            // The call must be awaited (the enrichment shape is `await store.GetAsync(...)`).
            if (!IsAwaited(invocation))
                continue;

            // The await must sit inside a loop within the enclosing method.
            if (!HasLoopAncestor(invocation))
                continue;

            // Suppression: a `// enrichment-n1-ok: <reason>` on the enclosing statement's trailing trivia.
            if (IsSuppressed(invocation))
                continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            matches.Add(new EnrichmentLoopMatch(path, line, EnclosingMethodName(invocation), method));
        }

        return matches;
    }

    /// <summary>Trailing member/identifier of a receiver expression (handles <c>qaStore</c>,
    /// <c>this.qaStore</c>, and deeper chains).</summary>
    private static string? TrailingMember(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
        _ => null,
    };

    /// <summary>True when the invocation is (directly, ignoring parentheses/ConfigureAwait chains)
    /// the operand of an <c>await</c>.</summary>
    private static bool IsAwaited(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AwaitExpressionSyntax:
                    return true;
                // Walk through the chain/wrappers an await may sit above:
                // await store.GetAsync(...).ConfigureAwait(false)
                case MemberAccessExpressionSyntax:
                case InvocationExpressionSyntax:
                case ParenthesizedExpressionSyntax:
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>True when a loop statement (<c>foreach</c>/<c>for</c>/<c>while</c>/<c>do</c>) encloses
    /// the node before its enclosing method boundary is reached.</summary>
    private static bool HasLoopAncestor(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case ForEachStatementSyntax:
                case ForStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                    return true;
                // A loop must be found within the enclosing method/local-function.
                case MethodDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                    return false;
            }
        }

        return false;
    }

    /// <summary>True when the statement enclosing <paramref name="node"/> carries a
    /// <c>// enrichment-n1-ok: &lt;reason&gt;</c> single-line comment in its trailing trivia
    /// (an empty reason does NOT suppress).</summary>
    private static bool IsSuppressed(SyntaxNode node)
    {
        var statement = node.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is null)
            return false;

        foreach (var trivia in statement.GetTrailingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                SuppressionMarker.IsMatch(trivia.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Name of the nearest enclosing method (or local function); <c>&lt;unknown&gt;</c>
    /// when the node is not inside one.</summary>
    private static string EnclosingMethodName(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.Text;
                case LocalFunctionStatementSyntax local:
                    return local.Identifier.Text;
            }
        }

        return "<unknown>";
    }
}
