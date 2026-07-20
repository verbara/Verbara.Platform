using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// A single <c>RequestServices.GetService&lt;IRealtimeSyncService&gt;()</c> (or
/// <c>GetRequiredService</c>) Service-Locator resolve. <see cref="Path"/> is a display path
/// (repo-relative when produced by the tree scan), <see cref="Line"/> is 1-based,
/// <see cref="EnclosingMethod"/> is the name of the method the resolve sits in (or
/// <c>&lt;unknown&gt;</c> for a top-level / lambda-only context).
/// </summary>
internal sealed record ServiceLocatorMatch(string Path, int Line, string EnclosingMethod);

/// <summary>
/// ADR-0012 Ola-3 Gate 2 — pure, I/O-free detector that parses C# with Roslyn and reports every
/// <c>context.RequestServices.GetService&lt;IRealtimeSyncService&gt;()</c> /
/// <c>...GetRequiredService&lt;IRealtimeSyncService&gt;()</c> Service-Locator resolve. Detection is
/// syntactic (real invocation nodes with a matching type argument), so a mention inside a comment,
/// XML-doc, or string literal can never produce a false positive.
/// </summary>
/// <remarks>
/// The scope is <c>IRealtimeSyncService</c> SPECIFICALLY. A general <c>RequestServices.GetService</c>
/// ban is out of scope — several legitimate, unrelated resolves exist across the endpoint surface
/// (feature gates, per-request optional services). This gate targets exactly the realtime-sync
/// resolves the ADR-0012 Ola-3 store decorators replaced; the single tolerated survivor is the
/// presence path (<c>SyncAgentPausedAsync</c>), which is not a store write.
/// </remarks>
internal static class ServiceLocatorScanner
{
    private const string TargetType = "IRealtimeSyncService";

    private static readonly string[] LocatorMethods = ["GetService", "GetRequiredService"];

    public static IReadOnlyList<ServiceLocatorMatch> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var matches = new List<ServiceLocatorMatch>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax mae)
                continue;

            // The invoked member must be a GENERIC GetService / GetRequiredService whose single
            // type argument is IRealtimeSyncService.
            if (mae.Name is not GenericNameSyntax generic)
                continue;
            if (!LocatorMethods.Contains(generic.Identifier.Text))
                continue;

            var typeArgs = generic.TypeArgumentList.Arguments;
            if (typeArgs.Count != 1)
                continue;
            if (TrailingTypeName(typeArgs[0]) != TargetType)
                continue;

            // The receiver's trailing member must be RequestServices
            // (context.RequestServices, HttpContext.RequestServices, etc.).
            if (TrailingMember(mae.Expression) != "RequestServices")
                continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            matches.Add(new ServiceLocatorMatch(path, line, EnclosingMethodName(invocation)));
        }

        return matches;
    }

    /// <summary>Trailing simple name of a type reference (handles <c>IRealtimeSyncService</c> and the
    /// fully-qualified <c>Verbara.Sdk.Pro.Realtime.IRealtimeSyncService</c>).</summary>
    private static string? TrailingTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        _ => null,
    };

    /// <summary>Trailing member/identifier of a receiver expression (handles
    /// <c>RequestServices</c>, <c>context.RequestServices</c>, and deeper chains).</summary>
    private static string? TrailingMember(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
        _ => null,
    };

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
