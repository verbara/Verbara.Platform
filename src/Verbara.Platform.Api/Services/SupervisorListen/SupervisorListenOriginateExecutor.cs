using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Dialer.Execution;
using OriginateResult = Verbara.Sdk.Pro.Dialer.Execution.OriginateResult;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>
/// supervisor-silent-listen (design D2): the Platform-owned originate executor of the silent listen. It reuses the
/// <see cref="OriginateExecutorBase"/> type and test seam, but not the Dialer's instance:
/// <list type="bullet">
///   <item>it is built with no licence guard — supervision consumes no licence (PDR-0003 §C.6), on either destination;</item>
///   <item>it reports nothing to the dialer post-call handler or <c>DialerMetrics</c>, so a listen leg is never a call attempt;</item>
///   <item>it has no circuit breaker, so failed listens never trip the one campaign dialing and click-to-dial share.</item>
/// </list>
/// Registered under <see cref="ServiceKey"/> only; the unkeyed <see cref="OriginateExecutorBase"/> stays the Dialer's.
/// </summary>
internal sealed class SupervisorListenOriginateExecutor : OriginateExecutorBase
{
    /// <summary>The DI key of this executor.</summary>
    public const string ServiceKey = "supervisor-listen";

    private readonly VerbaraServerPool _pool;

    public SupervisorListenOriginateExecutor(VerbaraServerPool pool) : base()
    {
        ArgumentNullException.ThrowIfNull(pool);
        _pool = pool;
    }

    protected override async ValueTask<OriginateResult> ExecuteCoreAsync(OriginateAction action, string nodeId, CancellationToken ct)
    {
        var server = _pool.GetServer(nodeId);
        if (server is null)
            return new OriginateResult(false, action.ActionId, $"Node '{nodeId}' not found");

        try
        {
            var response = await server.Connection.SendActionAsync(action, ct).ConfigureAwait(false);
            return string.Equals(response.Response, "Success", StringComparison.OrdinalIgnoreCase)
                ? new OriginateResult(true, action.ActionId, null)
                : new OriginateResult(false, action.ActionId, response.Message ?? "AMI originate failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new OriginateResult(false, action.ActionId, ex.Message);
        }
    }
}
