using System.Text;
using Isopoh.Cryptography.Argon2;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// The stored-cost bound of <see cref="Argon2idHasher.SafeVerify"/> (openspec <c>harden-recovery-code-digest</c>,
/// design D3). A stored digest names its own cost, and Isopoh honours it, so the bound is what keeps a
/// tampered or corrupt row from making one verify allocate far more than the Api ever mints. The bound admits
/// the two parameter sets the Api has written (m = 19 MiB / t = 2 before v1.14.2, m = 12 MiB / t = 3 since)
/// and at most twice the current work (m × t ≤ 2 × 12288 × 3), and refuses the rest before evaluating.
/// </summary>
public sealed class Argon2idHasherTests
{
    private const string Secret = "ABCD2345";

    /// <summary>Anything above this was not refused before evaluation: one evaluation at the production cost allocates about 100 MiB.</summary>
    private const long NotEvaluatedBudgetBytes = 1L * 1024 * 1024;

    [Theory]
    [InlineData(24577, 3)]  // m × t one step over 2 × (12288 × 3)
    [InlineData(32769, 1)]  // memory over 32 MiB
    [InlineData(8192, 5)]   // time cost over 4
    [InlineData(65536, 10)] // the ceiling the first bound admitted (about 1.4 GiB allocated by one verify)
    public void SafeVerify_ShouldReturnFalseWithoutEvaluating_WhenStoredCostExceedsBound(int memoryKib, int timeCost)
    {
        var stored = WithParameters(Argon2idTestDigests.Good, memoryKib, timeCost);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var matched = Argon2idHasher.SafeVerify(stored, Secret);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        matched.Should().BeFalse(because: "a stored cost outside the bound is never evaluated, so it cannot match");
        allocated.Should().BeLessThan(NotEvaluatedBudgetBytes,
            because: "m={0},t={1} must be refused by the bound before Isopoh allocates its memory matrix", memoryKib, timeCost);
    }

    [Theory]
    [InlineData(19456, 2)] // v1.14.0 – v1.14.1 password hashes
    [InlineData(12288, 3)] // v1.14.2 onward, passwords and recovery codes
    public void SafeVerify_ShouldMatch_WhenDigestUsesParametersTheApiHasWritten(int memoryKib, int timeCost)
    {
        var stored = Forge(Secret, memoryKib, timeCost);

        Argon2idHasher.SafeVerify(stored, Secret).Should().BeTrue(
            because: "a password hash or recovery code the Api wrote at m={0},t={1} must keep verifying", memoryKib, timeCost);
    }

    [Fact]
    public void SafeVerify_ShouldEvaluateWithinTwiceTheProductionAllocation_WhenStoredCostIsAtTheBound()
    {
        var production = Forge(Secret, Argon2idHasher.MemoryCostKib, Argon2idHasher.TimeCost);
        var atBound = Forge(Secret, 24576, 3);

        var productionBytes = MeasureVerify(production, out var productionMatched);
        var atBoundBytes = MeasureVerify(atBound, out var atBoundMatched);

        productionMatched.Should().BeTrue();
        atBoundMatched.Should().BeTrue(because: "the bound is inclusive: m × t = 2 × 12288 × 3 is still evaluated");
        atBoundBytes.Should().BeGreaterThan(NotEvaluatedBudgetBytes, because: "a digest at the bound is really evaluated");
        atBoundBytes.Should().BeLessThanOrEqualTo(productionBytes * 5 / 2,
            because: "the most expensive stored digest the bound admits costs about twice a production verify "
                   + "(measured {0:N0} bytes at the bound vs {1:N0} at production)", atBoundBytes, productionBytes);
    }

    private static long MeasureVerify(string stored, out bool matched)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        matched = Argon2idHasher.SafeVerify(stored, Secret);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string Forge(string secret, int memoryKib, int timeCost)
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = timeCost,
            MemoryCost = memoryKib,
            Lanes = 1,
            Threads = 1,
            Password = Encoding.UTF8.GetBytes(secret),
            Salt = Encoding.UTF8.GetBytes("saltsaltsaltsalt"),
            HashLength = 32,
        };

        using var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        return config.EncodeString(hash.Buffer);
    }

    private static string WithParameters(string good, int memoryKib, int timeCost)
    {
        var parts = good.Split('$');
        return string.Join('$', parts[0], parts[1], parts[2], $"m={memoryKib},t={timeCost},p=1", parts[4], parts[5]);
    }
}
