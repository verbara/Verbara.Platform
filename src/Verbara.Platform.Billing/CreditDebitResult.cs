namespace Verbara.Platform.Billing;

/// <summary>
/// The outcome of an atomic guarded debit against the AI-credit balance projection
/// (<see cref="ICreditLedgerStore.TryPostDebitAsync"/>). Either the debit was applied
/// (<see cref="CreditDebitOutcome.Posted"/>, exposing the new balance) or it was rejected because the
/// balance was insufficient (<see cref="CreditDebitOutcome.RejectedInsufficientBalance"/>, no ledger row
/// written, balance unchanged). A reflection-free, allocation-free readonly struct (AOT-safe). See
/// ADR-0033 / the credit-ledger-substrate spec delta.
/// </summary>
public readonly struct CreditDebitResult : IEquatable<CreditDebitResult>
{
    private CreditDebitResult(CreditDebitOutcome outcome, decimal newBalance)
    {
        Outcome = outcome;
        NewBalance = newBalance;
    }

    /// <summary>Which branch of the outcome occurred.</summary>
    public CreditDebitOutcome Outcome { get; }

    /// <summary>The post-debit balance when <see cref="Outcome"/> is <see cref="CreditDebitOutcome.Posted"/>; otherwise 0.</summary>
    public decimal NewBalance { get; }

    /// <summary><see langword="true"/> when the debit was applied.</summary>
    public bool IsPosted => Outcome == CreditDebitOutcome.Posted;

    /// <summary>The debit was applied atomically; the balance is now <paramref name="newBalance"/>.</summary>
    public static CreditDebitResult Posted(decimal newBalance) => new(CreditDebitOutcome.Posted, newBalance);

    /// <summary>The debit was rejected — the balance was below the requested amount. Nothing was written.</summary>
    public static CreditDebitResult RejectedInsufficientBalance { get; } =
        new(CreditDebitOutcome.RejectedInsufficientBalance, 0m);

    /// <inheritdoc />
    public bool Equals(CreditDebitResult other) => Outcome == other.Outcome && NewBalance == other.NewBalance;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CreditDebitResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Outcome, NewBalance);

    /// <summary>Structural equality.</summary>
    public static bool operator ==(CreditDebitResult left, CreditDebitResult right) => left.Equals(right);

    /// <summary>Structural inequality.</summary>
    public static bool operator !=(CreditDebitResult left, CreditDebitResult right) => !left.Equals(right);
}

/// <summary>The discriminator for <see cref="CreditDebitResult"/>.</summary>
public enum CreditDebitOutcome
{
    /// <summary>The debit was applied; the balance was decremented.</summary>
    Posted = 0,

    /// <summary>The debit was rejected for insufficient balance; nothing was written.</summary>
    RejectedInsufficientBalance = 1,
}
