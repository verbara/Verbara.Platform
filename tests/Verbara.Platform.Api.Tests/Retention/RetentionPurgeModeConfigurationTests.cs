using Verbara.Platform.Api.Endpoints.Retention;
using Microsoft.Extensions.Configuration;

namespace Verbara.Platform.Api.Tests.Retention;

/// <summary>
/// openspec change <c>retention-dry-run-is-configurable</c>, task 1.1 — spec requirements "Purge mode comes from
/// configuration" (key absent, false, true) and "An unreadable purge mode stops the host" (misspelled value).
/// </summary>
public sealed class RetentionPurgeModeConfigurationTests
{
    private static IConfiguration Config(string? value)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (value is not null)
            values[RetentionPurgeModeConfiguration.Key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Read_ShouldReturnTrue_WhenKeyAbsent()
    {
        RetentionPurgeModeConfiguration.Read(Config(null)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_ShouldReturnTrue_WhenKeyEmpty(string value)
    {
        RetentionPurgeModeConfiguration.Read(Config(value)).Should().BeTrue();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    [InlineData(" false ")]
    public void Read_ShouldReturnFalse_WhenKeyIsFalse(string value)
    {
        RetentionPurgeModeConfiguration.Read(Config(value)).Should().BeFalse();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Read_ShouldReturnTrue_WhenKeyIsTrue(string value)
    {
        RetentionPurgeModeConfiguration.Read(Config(value)).Should().BeTrue();
    }

    [Theory]
    [InlineData("flase")]
    [InlineData("0")]
    [InlineData("no")]
    public void Read_ShouldThrowNamingKey_WhenValueIsNotBoolean(string value)
    {
        var act = () => RetentionPurgeModeConfiguration.Read(Config(value));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Retention:DryRun*")
            .Which.Message.Should().Contain(value);
    }

    [Fact]
    public void Key_ShouldBeRetentionDryRun()
    {
        RetentionPurgeModeConfiguration.Key.Should().Be("Retention:DryRun");
    }
}
