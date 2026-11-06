namespace Verbara.Platform.Switchboard.Tests;

/// <summary>
/// conversations-return-to-last-agent task 1.6 (design D3): the metadata keys and outcome values an integrator reads on
/// <c>GET /api/v1/conversations/{id}</c> are the preference fixture's, verbatim.
/// </summary>
public sealed class ConversationOfferPreferenceKeysTests
{
    [Fact]
    public void PreferenceKeys_ShouldBeTheFixtureNames_WhenAnIntegratorReadsTheConversation()
    {
        ConversationOffer.PreferredAgentIdKey.Should().Be("_preferredAgentId");
        ConversationOffer.PreferredAgentOutcomeKey.Should().Be("_preferredAgentOutcome");
        new[] { ConversationOffer.PreferredOutcomeOfferedPreferred, ConversationOffer.PreferredOutcomeFallback }
            .Should().Equal("offered-preferred", "fallback");
    }
}
