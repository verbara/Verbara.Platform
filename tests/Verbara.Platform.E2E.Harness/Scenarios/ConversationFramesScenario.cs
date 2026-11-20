using System.Diagnostics;
using System.Text.Json;
using Verbara.Platform.E2E.Harness.Audit;
using Verbara.Platform.E2E.Harness.Auth;
using Verbara.Platform.E2E.Harness.Config;
using Verbara.Platform.E2E.Harness.Hub;
using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.E2E.Harness.Scenarios;

/// <summary>A (previous state, new state) pair of one <c>OnConversationStateChanged</c> frame.</summary>
internal sealed record FrameTransition(string PreviousState, string NewState)
{
    public override string ToString() => $"\"{PreviousState}\"->\"{NewState}\"";
}

/// <summary>The declared-frames check of one conversation on one client.</summary>
internal sealed record ConversationCheck(
    string ConversationId,
    string Kind,
    bool Passed,
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Actual,
    IReadOnlyList<string> Undeclared);

/// <summary>Everything one client holds in the window, and the declared-frames verdict computed on it alone.</summary>
internal sealed record ClientRecord(
    string Client,
    string Tenant,
    int TotalFrames,
    bool DeclaredFramesPassed,
    IReadOnlyList<ConversationCheck> Conversations,
    IReadOnlyList<ConversationFrame> Frames,
    IReadOnlyList<ConversationFrame> FramesOnOtherConversations,
    int AgentFramesInWindow);

/// <summary>What the workload command wrote: the conversations it drove, in order.</summary>
internal sealed record WorkloadResult(
    bool Ok,
    IReadOnlyList<string> WebChatConversationIds,
    IReadOnlyList<string> VoiceConversationIds,
    IReadOnlyList<string> Errors,
    string RawJson);

/// <summary>Result of the conversation-frames scenario (tasks 1.1 and 4.1 of platform-drops-the-pro-agent-bridge).</summary>
internal sealed record ConversationFramesReport(
    string ScenarioName,
    DateTimeOffset StartedAt,
    DateTimeOffset WindowStart,
    DateTimeOffset WorkloadEnd,
    DateTimeOffset CompletedAt,
    bool Passed,
    bool DeclaredFramesPassed,
    bool ExactlyOncePassed,
    bool NegativeControlPassed,
    bool NoProForwardPassed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings,
    string Tenant,
    string? OtherTenant,
    int WebChatConversations,
    int VoiceCalls,
    int DeclaredFramesPerClient,
    IReadOnlyList<string> DeclaredWebChat,
    IReadOnlyList<string> DeclaredVoicePrefix,
    IReadOnlyList<string> WebChatConversationIds,
    IReadOnlyList<string> VoiceConversationIds,
    int CoreConversationForwardedForTenant,
    int ProConversationForwarded,
    IReadOnlyDictionary<string, int> OutcomesByEventType,
    IReadOnlyList<ClientRecord> Clients,
    ClientRecord? OtherTenantClient,
    string WorkloadJson,
    IReadOnlyList<RelayOutcomePage> AuditPages);

/// <summary>
/// The conversation half of the lab cell of <c>platform-drops-the-pro-agent-bridge</c> (tasks 1.1 and 4.1; design
/// D10). N clients of the workload's tenant and one client of a second tenant connect to the hub, an external
/// workload command (the lab's WebChat and voice drivers) drives C WebChat conversations and one voice call, and the
/// scenario applies the four-part rule to the <c>OnConversationStateChanged</c> frames each client holds:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><description><b>Declared frames.</b> Every tenant-A client holds, per WebChat conversation, exactly the six
/// declared frames in order, and per voice call the three declared first frames, followed by nothing or by the
/// timed close once.</description></item>
/// <item><description><b>Exactly once.</b> Every tenant-A client's total frame count is the same and equals the
/// <c>conversation.state_changed</c> Forwarded rows of the tenant in the window.</description></item>
/// <item><description><b>Negative control.</b> The other tenant's client holds 0 frames, and the declared-frames
/// verdict computed on its record alone is FAIL.</description></item>
/// <item><description><b>No Pro forward.</b> 0 Forwarded rows of the Pro type <c>conversation.state.changed</c>.</description></item>
/// </list>
/// The workload command is run with <c>bash -c</c>; it must write
/// <c>{"ok": bool, "webchat": [{"conversationId": ...}], "voice": [{"conversationId": ...}], "errors": [...]}</c>
/// to <c>HARNESS_WORKLOAD_OUT</c>. A workload that fails is a fixture error (exit 2), never a verdict.
/// </remarks>
internal static class ConversationFramesScenario
{
    public const string Name = "conversation-frames";
    public const string CoreEventType = "conversation.state_changed";
    public const string ProEventType = "conversation.state.changed";

    /// <summary>The six frames of one WebChat conversation (tasks.md 4.1; design D10).</summary>
    public static readonly IReadOnlyList<FrameTransition> DeclaredWebChat =
    [
        new(string.Empty, "Queued"),
        new("Queued", "Queued"),
        new("Queued", "Offered"),
        new("Offered", "Active"),
        new("Active", "WrapUp"),
        new("WrapUp", "Closed"),
    ];

    /// <summary>The first three frames of the voice call; later frames (the timed close) are recorded, not declared.</summary>
    public static readonly IReadOnlyList<FrameTransition> DeclaredVoicePrefix =
    [
        new(string.Empty, "Queued"),
        new("Queued", "Active"),
        new("Active", "WrapUp"),
    ];

    /// <summary>
    /// The one frame allowed after the voice call's declared three: the wrap-up timer's close, inside the window or
    /// not. It is recorded as undeclared; a second one, or any other later frame, fails the voice check.
    /// </summary>
    public static readonly FrameTransition VoiceTimedClose = new("WrapUp", "Closed");

    public static async Task<(ConversationFramesReport? Report, string? Error)> RunAsync(HarnessConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.WorkloadCommand) || string.IsNullOrEmpty(config.WorkloadOutput))
        {
            throw new InvalidOperationException("HARNESS_WORKLOAD_CMD and HARNESS_WORKLOAD_OUT are required for scenario 'conversation-frames'.");
        }

        if (string.IsNullOrEmpty(config.OtherTenant) || string.IsNullOrEmpty(config.OtherEmail) || string.IsNullOrEmpty(config.OtherPassword))
        {
            throw new InvalidOperationException("HARNESS_OTHER_TENANT, HARNESS_OTHER_EMAIL and HARNESS_OTHER_PASSWORD are required for scenario 'conversation-frames'.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var failures = new List<string>();
        var warnings = new List<string>();

        var auth = new PlatformAuthClient(config.ApiBaseUrl);
        Log($"Logging in as admin '{config.PlatformAdminEmail}' (tenant '{config.AdminTenant}')...");
        var adminToken = await auth.LoginAsync(config.PlatformAdminEmail, config.PlatformAdminPassword, config.AdminTenant, ct).ConfigureAwait(false);
        Log($"Logging in as '{config.Email}' (tenant '{config.Tenant}') for the tenant's hub clients...");
        var tenantToken = await auth.LoginAsync(config.Email, config.Password, config.Tenant, ct).ConfigureAwait(false);
        Log($"Logging in as '{config.OtherEmail}' (tenant '{config.OtherTenant}') for the negative-control client...");
        var otherToken = await auth.LoginAsync(config.OtherEmail, config.OtherPassword, config.OtherTenant, ct).ConfigureAwait(false);

        Log($"Connecting {config.ClientCount} client(s) of '{config.Tenant}' and 1 of '{config.OtherTenant}' to {config.RealtimeHubUrl}...");
        await using var pool = new SignalRClientPool(config.RealtimeHubUrl, tenantToken);
        await pool.ConnectAsync(config.ClientCount, ct).ConfigureAwait(false);
        await using var otherPool = new SignalRClientPool(config.RealtimeHubUrl, otherToken);
        await otherPool.ConnectAsync(1, ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); // fence-allow: SETTLE — let the SignalR tenant-group join settle against the live hub

        var windowStart = DateTimeOffset.UtcNow.AddMilliseconds(-500);
        pool.ResetConversationFrames();
        otherPool.ResetConversationFrames();
        var agentFramesAtStart = pool.ReceivedCounts.ToArray();
        Log($"Window opens at {windowStart:O}. Running the workload: {config.WorkloadCommand}");

        var workload = await RunWorkloadAsync(config, ct).ConfigureAwait(false);
        var workloadEnd = DateTimeOffset.UtcNow;
        if (!workload.Ok)
        {
            return (null, $"Workload failed (a fixture error, not a verdict): {string.Join("; ", workload.Errors)}");
        }

        if (workload.WebChatConversationIds.Count != config.WebChatConversations || workload.VoiceConversationIds.Count != config.VoiceCalls)
        {
            return (null, $"Workload drove {workload.WebChatConversationIds.Count} WebChat conversation(s) and {workload.VoiceConversationIds.Count} voice call(s); " +
                          $"the cell declares {config.WebChatConversations} and {config.VoiceCalls} (a fixture error).");
        }

        Log($"Workload done. Settling for {config.SettleDelay.TotalSeconds:F1}s before reading the audit.");
        await Task.Delay(config.SettleDelay, ct).ConfigureAwait(false); // fence-allow: SETTLE — settle before reading the audit on the live resource

        var auditClients = config.AuditBaseUrls.Select(url => new AuditClient(url, adminToken)).ToList();
        RelayOutcomePage[] pages;
        try
        {
            pages = await Task.WhenAll(auditClients.Select(c => c.FetchAsync(windowStart, limit: 10_000, ct))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var c in auditClients)
            {
                c.Dispose();
            }
        }

        var aggregate = MultiPodAuditAggregator.Compute(pages);
        warnings.AddRange(aggregate.EvictionWarnings);
        var coreForwarded = pages.SelectMany(p => p.Entries).Count(e =>
            e.Outcome == RelayOutcomeKind.Forwarded
            && string.Equals(e.EventType, CoreEventType, StringComparison.Ordinal)
            && string.Equals(e.TenantId, config.Tenant, StringComparison.Ordinal));
        var proForwarded = pages.SelectMany(p => p.Entries).Count(e =>
            e.Outcome == RelayOutcomeKind.Forwarded && string.Equals(e.EventType, ProEventType, StringComparison.Ordinal));

        var agentFramesNow = pool.ReceivedCounts.ToArray();
        var clients = pool.ConversationFrames
            .Select((frames, i) => Evaluate($"{config.Tenant}#{i}", config.Tenant, frames, workload, agentFramesNow[i] - agentFramesAtStart[i]))
            .ToList();
        var other = Evaluate($"{config.OtherTenant}#0", config.OtherTenant, otherPool.ConversationFrames[0], workload, 0);

        // 1. Declared frames, a positive count.
        var declaredPassed = clients.Count > 0 && clients.All(c => c.DeclaredFramesPassed);
        foreach (var c in clients.Where(c => !c.DeclaredFramesPassed))
        {
            foreach (var conv in c.Conversations.Where(v => !v.Passed))
            {
                failures.Add($"Client {c.Client}, {conv.Kind} {conv.ConversationId}: expected [{string.Join(", ", conv.Expected)}], got [{string.Join(", ", conv.Actual)}].");
            }
        }

        // 2. Exactly once against the Forwarded rows.
        var totals = clients.Select(c => c.TotalFrames).Distinct().ToList();
        var exactlyOnce = totals.Count == 1 && totals[0] == coreForwarded;
        if (!exactlyOnce)
        {
            failures.Add($"Exactly once: client totals [{string.Join(", ", clients.Select(c => c.TotalFrames))}], " +
                         $"{CoreEventType} Forwarded rows of '{config.Tenant}' {coreForwarded}.");
        }

        // 3. Negative control: 0 frames, and its own verdict FAIL.
        var negativeControl = other.TotalFrames == 0 && !other.DeclaredFramesPassed;
        if (!negativeControl)
        {
            failures.Add($"Negative control: the '{config.OtherTenant}' client holds {other.TotalFrames} frame(s); " +
                         $"its own declared-frames verdict is {(other.DeclaredFramesPassed ? "PASS (the cell cannot tell delivery from silence)" : "FAIL")}.");
        }

        // 4. No Pro forward.
        var noProForward = proForwarded == 0;
        if (!noProForward)
        {
            failures.Add($"{proForwarded} Forwarded row(s) of the Pro type '{ProEventType}' — expected 0.");
        }

        var report = new ConversationFramesReport(
            ScenarioName: Name,
            StartedAt: startedAt,
            WindowStart: windowStart,
            WorkloadEnd: workloadEnd,
            CompletedAt: DateTimeOffset.UtcNow,
            Passed: declaredPassed && exactlyOnce && negativeControl && noProForward,
            DeclaredFramesPassed: declaredPassed,
            ExactlyOncePassed: exactlyOnce,
            NegativeControlPassed: negativeControl,
            NoProForwardPassed: noProForward,
            Failures: failures,
            Warnings: warnings,
            Tenant: config.Tenant,
            OtherTenant: config.OtherTenant,
            WebChatConversations: config.WebChatConversations,
            VoiceCalls: config.VoiceCalls,
            DeclaredFramesPerClient: (DeclaredWebChat.Count * config.WebChatConversations) + (DeclaredVoicePrefix.Count * config.VoiceCalls),
            DeclaredWebChat: DeclaredWebChat.Select(t => t.ToString()).ToList(),
            DeclaredVoicePrefix: DeclaredVoicePrefix.Select(t => t.ToString()).ToList(),
            WebChatConversationIds: workload.WebChatConversationIds,
            VoiceConversationIds: workload.VoiceConversationIds,
            CoreConversationForwardedForTenant: coreForwarded,
            ProConversationForwarded: proForwarded,
            OutcomesByEventType: aggregate.OutcomesByEventType,
            Clients: clients,
            OtherTenantClient: other,
            WorkloadJson: workload.RawJson,
            AuditPages: pages);
        return (report, null);
    }

    /// <summary>
    /// The declared-frames verdict computed on one client's record alone. A WebChat conversation passes only with
    /// exactly its six declared frames in order; a voice call passes when its first three frames are the declared
    /// ones and at most the timed close <see cref="VoiceTimedClose"/> follows them, once (it is listed as undeclared;
    /// any other later frame fails). Frames on any other conversation are listed, not judged.
    /// </summary>
    internal static ClientRecord Evaluate(
        string client,
        string tenant,
        IReadOnlyList<ConversationFrame> frames,
        WorkloadResult workload,
        int agentFramesInWindow)
    {
        var checks = new List<ConversationCheck>();
        foreach (var id in workload.WebChatConversationIds)
        {
            var actual = frames.Where(f => f.ConversationId == id).Select(f => new FrameTransition(f.PreviousState, f.NewState)).ToList();
            var passed = actual.SequenceEqual(DeclaredWebChat);
            checks.Add(new ConversationCheck(id, "webchat", passed,
                DeclaredWebChat.Select(t => t.ToString()).ToList(),
                actual.Select(t => t.ToString()).ToList(),
                Undeclared: []));
        }

        foreach (var id in workload.VoiceConversationIds)
        {
            var actual = frames.Where(f => f.ConversationId == id).Select(f => new FrameTransition(f.PreviousState, f.NewState)).ToList();
            var undeclared = actual.Skip(DeclaredVoicePrefix.Count).ToList();
            var passed = actual.Count >= DeclaredVoicePrefix.Count
                && actual.Take(DeclaredVoicePrefix.Count).SequenceEqual(DeclaredVoicePrefix)
                && (undeclared.Count == 0 || undeclared.SequenceEqual([VoiceTimedClose]));
            checks.Add(new ConversationCheck(id, "voice", passed,
                DeclaredVoicePrefix.Select(t => t.ToString()).ToList(),
                actual.Select(t => t.ToString()).ToList(),
                Undeclared: undeclared.Select(t => t.ToString()).ToList()));
        }

        var known = workload.WebChatConversationIds.Concat(workload.VoiceConversationIds).ToHashSet(StringComparer.Ordinal);
        var others = frames.Where(f => !known.Contains(f.ConversationId)).ToList();
        var declaredPassed = checks.Count > 0 && checks.All(c => c.Passed);
        return new ClientRecord(client, tenant, frames.Count, declaredPassed, checks, frames, others, agentFramesInWindow);
    }

    private static async Task<WorkloadResult> RunWorkloadAsync(HarnessConfig config, CancellationToken ct)
    {
        var outPath = config.WorkloadOutput!;
        if (File.Exists(outPath))
        {
            File.Delete(outPath);
        }

        var psi = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(config.WorkloadCommand!);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the workload command.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(config.WorkloadTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new WorkloadResult(false, [], [], [$"workload did not finish within {config.WorkloadTimeout.TotalSeconds:F0}s"], string.Empty);
        }

        if (!File.Exists(outPath))
        {
            return new WorkloadResult(false, [], [], [$"workload exited {process.ExitCode} and wrote no {outPath}"], string.Empty);
        }

        var raw = await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True && process.ExitCode == 0;
        var errors = root.TryGetProperty("errors", out var errEl) && errEl.ValueKind == JsonValueKind.Array
            ? errEl.EnumerateArray().Select(e => e.ToString()).ToList()
            : [];
        if (process.ExitCode != 0)
        {
            errors.Add($"workload exit code {process.ExitCode}");
        }

        return new WorkloadResult(ok, Ids(root, "webchat"), Ids(root, "voice"), errors, raw);
    }

    private static List<string> Ids(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
                .Select(e => e.TryGetProperty("conversationId", out var id) ? id.GetString() ?? string.Empty : string.Empty)
                .ToList()
            : [];

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}");
}
