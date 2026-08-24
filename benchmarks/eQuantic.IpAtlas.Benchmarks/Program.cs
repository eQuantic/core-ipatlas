using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(eQuantic.IpAtlas.Benchmarks.LookupBenchmarks).Assembly).Run(args);
