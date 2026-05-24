namespace Verbara.Platform.E2E.Harness.Config;

/// <summary>
/// Environment-driven configuration for the harness. Populated once by
/// <see cref="FromEnvironment"/> and treated as immutable thereafter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why env vars (no Spectre/CLI):</b> the walking skeleton runs from
/// <c>scripts/run-harness-talos.sh</c> which already exports each value
/// after resolving the kubectl port-forwards. Keeping the harness env-only
/// means the wrapper is the ONE place that knows how to address the lab
/// (port-forward orchestration, KUBECONFIG, gateway hostnames). Future
/// scenarios reuse the same shape; multi-scenario CLI (Spectre) ships
/// after the contract is proven.
/// </para>
/// </remarks>
internal sealed record HarnessConfig(
    string ApiBaseUrl,
    string RealtimeHubUrl,
    string Tenant,
    string AdminTenant,
    string Email,
    string Password,
    string PlatformAdminEmail,
    string PlatformAdminPassword,
    IReadOnlyList<string> AuditBaseUrls,
    int ClientCount,
    int EventCount,
    TimeSpan SettleDelay,
    string ReportDirectory)
{
    public static HarnessConfig FromEnvironment()
    {
        var apiBaseUrl = Required("HARNESS_API_BASE_URL");
        var hubUrl = Required("HARNESS_REALTIME_HUB_URL");
        var tenant = Required("HARNESS_TENANT");
        // PlatformAdmin users typically live in the host tenant (default "platform"),
        // not in the customer tenant the agent + hub clients operate on. Separate
        // env var so an integrator can override (e.g. when the admin user is
        // shared across two host tenants in a partner-reseller deployment).
        var adminTenant = Environment.GetEnvironmentVariable("HARNESS_ADMIN_TENANT") ?? "platform";
        var email = Required("HARNESS_AGENT_EMAIL");
        var password = Required("HARNESS_AGENT_PASSWORD");
        var adminEmail = Required("HARNESS_PLATFORMADMIN_EMAIL");
        var adminPassword = Required("HARNESS_PLATFORMADMIN_PASSWORD");
        var auditCsv = Required("HARNESS_AUDIT_BASE_URLS");

        var auditUrls = auditCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (auditUrls.Count == 0)
        {
            throw new InvalidOperationException("HARNESS_AUDIT_BASE_URLS must contain at least 1 URL.");
        }

        var clientCount = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_CLIENT_COUNT"), out var cc) && cc > 0 ? cc : 5;
        var eventCount = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_EVENT_COUNT"), out var ec) && ec > 0 ? ec : 10;
        var settleSec = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_SETTLE_SEC"), out var ss) && ss >= 0 ? ss : 5;
        var reportDir = Environment.GetEnvironmentVariable("HARNESS_REPORT_DIR") ?? "./harness-reports";

        return new HarnessConfig(
            ApiBaseUrl: apiBaseUrl.TrimEnd('/'),
            RealtimeHubUrl: hubUrl,
            Tenant: tenant,
            AdminTenant: adminTenant,
            Email: email,
            Password: password,
            PlatformAdminEmail: adminEmail,
            PlatformAdminPassword: adminPassword,
            AuditBaseUrls: auditUrls.Select(u => u.TrimEnd('/')).ToList(),
            ClientCount: clientCount,
            EventCount: eventCount,
            SettleDelay: TimeSpan.FromSeconds(settleSec),
            ReportDirectory: reportDir);
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException($"Required env var {key} is not set.");
}
