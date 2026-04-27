// JwtScenario — R5.4 S5.1.
// Target: 2,000 req/s (login + /me) for 2 minutes. Each iteration logs in
// fresh and validates the bearer end-to-end so we measure real issuance +
// validation cost (not just cached-token lookup).

using System.Text.RegularExpressions;
using Microsoft.FSharp.Core;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Asterisk.Platform.LoadTests.Scenarios;

internal static partial class JwtScenario
{
    [GeneratedRegex("\"accessToken\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex AccessTokenRegex();

    public static ScenarioProps Build(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        return Scenario.Create("jwt_issuance_validation", async ctx =>
            {
                var loginReq = Http.CreateRequest("POST", "/api/v1/auth/login")
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(new StringContent(
                        """{"username":"loadtest","password":"loadtest"}"""));
                var loginResp = await Http.Send(http, loginReq);

                if (!loginResp.IsError &&
                    FSharpOption<HttpResponseMessage>.get_IsSome(loginResp.Payload))
                {
                    var msg = loginResp.Payload.Value;
                    var body = await msg.Content.ReadAsStringAsync();
                    var token = ExtractToken(body);

                    if (!string.IsNullOrEmpty(token))
                    {
                        var meReq = Http.CreateRequest("GET", "/api/v1/auth/me")
                            .WithHeader("Authorization", $"Bearer {token}");
                        await Http.Send(http, meReq);
                    }
                }

                return loginResp;
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: 2_000,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromMinutes(2)));
    }

    private static string ExtractToken(string responseBody) =>
        AccessTokenRegex().Match(responseBody).Groups[1].Value;
}
