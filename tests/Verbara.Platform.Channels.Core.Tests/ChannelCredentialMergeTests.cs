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

    [Theory]
    [InlineData("ApiToken")]
    [InlineData("PhoneNumber")]
    public void IsSecret_ShouldTreatKeyAsSecret_WhenKeyIsOutsideWhatsAppSchema(string legacyKey)
    {
        // Before Web #393 the console saved the Meta token as ApiToken: a key outside the schema may hold a
        // real secret, so it is wrapped at rest like one.
        ChannelCredentialSchemas.IsSecret(ChannelType.WhatsApp, legacyKey).Should().BeTrue();
    }

    [Fact]
    public void IsSecret_ShouldBeFalse_WhenKeyIsWhatsAppDefaultQueueId()
    {
        ChannelCredentialSchemas.WhatsApp.Contains(ChannelCredentialSchemas.DefaultQueueIdKey).Should().BeTrue(
            "inbound routing reads defaultQueueId from every channel's credentials, WhatsApp included");
        ChannelCredentialSchemas.IsSecret(ChannelType.WhatsApp, ChannelCredentialSchemas.DefaultQueueIdKey).Should().BeFalse();
    }

    [Fact]
    public void UnknownKeys_ShouldIgnoreKey_WhenValueIsNull()
    {
        // A null removes a key, so it may name a stored legacy key that is outside the schema.
        var unknown = ChannelCredentialMerge.UnknownKeys(ChannelType.WhatsApp, new Dictionary<string, string?>
        {
            ["ApiToken"] = null,
            ["PhoneNumber"] = "x",
        });

        unknown.Should().Equal("PhoneNumber");
    }

    [Fact]
    public void IsSecret_ShouldTreatEveryKeyAsSecret_WhenChannelHasNoSchema()
    {
        ChannelCredentialSchemas.For(ChannelType.Telegram).Should().BeNull();
        ChannelCredentialSchemas.IsSecret(ChannelType.Telegram, "PhoneNumberId").Should().BeTrue();
    }

    // ─── WebChat: the identity token secret (design D8) ──────────────────────────────────────────────

    private const string Current = "identityTokenSecret";
    private const string Previous = "identityTokenSecretPrevious";
    private const string ReplacedAt = "identityTokenSecretReplacedAt";
    private const string S1 = "fixture-only-previous-secret-before-rotation-a4";
    private const string S2 = "fixture-only-not-a-real-secret-6f1c2a9e4b7d";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    private static ChannelCredentialMerge.Result ApplyWebChat(
        Dictionary<string, string>? stored, Dictionary<string, string?> requested) =>
        ChannelCredentialMerge.Apply(ChannelType.WebChat, stored, requested, Now);

    [Fact]
    public void IsSecret_ShouldHoldForAllFourKeys_WhenChannelIsWebChat()
    {
        var schema = ChannelCredentialSchemas.For(ChannelType.WebChat);

        schema.Should().NotBeNull();
        schema!.Keys.Select(k => k.Name).Should().Equal("defaultQueueId", Current, Previous, ReplacedAt);
        schema.Keys.Should().OnlyContain(k => k.IsSecret, "the at-rest form must stay what an image without a schema reads");
        ChannelCredentialSchemas.IsSecret(ChannelType.WebChat, "defaultQueueId").Should().BeTrue();
        ChannelCredentialSchemas.IsSecret(ChannelType.WebChat, ReplacedAt).Should().BeTrue();
        ChannelCredentialSchemas.IsSecret(ChannelType.WebChat, "WidgetKey").Should().BeTrue("a stored legacy key may hold a secret");
    }

    [Fact]
    public void Apply_ShouldRotate_WhenWebChatSecretIsReplaced()
    {
        var result = ApplyWebChat(new() { [Current] = S1, ["defaultQueueId"] = "q1" }, new() { [Current] = S2 });

        result.Credentials[Current].Should().Be(S2);
        result.Credentials[Previous].Should().Be(S1);
        DateTimeOffset.Parse(result.Credentials[ReplacedAt], System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(Now);
        result.Credentials["defaultQueueId"].Should().Be("q1");
        result.ChangedKeys.Should().Equal(Current, Previous, ReplacedAt);
    }

    [Fact]
    public void Apply_ShouldStampInUtc_WhenClockHasAnOffset()
    {
        var result = ChannelCredentialMerge.Apply(
            ChannelType.WebChat, new Dictionary<string, string> { [Current] = S1 }, new Dictionary<string, string?> { [Current] = S2 },
            Now.ToOffset(TimeSpan.FromHours(-5)));

        result.Credentials[ReplacedAt].Should().EndWith("+00:00");
    }

    [Fact]
    public void Apply_ShouldReplaceThePrevious_WhenRotatedAgainInsideTheGrace()
    {
        var result = ApplyWebChat(
            new() { [Current] = S2, [Previous] = S1, [ReplacedAt] = "2026-11-04T08:00:00.000+00:00" },
            new() { [Current] = "fixture-only-third-secret-of-this-rotation-b5" });

        result.Credentials[Previous].Should().Be(S2);
        DateTimeOffset.Parse(result.Credentials[ReplacedAt], System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(Now);
    }

    [Fact]
    public void Apply_ShouldNotRotate_WhenWebChatSecretIsFirstSet()
    {
        var result = ApplyWebChat(new() { ["defaultQueueId"] = "q1" }, new() { [Current] = S1 });

        result.Credentials.Should().NotContainKey(Previous).And.NotContainKey(ReplacedAt);
        result.ChangedKeys.Should().Equal(Current);
    }

    [Fact]
    public void Apply_ShouldChangeNothing_WhenWebChatSecretIsUnchanged()
    {
        var stored = new Dictionary<string, string> { [Current] = S1 };

        var result = ApplyWebChat(stored, new() { [Current] = S1 });

        result.Credentials.Should().Equal(stored);
        result.ChangedKeys.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_ShouldKeepEverything_WhenWebChatSecretIsBlank(string blank)
    {
        var stored = new Dictionary<string, string> { [Current] = S1, [Previous] = S2, [ReplacedAt] = "2026-11-04T08:00:00.000+00:00" };

        var result = ApplyWebChat(stored, new() { [Current] = blank, [Previous] = blank });

        result.Credentials.Should().Equal(stored);
        result.ChangedKeys.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ShouldRemovePreviousAndStamp_WhenPreviousIsRevoked()
    {
        var result = ApplyWebChat(
            new() { [Current] = S2, [Previous] = S1, [ReplacedAt] = "2026-11-04T08:00:00.000+00:00" },
            new() { [Previous] = null });

        result.Credentials.Should().Equal(new Dictionary<string, string> { [Current] = S2 });
        result.ChangedKeys.Should().Equal(Previous, ReplacedAt);
    }

    [Fact]
    public void Apply_ShouldReportNothing_WhenNothingIsLeftToRevoke()
    {
        var result = ApplyWebChat(new() { [Current] = S2 }, new() { [Previous] = null });

        result.ChangedKeys.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ShouldRemoveAllThree_WhenCurrentSecretIsRemovedAlone()
    {
        var result = ApplyWebChat(
            new() { [Current] = S2, [Previous] = S1, [ReplacedAt] = "2026-11-04T08:00:00.000+00:00", ["defaultQueueId"] = "q1" },
            new() { [Current] = null });

        result.Credentials.Should().Equal(new Dictionary<string, string> { ["defaultQueueId"] = "q1" });
        result.ChangedKeys.Should().Equal(Current, Previous, ReplacedAt);
    }

    [Fact]
    public void Apply_ShouldKeepNoGrace_WhenSecretIsReplacedAndPreviousRevokedTogether()
    {
        var result = ApplyWebChat(
            new() { [Current] = S1 },
            new() { [Current] = S2, [Previous] = null });

        result.Credentials.Should().Equal(new Dictionary<string, string> { [Current] = S2 });
        result.ChangedKeys.Should().Equal(Current);
    }

    [Fact]
    public void Apply_ShouldNotRotate_WhenChannelIsNotWebChat()
    {
        var result = ChannelCredentialMerge.Apply(
            ChannelType.WhatsApp,
            new Dictionary<string, string> { [Current] = S1 },
            new Dictionary<string, string?> { [Current] = S2 },
            Now);

        result.Credentials.Should().Equal(new Dictionary<string, string> { [Current] = S2 });
        result.ChangedKeys.Should().Equal(Current);
    }

    [Fact]
    public void ServerManagedKeys_ShouldListPreviousOnlyWithAValue_WhenChannelIsWebChat()
    {
        ChannelCredentialMerge.ServerManagedKeys(ChannelType.WebChat, new Dictionary<string, string?> { [Previous] = "anything" })
            .Should().Equal(Previous);
        ChannelCredentialMerge.ServerManagedKeys(ChannelType.WebChat, new Dictionary<string, string?> { [Previous] = null })
            .Should().BeEmpty("a null is the revoke");
        ChannelCredentialMerge.ServerManagedKeys(ChannelType.WebChat, new Dictionary<string, string?> { [Previous] = " " })
            .Should().BeEmpty("a blank keeps what is stored, as for every key");
    }

    [Theory]
    [InlineData("2026-11-04T09:00:00Z")]
    [InlineData("")]
    [InlineData(null)]
    public void ServerManagedKeys_ShouldListStamp_WhenNamedAtAll(string? value) =>
        ChannelCredentialMerge.ServerManagedKeys(ChannelType.WebChat, new Dictionary<string, string?> { [ReplacedAt] = value })
            .Should().Equal(ReplacedAt);

    [Fact]
    public void ServerManagedKeys_ShouldBeEmpty_WhenChannelIsNotWebChat() =>
        ChannelCredentialMerge.ServerManagedKeys(ChannelType.Telegram, new Dictionary<string, string?> { [ReplacedAt] = null })
            .Should().BeEmpty();

    [Theory]
    [InlineData(31, true)]
    [InlineData(32, false)]
    [InlineData(43, false)]
    [InlineData(512, false)]
    [InlineData(513, true)]
    public void WeakSecretKeys_ShouldHoldTheBounds_WhenSecretHasThisLength(int length, bool weak)
    {
        var keys = ChannelCredentialMerge.WeakSecretKeys(
            ChannelType.WebChat, new Dictionary<string, string?> { [Current] = new string('x', length) });

        if (weak)
            keys.Should().Equal(Current);
        else
            keys.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WeakSecretKeys_ShouldBeEmpty_WhenThereIsNoValueToJudge(string? value) =>
        ChannelCredentialMerge.WeakSecretKeys(ChannelType.WebChat, new Dictionary<string, string?> { [Current] = value })
            .Should().BeEmpty();

    [Fact]
    public void WeakSecretKeys_ShouldBeEmpty_WhenChannelIsNotWebChat() =>
        ChannelCredentialMerge.WeakSecretKeys(ChannelType.Telegram, new Dictionary<string, string?> { [Current] = "short" })
            .Should().BeEmpty();
}
