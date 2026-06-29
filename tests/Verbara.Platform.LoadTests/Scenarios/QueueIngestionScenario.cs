// QueueIngestionScenario — R5.4 S5.1.
// R5.5 P1 follow-up rewrite (2026-04-27): the original POST /api/v1/queues/
// {id}/calls endpoint never existed on the current Platform.Api surface
// (queue ingestion is SIP-driven via Asterisk PJSIP, not an HTTP REST path).
// The scenario was producing 100 % 404s under load — wrong signal entirely.
//
// New target: GET /api/v1/admin/queues (paginated list).
// What it measures: auth + tenant resolution + Postgres read of the queues
// table + JSON serialization at the same 17 req/s steady-state rate
// (~1 000 reads/min for 5 min). Scenario name preserved for report
// continuity, even though the behavior is now read-flavored.
//
// R5.5 C-L stress sweep amendment: rate + duration honor LOADTEST_RATE +
// LOADTEST_DURATION_SEC env vars so scripts/scenario-sweep.sh can drive
// per-step isolated runs to find the docker-compose knee on this hardware.
//
// True queue-ingestion measurement requires SIPp driving inbound calls
// via tests/sipp-scenarios/03-queue-join.xml against a provisioned
// Asterisk PJSIP endpoint — Phase B-L SIP-side prep, not Phase B-L HTTP.

using System.Text;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Verbara.Platform.LoadTests.Scenarios;

internal static class QueueIngestionScenario
{
    public static ScenarioProps Build(string baseUrl)
    {
        var http = Verbara.Platform.LoadTests.Infrastructure.LoadTestHttpClient.Create(baseUrl);
        var tenant = Environment.GetEnvironmentVariable("LOADTEST_TENANT") ?? "loadtest";

        // R5.5 Phase D-LK fix (2026-05-17 round 2): the previous static-
        // token approach broke at T+18m of the 24h soak because the JWT
        // access token default lifetime is 15 min. Now the scenario keeps
        // a refreshable Bearer token. If LOADTEST_REFRESH_USER + PASSWORD
        // are set (auth tenant `LOADTEST_REFRESH_TENANT` or default
        // `platform`), the scenario will re-login every 12 min in the
        // background and swap the cached token in-place. Initial token
        // comes from LOADTEST_TOKEN env (compat with existing scripts).
        // Thread-safe via volatile field; HTTP requests read latest snap.
        var initialToken = Environment.GetEnvironmentVariable("LOADTEST_TOKEN") ?? "";
        var refreshUser = Environment.GetEnvironmentVariable("LOADTEST_REFRESH_USER");
        var refreshPass = Environment.GetEnvironmentVariable("LOADTEST_REFRESH_PASSWORD");
        var refreshTenant = Environment.GetEnvironmentVariable("LOADTEST_REFRESH_TENANT") ?? "platform";

        var tokenHolder = new TokenHolder(initialToken);

        if (!string.IsNullOrEmpty(refreshUser) && !string.IsNullOrEmpty(refreshPass))
        {
            var refreshHttp = Verbara.Platform.LoadTests.Infrastructure.LoadTestHttpClient.Create(baseUrl);
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(12)); // fence-allow: LOOP-DRIVER — background token-refresh pacing during the long soak run
                        // Hand-rolled JSON to keep the harness AOT/trim-safe (no
                        // JsonContent.Create reflection). Credentials are loadtest-
                        // only and pre-escaped via env validation.
                        var body = $$"""{"email":"{{refreshUser}}","password":"{{refreshPass}}"}""";
                        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
                        {
                            Content = new StringContent(body, Encoding.UTF8, "application/json"),
                        };
                        loginReq.Headers.Add("X-Tenant-Id", refreshTenant);
                        var resp = await refreshHttp.SendAsync(loginReq);
                        if (resp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                            var newToken = doc.RootElement.GetProperty("accessToken").GetString();
                            if (!string.IsNullOrEmpty(newToken))
                                tokenHolder.Set(newToken);
                        }
                    }
                    catch { /* swallow — keep using last good token */ }
                }
            });
        }

        return Scenario.Create("queue_ingestion", async ctx =>
            {
                var req = Http.CreateRequest("GET", "/api/v1/admin/queues?pageSize=20")
                    .WithHeader("X-Tenant-Id", tenant)
                    .WithHeader("Authorization", $"Bearer {tokenHolder.Get()}");
                return await Http.Send(http, req);
            })
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_RATE"), out var r) ? r : 17,
                    interval: TimeSpan.FromSeconds(1),
                    during: TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("LOADTEST_DURATION_SEC"), out var d) ? d : 300)));
    }
}
