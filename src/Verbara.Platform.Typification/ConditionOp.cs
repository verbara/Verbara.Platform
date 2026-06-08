namespace Verbara.Platform.Typification;

/// <summary>Comparison operator for a <see cref="ConditionExpr"/>.</summary>
public enum ConditionOp
{
    Eq = 0,
    Neq = 1,
    In = 2,
    Contains = 3,
    Exists = 4,
    GreaterThan = 5,
    LessThan = 6,
}
