# SharpLink 0.7.10 performance evidence

## Scope

The coordinator is constructed only by `SharpLinkMultiClusterClientBuilder`; normal `SharpClientBuilder` construction and all single-client RPC invocation code are unchanged. The multi-cluster read path is limited to proxy construction. Calls made through an existing proxy use the identical child `IRpcChannel` path.

## Reproducible gate

Baseline is the implementation branch point `bfb26f0ba40c980a59e621d0a549399d7c444fba` (0.7.9). Candidate is recorded after the final implementation commit. Use Release builds with no debugger or profiler, an unchanged power mode, identical concurrency and payloads, and alternate baseline and candidate for five warmed runs.

```
dotnet build Sharplink.slnx -c Release
dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release -- --filter *UnaryBenchmarks.Rpc_Add*
```

Record raw QPS, P50, P99, error count, process allocations, and BenchmarkDotNet bytes/op for every run. P1 passes only when ordinary fixed TCP QPS median is at least 99% of baseline, P99 is at most 105%, and bytes/op do not increase. Repeat for UDS, Named Pipe and Shared Memory where supported, at c1/c8/c32/c128.

P2 compares a direct child proxy with a proxy acquired once from the multi-cluster coordinator; the measurement starts after `Get<T>`. It requires QPS >=99%, P99 <=105%, identical bytes/op, and a route lookup counter that does not increase during calls. P3 runs resolver or dynamic-registration writes in slot A while slot B invokes a static unary method; slot B requires QPS >=97%, P99 <=105%, and zero unexpected failures. P4 records build/connect/stop time, configured and actual connection count, and coordinator memory for 2, 8, and 16 slots.

## Local result status

Functional Release builds, generator tests, and unit tests are run before publishing this change. The five-round transport matrix is retained as a release-gate command and result table to be populated by the final benchmark run on the target release host; it must not be inferred from single-run development measurements.
