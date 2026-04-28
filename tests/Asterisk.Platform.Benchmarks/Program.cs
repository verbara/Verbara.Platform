// Asterisk.Platform.Benchmarks (AHH Phase 0)
//
// Entry point for BenchmarkDotNet. All benches live in *Bench.cs files and
// inherit the project's central package configuration. Each runner writes
// its report to BenchmarkDotNet.Artifacts/results/.
//
// Repro:
//   dotnet run -c Release --project tests/Asterisk.Platform.Benchmarks -- --filter '*'
//
// Filter examples:
//   --filter '*AuthHotPathBench*'
//   --filter '*Bcrypt12_Verify*'

using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
