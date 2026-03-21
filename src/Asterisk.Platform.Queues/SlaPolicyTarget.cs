namespace Asterisk.Platform.Queues;

public sealed class SlaPolicyTarget
{
    public int? AnswerWithinSeconds { get; set; }
    public int? FirstResponseWithinSeconds { get; set; }
    public int? ResolutionWithinSeconds { get; set; }
}
