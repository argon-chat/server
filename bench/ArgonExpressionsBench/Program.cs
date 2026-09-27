using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Argon.Expressions.Bench.OutlineBenchmarks).Assembly).Run(args);
