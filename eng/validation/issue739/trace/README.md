# Issue 739 bounded allocation diagnostics

This directory is a validation-only helper. It changes no production code and does
not gate or claim 90% exact ownership. Keep performance timing in fresh, untraced
processes; compare the traced sample's precise B/op and throughput with matching
untraced samples only to expose observer impact.

## Reproducible commands

Use the same pinned SDK as the main harness. Install only the official Microsoft
NuGet tool, into a disposable local path, and preserve its reported version:

```sh
dotnet tool install dotnet-trace --tool-path "$PWD/artifacts/issue739/tools" \
  --version 9.0.661903 --allow-roll-forward
dotnet build eng/validation/issue739/trace/TraceDecode.csproj -c Release
python3 -m unittest discover -s eng/validation/issue739/trace -p 'test_*.py'
python3 eng/validation/issue739/trace/collect.py \
  --trace-tool "$PWD/artifacts/issue739/tools/dotnet-trace" \
  --output "$PWD/artifacts/issue739/tcp-add-c1.nettrace" --seconds 120 --max-mib 256 \
  -- dotnet eng/validation/issue739/bin/Release/net10.0/SharpLink.Benchmarks.dll \
  tcp add 1 100000 8192 tcp-add-c1 artifacts/issue739/tcp-add-c1.json
dotnet eng/validation/issue739/trace/bin/Release/net10.0/TraceDecode.dll \
  artifacts/issue739/tcp-add-c1.nettrace tcp-add-c1 artifacts/issue739/tcp-add-c1
python3 eng/validation/issue739/trace/summarize.py \
  artifacts/issue739/tcp-add-c1 artifacts/issue739/tcp-add-c1.json
```

Set `DOTNET_ROOT` to the SDK location when using a nonstandard local SDK. For a
read-only user home, use writable `HOME`, `DOTNET_CLI_HOME`, and
`NUGET_HTTP_CACHE_PATH` paths. Do not turn off vulnerability auditing to work
around an unwritable cache. The collector records the exact command, provider
profile and tool version, raw provider-table log, 256 MiB EventPipe buffer,
recording duration/size bounds, tool exit and drain-status caveat. Duration or
size termination without the final marker is an invalid measurement, not zero
allocation. The size guard is sampled every 250ms, so the limit is approximate.

Retain `.nettrace`, `.collector.log`, `.capture.json`, sample `.json`,
`.decode.json`, `.ticks.jsonl`, `.summary.json`, and the owner-rules JSON. ETLX is a
rebuildable cache. Sample files and raw traces are diagnostic artifacts, not
source files. Commands need unique filenames for each fresh process/repetition.

## Interpretation and limitations

- A = sample's `GC.GetTotalAllocatedBytes(precise: true)` delta for the combined
  client/server process. Stop marker payload must match A and operation count.
- T = sum of valid `AllocationAmount64` weights, all target-process threads,
  strictly inside the one matching Start/Stop marker pair. Older schemas without
  the field, invalid weights, or missing markers are never silently imputed.
- K = the subset of those weights assigned by the ordered owner rules. These
  classify the **sampled threshold-crossing object**, not every object in the
  roughly 100 KB interval. Each sample has at most one owner.
- Report T/A (represented sample mass), K/T (classified sample fraction), K/A
  (sample-supported mass ratio), signed A−T, T−K, and signed A−K. No ratio is
  rescaled or clamped, even if T exceeds A. None proves exact owner coverage.
- The marker interval is wider than the precise snapshot interval. It includes
  marker-adjacent counter/timing work, and allocation ticks can straddle the
  boundaries. These ratios are diagnostics, **not matched-window estimators**.
  Background allocation, unflushed partial tick intervals, allocation sampling
  bias, loss, and runtime accounting differences remain in the residual.
- EventPipe `EventsLost` is reported as runtime/parser sequence loss accounting,
  not a guarantee of completeness. Missing/unknown loss information is not zero.
  Decoder writes event schema versions, PID, marker timestamps, hashes, missing
  stack/type weights and unresolved stack weights. No native symbol server lookup
  is performed. All raw frames and type names remain in JSONL.
- `sum(weight / ObjectSize) / operations` is a potentially biased weighted
  **objects/op estimate** on the subset with v4 ObjectSize, not an object counter.
  Exact objects/op and exact owner coverage remain null. Missing ObjectSize mass
  is reported separately. Never fill it with the sample count or weight/24.
- `owners.json` deliberately starts with a few narrow, source-grounded type
  matches. Review raw top signatures before adding a rule and preserve the
  changed rules' hash. A broad `SharpLink`/`Task` catch-all manufactures confidence;
  do not add one just to exceed a threshold. Confirm leading owner hypotheses
  with focused controls/source analysis, independently of these sample weights.

The diagnostic helper's own allocations are in a separate process. Harness
markers/reporting may allocate in the target process; marker-window mismatches
are stated rather than hidden or corrected by rescaling.

Sources: [runtime GC events](https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-garbage-collection-events),
[official dotnet-trace tool](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace),
[TraceEvent implementation](https://github.com/microsoft/perfview/blob/main/src/TraceEvent/Parsers/ClrTraceEventParser.cs).

## Synthetic end-to-end parser smoke

This tests the decoder against a known array-allocation emitter, not RPC
performance. Run before the first real capture in an environment allowing local
EventPipe Unix-domain sockets:

```sh
dotnet build eng/validation/issue739/trace/Fixture/Fixture.csproj -c Release
python3 eng/validation/issue739/trace/collect.py \
  --trace-tool "$PWD/artifacts/issue739/tools/dotnet-trace" \
  --output "$PWD/artifacts/issue739/synthetic.nettrace" --seconds 20 \
  -- dotnet eng/validation/issue739/trace/Fixture/bin/Release/net10.0/Fixture.dll \
  artifacts/issue739/synthetic.json
dotnet eng/validation/issue739/trace/bin/Release/net10.0/TraceDecode.dll \
  artifacts/issue739/synthetic.nettrace synthetic-smoke artifacts/issue739/synthetic
python3 eng/validation/issue739/trace/summarize.py \
  artifacts/issue739/synthetic artifacts/issue739/synthetic.json
```

Local validation on SDK 10.0.102/runtime 10.0.2: decoder and fixture built with
zero warnings/errors; 11 Python tests passed. Live capture was **not validated**
in the local sandbox: official dotnet-trace could not create its diagnostics
Unix-domain socket (`SocketException(13): Permission denied`), including one
escalated execution attempt. CI must establish the live decode result. No trace
or synthetic allocation result was manufactured to replace that missing test.
