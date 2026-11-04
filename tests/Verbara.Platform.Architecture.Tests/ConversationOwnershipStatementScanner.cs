using System.Text.RegularExpressions;

namespace Verbara.Platform.Architecture.Tests;

/// <summary>One SQL statement that writes a conversation's owner without the shared <c>queue_id</c> rule.</summary>
internal sealed record OwnershipStatementViolation(string Where, string Sql);

/// <summary>
/// contact-history-opens-by-assignment task 1.6 (design D7, fable P3): every SQL statement of
/// <c>PostgresConversationStore</c> that writes <c>owner_kind</c> (an <c>INSERT INTO conversations</c> naming the
/// column, an <c>UPDATE conversations … SET owner_kind =</c>, or an upsert's <c>DO UPDATE SET owner_kind =</c>) must
/// also write <c>queue_id</c> through the shared constant <see cref="CaseConstant"/>. A statement that moved a
/// conversation to a queue without it (the planned conditional ownership moves) would leave the previous queue in place.
/// </summary>
/// <remarks>
/// A statement is a C# string expression: string literals (plain or interpolated), and identifiers, joined by
/// <c>+</c>, with line comments allowed between the pieces. Its SQL is the concatenated literal text; the constant
/// counts only when the expression names it, so a copy of its text does not.
/// </remarks>
internal static partial class ConversationOwnershipStatementScanner
{
    public const string StoreFile = "PostgresConversationStore.cs";

    /// <summary>The constant that holds the <c>queue_id</c> CASE, declared in <see cref="StoreFile"/>.</summary>
    public const string CaseConstant = "QueueIdWriteCase";

    // A literal ("…" or $"…"), then any number of '+'-joined literals or identifiers (comments between allowed).
    [GeneratedRegex(@"\$?""(?:[^""\\]|\\.)*""(?:\s*(?://[^\n]*\n\s*)*\+\s*(?://[^\n]*\n\s*)*(?:\$?""(?:[^""\\]|\\.)*""|\b[A-Z]\w*\b(?!\s*\()))*")]
    private static partial Regex StringExpression();

    [GeneratedRegex(@"\$?""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\+\s*(?://[^\n]*\n\s*)*\b([A-Z]\w*)\b")]
    private static partial Regex JoinedIdentifier();

    [GeneratedRegex(
        @"\bINSERT\s+INTO\s+conversations\s*\([^)]*\bowner_kind\b" +
        @"|\bUPDATE\s+conversations\b(?:(?!\bWHERE\b).)*?\bowner_kind\s*=" +
        @"|\bDO\s+UPDATE\s+SET\b(?:(?!\bWHERE\b).)*?\bowner_kind\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OwnershipWrite();

    [GeneratedRegex(@"\bqueue_id\b", RegexOptions.IgnoreCase)]
    private static partial Regex QueueIdColumn();

    /// <summary>Every ownership-writing statement of <paramref name="source"/>, with whether it carries the rule.</summary>
    public static IReadOnlyList<(string Sql, bool CarriesTheRule)> OwnershipStatements(string source)
    {
        var found = new List<(string, bool)>();
        foreach (Match expression in StringExpression().Matches(source))
        {
            var sql = string.Concat(Literal().Matches(expression.Value).Select(m => m.Groups[1].Value));
            if (!OwnershipWrite().IsMatch(sql))
                continue;

            var namesTheConstant = JoinedIdentifier().Matches(expression.Value)
                .Any(m => m.Groups[1].Value == CaseConstant);
            found.Add((sql, namesTheConstant && QueueIdColumn().IsMatch(sql)));
        }

        return found;
    }

    /// <summary>The ownership-writing statements of <paramref name="source"/> that do not carry the rule.</summary>
    public static IReadOnlyList<OwnershipStatementViolation> Scan(string source) =>
    [
        .. OwnershipStatements(source)
            .Where(s => !s.CarriesTheRule)
            .Select(s => new OwnershipStatementViolation(Excerpt(s.Sql), s.Sql)),
    ];

    private static string Excerpt(string sql) => sql.Length <= 80 ? sql : sql[..80] + "…";
}
