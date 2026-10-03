using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The entity tag of the fields an administrator edits on a user (<see cref="AdminFields"/>: display
/// name, role, status), for GET and PUT <c>/admin/users/{id}</c>, and the evaluation of an
/// <c>If-Match</c> against it.
/// </summary>
/// <remarks>
/// The tag is derived from those values alone, so it changes only when one of them does: a user's own
/// sign-in, failed attempts or MFA changes never make an administrator's form stale. A form whose values
/// were changed and changed back (A → B → A) matches again, which is harmless — it shows what is
/// stored. The tag is strong: <c>If-Match</c> compares strongly (RFC 9110 §13.1.1).
/// </remarks>
internal static class UserAdminFieldsTag
{
    /// <summary>The entity tag (quoted) of <paramref name="fields"/>.</summary>
    public static string For(AdminFields fields)
    {
        // Role and status as their stored integers, then the display name last, so no display name can
        // make two different value sets read the same.
        var canonical = string.Create(
            CultureInfo.InvariantCulture, $"{(int)fields.Role}\n{(int)fields.Status}\n{fields.DisplayName}");
        return $"\"{Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}\"";
    }

    /// <summary>The entity tag (quoted) of <paramref name="user"/>'s administrator-edited fields.</summary>
    public static string For(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return For(new AdminFields(user.DisplayName, user.Role, user.Status));
    }

    /// <summary>Sets the response's <c>ETag</c> to the tag of <paramref name="user"/>.</summary>
    public static void SetOn(HttpResponse response, User user)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = For(user);
    }

    /// <summary>
    /// Evaluates the request's <c>If-Match</c> against the tag of <paramref name="current"/>, the values
    /// the request read.
    /// </summary>
    public static IfMatchOutcome Evaluate(StringValues ifMatch, AdminFields current)
    {
        if (ifMatch.All(string.IsNullOrWhiteSpace))
            return IfMatchOutcome.Absent;

        // A field value that does not parse names no current tag.
        if (!EntityTagHeaderValue.TryParseList(ifMatch, out var tags) || tags is null || tags.Count == 0)
            return IfMatchOutcome.NotMatched;

        var currentTag = new EntityTagHeaderValue(For(current));
        foreach (var tag in tags)
        {
            if (tag.Equals(EntityTagHeaderValue.Any))
                return IfMatchOutcome.AnyMatched;
            if (tag.Compare(currentTag, useStrongComparison: true))
                return IfMatchOutcome.Matched;
        }

        return IfMatchOutcome.NotMatched;
    }
}

/// <summary>The result of evaluating an <c>If-Match</c> with <see cref="UserAdminFieldsTag.Evaluate"/>.</summary>
internal enum IfMatchOutcome
{
    /// <summary>No <c>If-Match</c>: the write is not conditional.</summary>
    Absent,

    /// <summary>A listed tag is the current one: the write must find the values that were read.</summary>
    Matched,

    /// <summary><c>If-Match: *</c>: any current state of the user satisfies it.</summary>
    AnyMatched,

    /// <summary>No listed tag is the current one (a weak tag never is): 412, nothing is written.</summary>
    NotMatched,
}
