using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core.Tests;

/// <summary>
/// whatsapp-works-for-real D15 — the key-by-key merge of a channel configuration PUT and the declared
/// credential schema it is validated against.
/// </summary>
public class ChannelCredentialMergeTests
{
    private static readonly Dictionary<string, string> Stored = new()
    {
        ["AppSecret"] = "stored-secret",
        ["PhoneNumberId"] = "pn-old",
        ["WabaId"] = "waba-1",
    };

    [Fact]
    public void Apply_ShouldKeepStoredValue_WhenKeyOmitted()
    {
        var result = ChannelCredentialMerge.Apply(Stored, new Dictionary<string, string?> { ["PhoneNumberId"] = "pn-new" });

        result.Credentials["AppSecret"].Should().Be("stored-secret");
        result.Credentials["PhoneNumberId"].Should().Be("pn-new");
        result.ChangedKeys.Should().Equal("PhoneNumberId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_ShouldKeepStoredValue_WhenValueIsBlank(string blank)
    {
        var result = ChannelCredentialMerge.Apply(Stored, new Dictionary<string, string?> { ["AppSecret"] = blank });

        result.Credentials["AppSecret"].Should().Be("stored-secret");
        result.ChangedKeys.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ShouldRemoveKey_WhenValueIsNull()
    {
        var result = ChannelCredentialMerge.Apply(Stored, new Dictionary<string, string?> { ["WabaId"] = null });

        result.Credentials.Should().NotContainKey("WabaId");
        result.ChangedKeys.Should().Equal("WabaId");
    }

    [Fact]
    public void Apply_ShouldNotReportChange_WhenNullRemovesAbsentKey()
    {
        var result = ChannelCredentialMerge.Apply(Stored, new Dictionary<string, string?> { ["ApiVersion"] = null });

        result.Credentials.Should().HaveCount(3);
        result.ChangedKeys.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ShouldNotReportChange_WhenValueEqualsStored()
    {
        var result = ChannelCredentialMerge.Apply(Stored, new Dictionary<string, string?> { ["AppSecret"] = "stored-secret" });

        result.ChangedKeys.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ShouldListChangedKeysInOrdinalOrder_WhenSeveralChange()
    {
        var result = ChannelCredentialMerge.Apply(Stored, new Dictionary<string, string?>
        {
            ["WabaId"] = null,
            ["AppSecret"] = "new-secret",
            ["AccessToken"] = "token",
        });

        result.ChangedKeys.Should().Equal("AccessToken", "AppSecret", "WabaId");
    }

    [Fact]
    public void Apply_ShouldStartEmpty_WhenNothingStored()
    {
        var result = ChannelCredentialMerge.Apply(null, new Dictionary<string, string?> { ["AccessToken"] = "t", ["WabaId"] = "" });

        result.Credentials.Should().Equal(new Dictionary<string, string> { ["AccessToken"] = "t" });
        result.ChangedKeys.Should().Equal("AccessToken");
    }

    [Fact]
    public void UnknownKeys_ShouldListKeysOutsideSchema_WhenChannelDeclaresOne()
    {
        var unknown = ChannelCredentialMerge.UnknownKeys(ChannelType.WhatsApp, new Dictionary<string, string?>
        {
            ["AppSecrt"] = "x",
            ["appsecret"] = "y",
            ["AppSecret"] = "z",
        });

        unknown.Should().Equal("AppSecrt", "appsecret");
    }

    [Fact]
    public void UnknownKeys_ShouldBeEmpty_WhenChannelHasNoSchema()
    {
        ChannelCredentialMerge.UnknownKeys(ChannelType.Telegram, new Dictionary<string, string?> { ["Anything"] = "x" })
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("AccessToken", true)]
    [InlineData("AppSecret", true)]
    [InlineData("WebhookVerifyToken", true)]
    [InlineData("PhoneNumberId", false)]
    [InlineData("WabaId", false)]
    [InlineData("ApiVersion", false)]
    public void IsSecret_ShouldFollowWhatsAppSchema_WhenKeyIsCanonical(string key, bool secret)
    {
        ChannelCredentialSchemas.IsSecret(ChannelType.WhatsApp, key).Should().Be(secret);
    }

    [Fact]
    public void IsSecret_ShouldTreatEveryKeyAsSecret_WhenChannelHasNoSchema()
    {
        ChannelCredentialSchemas.For(ChannelType.WebChat).Should().BeNull();
        ChannelCredentialSchemas.IsSecret(ChannelType.WebChat, "PhoneNumberId").Should().BeTrue();
    }
}
