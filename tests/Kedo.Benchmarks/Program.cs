using BenchmarkDotNet.Running;

// `dotnet run -c Release --project tests/Kedo.Benchmarks -- --filter *` runs everything;
// `-- --job short` for a quick pass; results land in BenchmarkDotNet.Artifacts/.
BenchmarkSwitcher.FromAssembly(typeof(Kedo.Benchmarks.ToolchainBenchmarks).Assembly).Run(args);
