namespace Verbara.Platform.Api.Services;

/// <summary>
/// The dialplan contract of campaign calls (dialer-places-calls-end-to-end, design D11).
/// </summary>
public static class CampaignDialplan
{
    /// <summary>
    /// The dialplan context an answered campaign leg enters: <c>[dialer-answered]</c> hands it to the Api's Stasis
    /// app as <c>Stasis(verbara,campaign,${DIALER_CAMPAIGN_ID})</c>.
    /// </summary>
    public const string AnsweredContext = "dialer-answered";
}
