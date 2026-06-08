namespace Verbara.Platform.Typification;

/// <summary>What a <see cref="ConditionExpr"/> references when evaluating visibility.</summary>
public enum ConditionRef
{
    /// <summary>References another field by its stable <c>Key</c>.</summary>
    Field = 0,

    /// <summary>References a selected node by its stable <c>Code</c>.</summary>
    NodeSelected = 1,
}
