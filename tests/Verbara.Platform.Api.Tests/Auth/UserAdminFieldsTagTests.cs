using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Identity;
using Microsoft.Extensions.Primitives;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The entity tag of the fields an administrator edits on a user, and how an <c>If-Match</c> is
/// evaluated against it.
/// </summary>
public sealed class UserAdminFieldsTagTests
{
    private static readonly AdminFields s_fields = new("Ana", UserRole.Agent, UserStatus.Active);

    [Fact]
    public void For_ShouldReturnTheSameStrongQuotedTag_WhenTheValuesAreTheSame()
    {
        var tag = UserAdminFieldsTag.For(s_fields);

        tag.Should().StartWith("\"").And.EndWith("\"");
        tag.Should().NotStartWith("W/");
        UserAdminFieldsTag.For(new AdminFields("Ana", UserRole.Agent, UserStatus.Active)).Should().Be(tag);
    }

    [Theory]
    [InlineData("Ana María", UserRole.Agent, UserStatus.Active)]
    [InlineData("ana", UserRole.Agent, UserStatus.Active)]
    [InlineData("Ana", UserRole.Supervisor, UserStatus.Active)]
    [InlineData("Ana", UserRole.Agent, UserStatus.Suspended)]
    public void For_ShouldReturnAnotherTag_WhenAnyEditedValueDiffers(string displayName, UserRole role, UserStatus status) =>
        UserAdminFieldsTag.For(new AdminFields(displayName, role, status)).Should().NotBe(UserAdminFieldsTag.For(s_fields));

    [Fact]
    public void For_ShouldTellValueSetsApart_WhenADisplayNameSpellsOutAnotherRoleAndStatus()
    {
        // Role and status are written before the display name, so a display name cannot be crafted to
        // read as different role or status values.
        var plain = new AdminFields("x", UserRole.Agent, UserStatus.Active);
        var crafted = new AdminFields($"x\n{(int)UserRole.Admin}\n{(int)UserStatus.Active}", UserRole.Agent, UserStatus.Active);

        UserAdminFieldsTag.For(crafted).Should().NotBe(UserAdminFieldsTag.For(plain));
    }

    [Fact]
    public void For_ShouldTagTheUsersEditedFields_WhenGivenAUser()
    {
        var user = new User
        {
            UserId = Verbara.Platform.Core.EntityId.From("u-1"),
            TenantId = new Verbara.Platform.Core.TenantId("t-1"),
            Email = "ana@example.com",
            DisplayName = "Ana",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        UserAdminFieldsTag.For(user).Should().Be(UserAdminFieldsTag.For(s_fields));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_ShouldReportAbsent_WhenNoIfMatchValueIsGiven(string? value) =>
        UserAdminFieldsTag.Evaluate(value is null ? StringValues.Empty : new StringValues(value), s_fields)
            .Should().Be(IfMatchOutcome.Absent);

    [Fact]
    public void Evaluate_ShouldReportMatched_WhenTheCurrentTagIsGiven() =>
        UserAdminFieldsTag.Evaluate(UserAdminFieldsTag.For(s_fields), s_fields).Should().Be(IfMatchOutcome.Matched);

    [Fact]
    public void Evaluate_ShouldReportMatched_WhenTheCurrentTagIsOneOfSeveralInOneOrMoreHeaders()
    {
        var current = UserAdminFieldsTag.For(s_fields);

        UserAdminFieldsTag.Evaluate($"\"other\", {current}", s_fields).Should().Be(IfMatchOutcome.Matched);
        UserAdminFieldsTag.Evaluate(new StringValues(["\"other\"", current]), s_fields).Should().Be(IfMatchOutcome.Matched);
    }

    [Fact]
    public void Evaluate_ShouldReportAnyMatched_WhenTheWildcardIsGiven() =>
        UserAdminFieldsTag.Evaluate("*", s_fields).Should().Be(IfMatchOutcome.AnyMatched);

    [Theory]
    [InlineData("\"other\"")]
    [InlineData("not-an-entity-tag")]
    public void Evaluate_ShouldReportNotMatched_WhenNoGivenTagIsTheCurrentOne(string value) =>
        UserAdminFieldsTag.Evaluate(value, s_fields).Should().Be(IfMatchOutcome.NotMatched);

    [Fact]
    public void Evaluate_ShouldReportNotMatched_WhenOnlyTheWeakFormOfTheCurrentTagIsGiven() =>
        UserAdminFieldsTag.Evaluate($"W/{UserAdminFieldsTag.For(s_fields)}", s_fields).Should().Be(IfMatchOutcome.NotMatched);
}
