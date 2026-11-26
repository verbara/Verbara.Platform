using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Verbara.Platform.Governance.Tests;

/// <summary>A place in the code that writes an audit category, and the value it writes (null when it cannot be read).</summary>
internal sealed record AuditCategoryWrite(string File, int Line, string Expression, string? Value)
{
    public override string ToString() => $"{File}:{Line}: {Expression}{(Value is null ? string.Empty : $" -> \"{Value}\"")}";
}

/// <summary>
/// audit-categories-accepted (#694): reads the audit categories on both sides of <c>audit_entries_category_check</c>.
/// <list type="bullet">
///   <item>The accepted set: the check's list as a fresh database ends up, the last definition in the migrations in the
///   order the runner applies them (ordinal file name), SQL comments removed.</item>
///   <item>The written set: every <c>category:</c> argument of a <c>RecordAsync</c> call, a string literal second
///   argument of a positional <c>RecordAsync</c> call, and every <c>Category =</c> of a <c>new AuditEntry { }</c>,
///   resolved through string literals, <c>const string</c> fields, conditionals, <c>??</c> and the returned values of
///   a method in the same file.</item>
/// </list>
/// Syntax only: it reads text, so a value it cannot resolve is reported with a null value rather than skipped.
/// </summary>
internal static partial class AuditCategoryScanner
{
    private const string ConstraintName = "audit_entries_category_check";

    /// <summary>The categories the check accepts once every migration in <paramref name="migrations"/> has run.</summary>
    public static IReadOnlySet<string> AcceptedCategories(IEnumerable<(string Name, string Sql)> migrations)
    {
        HashSet<string>? accepted = null;
        foreach (var (name, sql) in migrations.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var text = StripSqlComments(sql);
            var definitions = CategoryCheck().Matches(text);
            if (definitions.Count > 0)
            {
                accepted = SqlStringList(definitions[^1].Groups["list"].Value);
                continue;
            }

            if (text.Contains(ConstraintName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{name} names {ConstraintName} without a CHECK (category IN (...)) this guard can read.");
        }

        return accepted ?? throw new InvalidOperationException($"No migration defines {ConstraintName}.");
    }

    /// <summary>Every place in <paramref name="source"/> that writes an audit category.</summary>
    public static IEnumerable<AuditCategoryWrite> CategoryWrites(string source, string file)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var constants = Constants(root);

        var recordCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodName(i) == "RecordAsync")
            .Select(i => i.ArgumentList.Arguments);
        foreach (var arguments in recordCalls)
        {
            var named = arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "category");
            if (named is not null)
            {
                foreach (var write in Resolve(named.Expression, root, constants, file))
                    yield return write;
            }
            else if (arguments.Count > 1 && arguments[1].NameColon is null
                     && arguments[1].Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
            {
                yield return Write(literal, file, literal.Token.ValueText);
            }
        }

        var categoryAssignments = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(c => TypeName(c.Type) == "AuditEntry" && c.Initializer is not null)
            .SelectMany(c => c.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>())
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "Category" });
        foreach (var assignment in categoryAssignments)
        {
            foreach (var write in Resolve(assignment.Right, root, constants, file))
                yield return write;
        }
    }

    /// <summary>The string values of the array field <paramref name="fieldName"/> declared in <paramref name="source"/>.</summary>
    public static IReadOnlyList<string> StringArrayField(string source, string fieldName)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var variable = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == fieldName);
        return variable.Initializer!.Value.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .ToList();
    }

    /// <summary>The quoted values in the documentation comment of the <c>Category</c> property in <paramref name="source"/>.</summary>
    public static IReadOnlySet<string> DocumentedCategories(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var property = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "Category");
        var doc = property.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .Select(t => t.ToFullString())
            .Single();
        return QuotedWord().Matches(doc).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<AuditCategoryWrite> Resolve(
        ExpressionSyntax expression, CompilationUnitSyntax root, IReadOnlyDictionary<string, string> constants, string file)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                yield return Write(literal, file, literal.Token.ValueText);
                yield break;

            case ParenthesizedExpressionSyntax parenthesized:
                foreach (var write in Resolve(parenthesized.Expression, root, constants, file))
                    yield return write;
                yield break;

            case ConditionalExpressionSyntax conditional:
                foreach (var write in Resolve(conditional.WhenTrue, root, constants, file).Concat(Resolve(conditional.WhenFalse, root, constants, file)))
                    yield return write;
                yield break;

            case IdentifierNameSyntax or MemberAccessExpressionSyntax
                when constants.TryGetValue(LastName(expression), out var constant):
                yield return Write(expression, file, constant);
                yield break;

            case InvocationExpressionSyntax invocation when LocalMethod(root, MethodName(invocation)) is { } method:
                var returned = ReturnedStrings(method).ToList();
                if (returned.Count == 0)
                    yield return Write(expression, file, null);
                foreach (var literal in returned)
                    yield return Write(literal, file, literal.Token.ValueText);
                yield break;

            default:
                yield return Write(expression, file, null);
                yield break;
        }
    }

    // The string literals a method can return: switch-expression arms, return statements and an expression body.
    private static IEnumerable<LiteralExpressionSyntax> ReturnedStrings(MethodDeclarationSyntax method)
    {
        var results = new List<ExpressionSyntax>();
        if (method.ExpressionBody is { } body)
            results.Add(body.Expression);
        results.AddRange(method.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => r.Expression).OfType<ExpressionSyntax>());

        return results
            .SelectMany(result => result is SwitchExpressionSyntax @switch ? @switch.Arms.Select(a => a.Expression) : [result])
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));
    }

    private static Dictionary<string, string> Constants(CompilationUnitSyntax root)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        var stringConstants = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
            .SelectMany(f => f.Declaration.Variables)
            .Select(v => (Name: v.Identifier.ValueText, Literal: v.Initializer?.Value as LiteralExpressionSyntax))
            .Where(c => c.Literal is not null && c.Literal.IsKind(SyntaxKind.StringLiteralExpression));
        foreach (var (name, literal) in stringConstants)
            constants[name] = literal!.Token.ValueText;

        return constants;
    }

    private static MethodDeclarationSyntax? LocalMethod(CompilationUnitSyntax root, string? name) =>
        name is null ? null : root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == name);

    private static string? MethodName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null,
    };

    private static string LastName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string? TypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        _ => null,
    };

    private static AuditCategoryWrite Write(SyntaxNode node, string file, string? value) =>
        new(file, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, node.ToString(), value);

    private static HashSet<string> SqlStringList(string list) =>
        SqlString().Matches(list).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static string StripSqlComments(string sql) => LineComment().Replace(BlockComment().Replace(sql, " "), string.Empty);

    [GeneratedRegex(@"audit_entries_category_check\s+CHECK\s*\(\s*category\s+IN\s*\((?<list>[^)]*)\)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryCheck();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex SqlString();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex("\"([a-z_]+)\"")]
    private static partial Regex QuotedWord();
}
