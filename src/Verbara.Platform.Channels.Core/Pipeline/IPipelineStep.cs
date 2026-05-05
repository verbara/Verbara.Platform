namespace Verbara.Platform.Channels.Core.Pipeline;

public interface IPipelineStep
{
    Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct);
}
