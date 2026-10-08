using System.Text.RegularExpressions;

namespace Verbara.Platform.Architecture.Tests;

/// <summary>One write that can change who counts as a licensed agent and does not go through the writer.</summary>
internal sealed record LicensedAgentWriteViolation(string Path, string Where, string Why);

/// <summary>
/// licensed-agent-metering (tasks.md 5.5, design D5): finds the SQL writers of <c>agents</c> (INSERT/DELETE)
/// and of <c>users.status</c> (UPDATE … status, DELETE FROM users) that a change could reach without its
/// ledger row. Two arms:
/// <list type="number">
/// <item><b>SQL arm</b> (Storage.Postgres sources): such SQL may live only in a named constant of
/// <see cref="AllowedSqlConstants"/>; every reference to that constant must sit inside a method that takes an
/// <c>NpgsqlTransaction</c> (a transaction overload); every such overload must be invoked, with the caller's
/// <c>conn, tx</c>, from the licensed-agent writer; and no other file may invoke it except the store that
/// declares it (its standalone twin). Inline SQL of that kind anywhere else is a violation.</item>
/// <item><b>Caller arm</b> (production sources outside the storage projects): no code calls a store's
/// standalone status write (<c>UpdateAdminFieldsAsync</c>) or delete (<c>DeleteAsync</c>) on a receiver typed
/// <c>IUserStore</c> or <c>IAgentStore</c>, except the decorators of <see cref="AllowedPassThroughFiles"/>,
/// which forward to their inner store.</item>
/// </list>
/// </summary>
internal static partial class LicensedAgentWriteScanner
{
    public const string WriterFile = "PostgresLicensedAgentChangeWriter.cs";

    /// <summary>The named SQL constants allowed to write agents or users.status: (declaring file, constant).</summary>
    public static readonly (string File, string Constant)[] AllowedSqlConstants =
    [
        ("PostgresAgentStore.cs", "InsertSql"),
        ("PostgresAgentStore.cs", "DeleteSql"),
        ("PostgresUserStore.cs", "UpdateAdminFieldsSql"),
        ("PostgresUserStore.cs", "DeleteSql"),
    ];

    /// <summary>Decorators that forward a standalone write to their inner store.</summary>
    public static readonly string[] AllowedPassThroughFiles =
    [
        "CachedUserStore.cs",
        "RealtimeSyncingAgentStore.cs",
    ];

    [GeneratedRegex(@"\bINSERT\s+INTO\s+agents\b|\bDELETE\s+FROM\s+agents\b|\bDELETE\s+FROM\s+users\b|\bUPDATE\s+users\b(?:(?!\bWHERE\b).)*?\bstatus\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CountedWriteSql();

    // A string literal, or several joined with '+' (comments between the pieces are allowed).
    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""(?:\s*(?://[^\n]*\n\s*)*\+\s*(?://[^\n]*\n\s*)*""(?:[^""\\]|\\.)*"")*")]
    private static partial Regex StringExpression();

    [GeneratedRegex(@"(?:public|internal|private|protected)[^;{=]*?\b(\w+)\s*\(([^)]*)\)\s*(?:=>|\{|$)", RegexOptions.Multiline)]
    private static partial Regex MethodSignature();

    [GeneratedRegex(@"\b(?:IUserStore|IAgentStore)\??\s+(\w+)\b")]
    private static partial Regex StoreTypedIdentifier();

    /// <summary>The SQL arm over <c>(file name, source)</c> pairs of the Storage.Postgres project.</summary>
    public static IReadOnlyList<LicensedAgentWriteViolation> ScanStorage(IReadOnlyList<(string File, string Source)> files)
    {
        var violations = new List<LicensedAgentWriteViolation>();
        var overloads = new List<(string File, string Method)>();

        foreach (var (file, source) in files)
        {
            foreach (Match literal in StringExpression().Matches(source))
            {
                var sql = string.Concat(Regex.Matches(literal.Value, @"""((?:[^""\\]|\\.)*)""").Select(m => m.Groups[1].Value));
                if (!CountedWriteSql().IsMatch(sql))
                    continue;

                var head = source[..literal.Index];
                var constant = DeclaredConstantBefore(head);
                if (constant is null)
                {
                    violations.Add(new(file, EnclosingMethod(source, literal.Index) ?? "?", "writes agents or users.status in inline SQL, not in an allowed constant"));
                    continue;
                }

                if (!AllowedSqlConstants.Contains((file, constant)))
                {
                    violations.Add(new(file, constant, "is not an allowed licensed-agent SQL constant"));
                    continue;
                }

                foreach (Match reference in Regex.Matches(source, $@"\b{Regex.Escape(constant)}\b"))
                {
                    if (reference.Index >= literal.Index - 200 && reference.Index < literal.Index)
                        continue; // the declaration itself
                    var method = EnclosingMethodSignature(source, reference.Index);
                    if (method is null || !method.Value.Parameters.Contains("NpgsqlTransaction", StringComparison.Ordinal))
                        violations.Add(new(file, method?.Name ?? "?", $"uses {constant} outside a transaction overload"));
                    else
                        overloads.Add((file, method.Value.Name));
                }
            }
        }

        var writer = files.FirstOrDefault(f => f.File == WriterFile).Source ?? "";
        foreach (var (file, method) in overloads.Distinct())
        {
            if (!Regex.IsMatch(writer, $@"\.{Regex.Escape(method)}\(\s*conn\s*,\s*tx\b"))
                violations.Add(new(file, method, "is a transaction overload the licensed-agent writer never calls"));

            foreach (var (other, source) in files)
            {
                if (other == file || other == WriterFile)
                    continue;
                if (Regex.IsMatch(source, $@"\b{Regex.Escape(Path.GetFileNameWithoutExtension(file))}\.{Regex.Escape(method)}\(|\.{Regex.Escape(method)}\(\s*conn\s*,\s*tx\b"))
                    violations.Add(new(other, method, $"calls {Path.GetFileNameWithoutExtension(file)}.{method} outside the licensed-agent writer"));
            }
        }

        return violations;
    }

    /// <summary>The caller arm over <c>(file name, source)</c> pairs of production code outside the storage projects.</summary>
    public static IReadOnlyList<LicensedAgentWriteViolation> ScanCallers(IReadOnlyList<(string File, string Source)> files)
    {
        var violations = new List<LicensedAgentWriteViolation>();
        foreach (var (file, source) in files)
        {
            if (AllowedPassThroughFiles.Contains(Path.GetFileName(file)))
                continue;

            // Receivers are scoped to the method they are declared in (a parameter or a local), so a store of
            // another type that happens to share the name elsewhere in the file is not confused with them;
            // fields (underscore-prefixed) are visible in every method.
            var fields = StoreTypedIdentifier().Matches(source).Select(m => m.Groups[1].Value)
                .Where(n => n.StartsWith('_')).ToHashSet(StringComparer.Ordinal);
            var starts = MethodSignature().Matches(source).Select(m => m.Index).Append(source.Length).ToList();
            for (var i = 0; i < starts.Count - 1; i++)
            {
                var segment = source[starts[i]..starts[i + 1]];
                var receivers = StoreTypedIdentifier().Matches(segment).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                receivers.UnionWith(fields);
                foreach (var receiver in receivers)
                {
                    foreach (Match call in Regex.Matches(segment, $@"\b{Regex.Escape(receiver)}\s*\.\s*(UpdateAdminFieldsAsync|DeleteAsync)\s*\("))
                    {
                        violations.Add(new(file, EnclosingMethod(source, starts[i] + call.Index + 1) ?? "?",
                            $"calls {receiver}.{call.Groups[1].Value} directly instead of the licensed-agent writer"));
                    }
                }
            }
        }

        return violations;
    }

    // A string expression continued from a constant declared just before it (e.g. "= \n  \"...\" +").
    private static string? DeclaredConstantBefore(string head)
    {
        var lastSemicolon = Math.Max(head.LastIndexOf(';'), Math.Max(head.LastIndexOf('{'), head.LastIndexOf('}')));
        var statement = head[(lastSemicolon + 1)..];
        var m = Regex.Match(statement, @"\b(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*$", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? EnclosingMethod(string source, int index) => EnclosingMethodSignature(source, index)?.Name;

    private static (string Name, string Parameters)? EnclosingMethodSignature(string source, int index)
    {
        (string, string)? last = null;
        foreach (Match m in MethodSignature().Matches(source[..index]))
        {
            if (m.Groups[1].Value is "if" or "while" or "for" or "foreach" or "switch" or "using" or "catch" or "lock" or "return")
                continue;
            last = (m.Groups[1].Value, m.Groups[2].Value);
        }

        return last;
    }
}
