// Verbara.Platform.Benchmarks (AHH Phase 0)
//
// Entry point for BenchmarkDotNet. All benches live in *Bench.cs files and
// inherit the project's central package configuration. Each runner writes
// its report to BenchmarkDotNet.Artifacts/results/.
//
// Repro:
//   dotnet run -c Release --project tests/Verbara.Platform.Benchmarks -- --filter '*'
//
// Filter examples:
//   --filter '*AuthHotPathBench*'
//   --filter '*Bcrypt12_Verify*'

using BenchmarkDotNet.Running;

// The bench references the Api, whose own Program would shadow this one (CS0436), so the assembly is named by a bench.
BenchmarkSwitcher.FromAssembly(typeof(Verbara.Platform.Benchmarks.ConversationClaimCostBench).Assembly).Run(args);

internal sealed partial class Program;
