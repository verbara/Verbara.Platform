using System.Text;
using System.Text.RegularExpressions;

namespace Verbara.Platform.Governance.Tests;

/// <summary>One directive of an nginx configuration file: its arguments, and its children when it opens a block.</summary>
internal sealed record NginxDirective(string Name, IReadOnlyList<string> Args, IReadOnlyList<NginxDirective>? Children, int Line);

/// <summary>
/// Pure, I/O-free check that an nginx configuration writes no credential to its logs. The SSE stream
/// (<c>?token=</c>), the SignalR hub (<c>id=</c>, <c>access_token=</c>), the OIDC callback (<c>code=</c>)
/// and the reset link (<c>/reset-password?token=</c>, also seen later as a Referer) all put a secret in the
/// query string. An access-log format may therefore log the path but never the query, the raw request
/// line, the raw Referer, or a credential-bearing header, cookie or body (<see cref="Scan"/>); and an error
/// log, whose text no directive can change, must write nowhere (<see cref="ScanErrorLogs"/>).
/// </summary>
/// <remarks>
/// The file is included inside the http block of the image's own <c>nginx.conf</c>, whose
/// <c>access_log … main</c> logs <c>$request</c> (the raw request line, query included) and whose main-level
/// <c>error_log … notice</c> receives every message logged while a request is handled. nginx inherits either
/// log only into a level that declares none, and several directives on one level all write, so the only
/// way to replace the image's is for every server block to declare its own.
/// </remarks>
internal static partial class NginxAccessLogScanner
{
    /// <summary>The one error-log destination that writes nothing.</summary>
    private const string Discard = "/dev/null";

    private static readonly HashSet<string> BannedVariables = new(StringComparer.Ordinal)
    {
        "request",            // the raw request line: method, path AND query
        "request_uri",        // path AND query
        "args",
        "query_string",
        "http_referer",       // a Referer carries the previous page's query (the reset page's ?token=)
        "request_body",
        "http_authorization",
        "http_cookie",
        "http_x_api_key",
        "http_x_service_key",
        "sent_http_location", // a redirect can carry a token (the OIDC callback's hand-off to the SPA)
        "upstream_http_location",
        "sent_http_set_cookie",
        "upstream_http_set_cookie",
    };

    private static readonly string[] BannedVariablePrefixes =
    [
        "arg_",    // one query-string argument, e.g. $arg_token
        "cookie_", // one cookie
    ];

    /// <summary>Every way the configuration can write a credential to an access log; empty when it cannot.</summary>
    public static IReadOnlyList<string> Scan(string config)
    {
        var directives = Parse(config);
        var violations = new List<string>();

        var formats = new Dictionary<string, NginxDirective>(StringComparer.Ordinal);
        foreach (var format in Descendants(directives).Where(d => d.Name == "log_format" && d.Args.Count > 0))
        {
            formats[format.Args[0]] = format;
            foreach (var variable in BannedVariablesIn(FormatString(format)))
            {
                violations.Add(
                    $"log_format '{format.Args[0]}' (line {format.Line}) writes ${variable}, which can carry a credential.");
            }
        }

        foreach (var accessLog in Descendants(directives).Where(d => d.Name == "access_log" && d.Args is not ["off"]))
        {
            if (accessLog.Args.Count < 2)
            {
                violations.Add(
                    $"access_log (line {accessLog.Line}) names no log_format, so nginx writes 'combined', which logs $request.");
            }
            else if (!formats.ContainsKey(accessLog.Args[1]))
            {
                violations.Add(
                    $"access_log (line {accessLog.Line}) names log_format '{accessLog.Args[1]}', which this file does not " +
                    "define (the image's own 'main' and nginx's 'combined' both log $request).");
            }
        }

        var serversWithoutAccessLog = Descendants(directives)
            .Where(d => d.Name == "server" && d.Children is not null && !d.Children.Any(c => c.Name == "access_log"));
        foreach (var server in serversWithoutAccessLog)
        {
            violations.Add(
                $"server block (line {server.Line}) declares no access_log of its own, so it inherits the image's " +
                "default 'main' format, which logs $request.");
        }

        return violations;
    }

    /// <summary>
    /// Every way the configuration can write a request line to an error log; empty when it cannot. nginx adds
    /// the request line, the upstream URL and the Referer, query strings included, to every message it logs
    /// while it handles a request, at every level, so an error log is safe only when it writes nowhere.
    /// </summary>
    public static IReadOnlyList<string> ScanErrorLogs(string config)
    {
        var directives = Parse(config);

        var violations = Descendants(directives)
            .Where(d => d.Name == "error_log" && d.Args is not [Discard, ..])
            .Select(errorLog =>
            {
                var destination = errorLog.Args.Count > 0 ? errorLog.Args[0] : "(none)";
                var level = errorLog.Args.Count > 1 ? errorLog.Args[1] : "error";
                return $"error_log '{destination}' at level '{level}' (line {errorLog.Line}) writes the request line, " +
                    $"the upstream URL and the Referer, query strings included, with {RequestMessagesAt(level)}.";
            })
            .ToList();

        violations.AddRange(Descendants(directives)
            .Where(d => d.Name == "server" && d.Children is not null && !d.Children.Any(c => c.Name == "error_log"))
            .Select(server =>
                $"server block (line {server.Line}) declares no error_log of its own, so it inherits the image's " +
                "'error_log /var/log/nginx/error.log notice', which writes the request line whenever a proxied " +
                "request fails."));

        return violations;
    }

    /// <summary>The messages about a request that an error log at <paramref name="level"/> still writes.</summary>
    private static string RequestMessagesAt(string level) => level switch
    {
        "crit" => "a temporary file nginx cannot write (crit) and worker_connections exhausted while it connects " +
            "to an upstream (alert)",
        "alert" => "worker_connections exhausted while nginx connects to an upstream (alert)",
        "emerg" => "a memory allocation that fails while nginx handles a request (emerg)",
        "error" => "every failed proxied request: connection refused or reset, a timeout, an upstream that closes " +
            "without answering (error)",
        _ => "every failed proxied request (error) and every response or request body buffered to a temporary " +
            "file (warn)",
    };

    /// <summary>Parses nginx configuration text into its directive tree.</summary>
    public static IReadOnlyList<NginxDirective> Parse(string config)
    {
        var tokens = Tokenize(config);
        var position = 0;
        var directives = ParseBlock(tokens, ref position, topLevel: true);
        return directives;
    }

    /// <summary>The configuration's directives at every depth, parents before children.</summary>
    public static IEnumerable<NginxDirective> Descendants(IEnumerable<NginxDirective> directives)
    {
        foreach (var directive in directives)
        {
            yield return directive;
            if (directive.Children is null)
                continue;
            foreach (var child in Descendants(directive.Children))
                yield return child;
        }
    }

    private static string FormatString(NginxDirective logFormat) =>
        string.Concat(logFormat.Args.Skip(1).Where(arg => !arg.StartsWith("escape=", StringComparison.Ordinal)));

    private static IEnumerable<string> BannedVariablesIn(string format)
    {
        foreach (Match match in VariableReference().Matches(format))
        {
            var name = match.Groups["braced"].Success ? match.Groups["braced"].Value : match.Groups["bare"].Value;
            if (BannedVariables.Contains(name)
                || BannedVariablePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                yield return name;
            }
        }
    }

    private static List<NginxDirective> ParseBlock(List<Token> tokens, ref int position, bool topLevel)
    {
        var directives = new List<NginxDirective>();
        var words = new List<string>();
        var line = 0;
        while (position < tokens.Count)
        {
            var token = tokens[position++];
            switch (token.Kind)
            {
                case TokenKind.Word:
                    if (words.Count == 0)
                        line = token.Line;
                    words.Add(token.Text);
                    break;
                case TokenKind.Semicolon:
                    if (words.Count > 0)
                        directives.Add(new NginxDirective(words[0], words.Skip(1).ToList(), null, line));
                    words.Clear();
                    break;
                case TokenKind.OpenBrace:
                    if (words.Count == 0)
                        throw new FormatException($"Line {token.Line}: a block opens without a directive name.");
                    var children = ParseBlock(tokens, ref position, topLevel: false);
                    directives.Add(new NginxDirective(words[0], words.Skip(1).ToList(), children, line));
                    words.Clear();
                    break;
                case TokenKind.CloseBrace:
                    if (topLevel)
                        throw new FormatException($"Line {token.Line}: '}}' closes no block.");
                    if (words.Count > 0)
                        throw new FormatException($"Line {token.Line}: directive '{words[0]}' is not terminated by ';'.");
                    return directives;
            }
        }

        if (!topLevel)
            throw new FormatException("A block is not closed before the end of the file.");
        if (words.Count > 0)
            throw new FormatException($"Line {line}: directive '{words[0]}' is not terminated by ';'.");
        return directives;
    }

    private static List<Token> Tokenize(string config)
    {
        var tokens = new List<Token>();
        var line = 1;
        var i = 0;
        while (i < config.Length)
        {
            var c = config[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#')
            {
                // A '#' starts a comment only where a token could start; inside a quoted string it is text.
                while (i < config.Length && config[i] != '\n')
                    i++;
            }
            else if (c is ';' or '{' or '}')
            {
                tokens.Add(new Token(c switch
                {
                    ';' => TokenKind.Semicolon,
                    '{' => TokenKind.OpenBrace,
                    _ => TokenKind.CloseBrace,
                }, new string(c, 1), line));
                i++;
            }
            else
            {
                var start = line;
                var text = new StringBuilder();
                while (i < config.Length && !char.IsWhiteSpace(config[i]) && config[i] is not (';' or '{' or '}'))
                {
                    if (config[i] is '"' or '\'')
                    {
                        var quote = config[i++];
                        while (i < config.Length && config[i] != quote)
                        {
                            if (config[i] == '\\' && i + 1 < config.Length)
                                i++;
                            if (config[i] == '\n')
                                line++;
                            text.Append(config[i++]);
                        }

                        if (i >= config.Length)
                            throw new FormatException($"Line {start}: a quoted string is not closed.");
                        i++; // the closing quote
                    }
                    else if (config[i] == '$' && i + 1 < config.Length && config[i + 1] == '{')
                    {
                        // ${name}: a variable with braces, valid in a bare word.
                        while (i < config.Length && config[i] != '}')
                            text.Append(config[i++]);
                        if (i < config.Length)
                            text.Append(config[i++]);
                    }
                    else
                    {
                        text.Append(config[i++]);
                    }
                }

                tokens.Add(new Token(TokenKind.Word, text.ToString(), start));
            }
        }

        return tokens;
    }

    [GeneratedRegex(@"\$(?:\{(?<braced>[A-Za-z0-9_]+)\}|(?<bare>[A-Za-z0-9_]+))")]
    private static partial Regex VariableReference();

    private enum TokenKind
    {
        Word,
        Semicolon,
        OpenBrace,
        CloseBrace,
    }

    private sealed record Token(TokenKind Kind, string Text, int Line);
}
