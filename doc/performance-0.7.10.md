# SharpLink 0.7.10 performance evidence

## Scope

The coordinator is constructed only by `SharpLinkMultiClusterClientBuilder`; normal `SharpClientBuilder` construction and all single-client RPC invocation code are unchanged. The multi-cluster read path is limited to proxy construction. Calls made through an existing proxy use the identical child `IRpcChannel` path.

## Reproducible gate

Baseline is the implementation branch point `bfb26f0ba40c980a59e621d0a549399d7c444fba` (0.7.9). Candidate code is `08f83c1e76b4e5f4a9354527bb2d8d0a10100572`. Use Release builds with no debugger or profiler, an unchanged power mode, identical concurrency and payloads, and alternate baseline and candidate for five warmed runs.

```
dotnet build Sharplink.slnx -c Release
dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release -- --filter *UnaryBenchmarks.Rpc_Add*
```

Record raw QPS, P50, P99, error count, process allocations, and BenchmarkDotNet bytes/op for every run. P1 passes only when ordinary fixed TCP QPS median is at least 99% of baseline, P99 is at most 105%, and bytes/op do not increase. Repeat for UDS, Named Pipe and Shared Memory where supported, at c1/c8/c32/c128.

P2 compares a direct child proxy with a proxy acquired once from the multi-cluster coordinator; the measurement starts after `Get<T>`. It requires QPS >=99%, P99 <=105%, identical bytes/op, and a route lookup counter that does not increase during calls. P3 runs resolver or dynamic-registration writes in slot A while slot B invokes a static unary method; slot B requires QPS >=97%, P99 <=105%, and zero unexpected failures. P4 records build/connect/stop time, configured and actual connection count, and coordinator memory for 2, 8, and 16 slots.

## Local P1 sample

The following alternating runs used macOS 26.4.1 arm64, .NET 10.0.2, 10 logical processors, Server GC disabled, fixed TCP `add`, c32, 2-second warmup, and 5-second measurement. Every run completed with zero errors. Raw JSON is retained locally under `artifacts/performance/issue-10/p1/`.

| round | baseline QPS | candidate QPS | baseline P99 (us) | candidate P99 (us) |
| --- | ---: | ---: | ---: | ---: |
| 1 | 80,621 | 54,618 | 7,561 | 9,229 |
| 2 | 419,848 | 421,875 | 137 | 130 |
| 3 | 366,690 | 187,101 | 169 | 1,585 |
| 4 | 413,036 | 418,630 | 138 | 137 |
| 5 | 486,636 | 402,208 | 121 | 146 |
| median | 413,036 | 402,208 | 138 | 146 |

The local machine showed substantial short-run scheduling variation. The observed medians are 97.38% QPS and 105.80% P99 relative to baseline, so this sample does **not** clear the strict P1 release gate. It is diagnostic evidence only, not a release approval.

P1 still needs the full transport/concurrency matrix and BenchmarkDotNet allocation comparison on a stable release host. P2-P4 need their corresponding direct-child comparison, cross-slot writer-pressure, and 2/8/16-slot resource runs before a release can claim all Issue 10 performance gates. Functional Release build, Unit, Generator, Integration, PackageSmoke, 120-second Chaos, and NativeAOT smoke are verified separately.
