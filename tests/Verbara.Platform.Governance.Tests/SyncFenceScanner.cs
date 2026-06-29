using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Platform.Governance.Tests;

/// <summary>
/// A single wall-clock synchronization barrier found in test source that lacks a valid
/// inline <c>// fence-allow:</c> marker. <see cref="Path"/> is a display path (repo-relative
/// when produced by the tree scan), <see cref="Line"/> is 1-based.
/// </summary>
internal sealed record FenceViolation(string Path, int Line, string Api, string Detail);

/// <summary>
/// Pure, I/O-free detector that parses C# with Roslyn and reports wall-clock synchronization
/// barriers (<c>Task.Delay</c>, <c>Thread.Sleep</c>/<c>SpinWait</c>, <c>SpinWait.SpinUntil</c>,
/// best-effort <see cref="System.Diagnostics.Stopwatch"/> spin-loops, and threading aliases that
/// could smuggle one past the syntactic match) that are not excused by an inline allow-marker.
/// Because detection is syntactic (real invocation/using nodes), comment, XML-doc, and string-literal
/// mentions can never produce a false positive.
/// </summary>
internal static class SyncFenceScanner
{
    /// <summary>
    /// Valid allow-marker: literal <c>// fence-allow:</c>, a category from the CLOSED enum,
    /// an em-dash (<c>—</c>) or <c>--</c> separator, then at least one non-space char (the reason).
    /// A bare marker, an unknown category, or an empty reason is NOT valid.
    /// </summary>
    private static readonly Regex AllowMarker = new(
        @"//\s*fence-allow:\s*(SIMULATED-WORK|GUARD-TIMEOUT|SETTLE|LOOP-DRIVER)\s*(?:—|--)\s*\S",
        RegexOptions.None);

    private static readonly (string Receiver, string Method)[] BannedCalls =
    [
        ("Task", "Delay"),
        ("Thread", "Sleep"),
        ("Thread", "SpinWait"),
        ("SpinWait", "SpinUntil"),
    ];

    private static readonly string[] StopwatchSpinMembers =
    [
        "Elapsed",
        "ElapsedMilliseconds",
        "ElapsedTicks",
    ];

    public static IReadOnlyList<FenceViolation> Scan(string source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var lines = SplitLines(source);
        var violations = new List<FenceViolation>();

        // (a) Banned invocations.
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax mae)
                continue;

            var method = mae.Name.Identifier.Text;
            var receiver = TrailingReceiver(mae.Expression);
            if (receiver is null)
                continue;

            if (!IsBanned(receiver, method))
                continue;

            if (IsAllowed(invocation, lines))
                continue;

            var line = LineSpan(invocation).StartLinePosition.Line + 1;
            violations.Add(new FenceViolation(
                path,
                line,
                $"{receiver}.{method}",
                $"wall-clock barrier '{receiver}.{method}(...)' — replace with a causal signal (FakeTimeProvider / WaitForXAsync) or annotate."));
        }

        // (b) Best-effort Stopwatch spin-loop (while / do).
        foreach (var node in root.DescendantNodes())
        {
            ExpressionSyntax? condition = node switch
            {
                WhileStatementSyntax @while => @while.Condition,
                DoStatementSyntax @do => @do.Condition,
                _ => null,
            };
            if (condition is null)
                continue;

            var hasStopwatchMember = condition
                .DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .Any(m => StopwatchSpinMembers.Contains(m.Name.Identifier.Text));
            if (!hasStopwatchMember)
                continue;

            if (IsAllowed(node, lines))
                continue;

            // Line = the loop keyword's line.
            var keyword = node is WhileStatementSyntax w
                ? w.WhileKeyword
                : ((DoStatementSyntax)node).DoKeyword;
            var line = keyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(new FenceViolation(
                path,
                line,
                "Stopwatch.spin",
                "best-effort: loop condition reads Stopwatch.Elapsed* — wall-clock spin barrier; replace with a causal signal or annotate."));
        }

        // (c) Threading-alias proxy (never marker-excused).
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.Name is null)
                continue;

            var nameText = directive.Name.ToString();
            var isStaticThread =
                directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                && nameText.EndsWith("System.Threading.Thread", StringComparison.Ordinal);
            var isAliasedBarrier =
                directive.Alias is not null
                && (nameText.EndsWith("System.Threading.Thread", StringComparison.Ordinal)
                    || nameText.EndsWith("System.Threading.Tasks.Task", StringComparison.Ordinal));

            if (!isStaticThread && !isAliasedBarrier)
                continue;

            var line = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(new FenceViolation(
                path,
                line,
                "ThreadingAlias",
                "threading alias / static import could smuggle a wall-clock barrier past the syntactic match — not allowed (no legitimate test use)."));
        }

        return violations;
    }

    private static bool IsBanned(string receiver, string method)
    {
        foreach (var (bannedReceiver, bannedMethod) in BannedCalls)
        {
            if (receiver == bannedReceiver && method == bannedMethod)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Trailing identifier of the receiver expression: handles both <c>Task.Delay(...)</c>
    /// (receiver is an <see cref="IdentifierNameSyntax"/>) and the fully-qualified
    /// <c>System.Threading.Tasks.Task.Delay(...)</c> (receiver is itself a member access whose
    /// trailing name is <c>Task</c>).
    /// </summary>
    private static string? TrailingReceiver(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
        _ => null,
    };

    private static FileLinePositionSpan LineSpan(SyntaxNode node) =>
        node.GetLocation().GetLineSpan();

    /// <summary>
    /// A flagged node is allowed iff a valid marker appears on any line it spans, or on the
    /// immediately-preceding non-blank line (scanning upward past whitespace-only lines).
    /// </summary>
    private static bool IsAllowed(SyntaxNode node, List<string> lines)
    {
        var span = LineSpan(node);
        var startLine = span.StartLinePosition.Line;
        var endLine = span.EndLinePosition.Line;

        for (var i = startLine; i <= endLine; i++)
        {
            if (LineHasMarker(lines, i))
                return true;
        }

        // Immediately-preceding non-blank line.
        for (var i = startLine - 1; i >= 0; i--)
        {
            if (i >= lines.Count)
                continue;
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            return LineHasMarker(lines, i);
        }

        return false;
    }

    private static bool LineHasMarker(List<string> lines, int index) =>
        index >= 0 && index < lines.Count && AllowMarker.IsMatch(lines[index]);

    private static List<string> SplitLines(string source)
    {
        var raw = source.Split('\n');
        var result = new List<string>(raw.Length);
        foreach (var line in raw)
            result.Add(line.EndsWith('\r') ? line[..^1] : line);

        return result;
    }
}
