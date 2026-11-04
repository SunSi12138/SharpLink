# SharpLink phase trace parser

Diagnostic evidence only. This tool does not run an RPC workload, calculate benchmark ratios, accept performance, or infer scheduler/off-CPU causes. Input trace and detailed output stay local. Do not upload `.nettrace`, sidecars, raw logs, or detailed CSVs. The surrounding runner may publish only its explicitly permitted compact summary.

## Build and run

Requires .NET 10 SDK. TraceEvent is pinned exactly to 4.0.0; `packages.lock.json` pins its dependencies. Restore with `dotnet restore TraceParser.csproj --locked-mode`, then build/publish normally.

`dotnet TraceParser.dll INPUT.nettrace OUTPUT_DIR PHASE_SIDECAR.jsonl`

Exit codes: 0 = trace parsed and all 54 expected markers exactly match the sidecar; 1 = parse/read failure; 2 = wrong argument count; 3 = missing, malformed, mismatched or incomplete phase sequence. A missing target method or tier is coverage uncertainty, not a parser failure. Runtime event loss is explicitly reported and makes coverage limited even if phase markers match; a zero exit code does not certify complete runtime-event coverage.

The fixed diagnostic contract is I/T, P/T, I/U, P/U, in that order, each with cell-start, warmup-end, five paired chunk markers, cell-end; enclosing process markers; sequences 0–53; per-cell warmup completion 1000, chunks of 2000, cell total 11000 and process total 44000. Timestamps must be nondecreasing and frequency fixed and positive. Every sidecar field is compared exactly with its captured phase payload. The sidecar itself is not a substitute for captured markers.

Recommended provider specification for dotnet-trace:

`Microsoft-Windows-DotNETRuntime:0x1000010019:5,SharpLink-MethodFacts-Phases:0xFFFFFFFFFFFFFFFF:5`

Runtime keywords include GC 0x1, Loader 0x8, Jit 0x10, Threading 0x10000, Compilation 0x1000000000. Compilation enables tiering lifecycle events; live method-load events carry available optimization-tier flags. Do not claim all runtime events are enabled. Preserve normal rundown so methods already compiled before capture can be recognized, but do not use rundown timestamps as JIT or code-installation times.

## Output interpretation

- `summary.json`: bounded method-name previews, provider first/last timestamps and counts, lost-event count, target coverage, runtime event counts and compact phase rows. `phaseColumns` and `phaseTargetColumns` explain array positions. Phase windows are consecutive captured markers with half-open event attribution `[start,end)`. Final process-end has no following window. Events before/after phase coverage remain in global runtime counts, not invented phases.
- `method-code-versions.csv`: relevant JIT-start/live method-load/rundown observations, exact runtime method identity, code address, available ReJITID, tier and observation timestamp. A code identity is the observed tuple of process, CLR, module, method, address and ReJITID, **not** a fabricated monotonically increasing version. ReJITID is not a tier ordinal. Version-1 method-load events do not contain ReJITID; the parser leaves it missing rather than accepting the library's compatibility fallback zero. Unknown optimization tier remains missing. Later name resolution can use the same full method identity but never backfills a later tier into an earlier event.
- `gc-pauses.csv`: separate collection spans (`GC/Start` to `GC/Stop`, not pause time) and runtime suspension spans (SuspendEEStart, SuspendEEStop, RestartEEStart, RestartEEStop). The suspension total includes suspension acquisition and restart; intermediate times identify the fully suspended portion. Suspension reason is retained and is not blindly labeled GC. Missing or out-of-order endpoints make a span incomplete. Background collection duration is never called stop-the-world time.
- `phase-runtime-summary.csv`: all phase intervals, observed counts, and clipped GC-collection and runtime-suspension overlaps. Incomplete spans overlapping a phase make the relevant overlap unavailable, rather than zero. Collection and suspension overlaps are separate and must not be summed as independent pause costs.
- `phases.jsonl`: captured marker payloads and their EventPipe-relative timestamps. Alignment uses `TraceEvent.TimeStampRelativeMSec`. Stopwatch payload is kept only for sidecar matching; no cross-process wall-clock synchronization is inferred.

Target categories match names heuristically: MethodFactsExperiment/IFactsRpc proxy and stub; SharpLink ResolveMethodCall/DispatchRpcAsync/InvokeServiceAsync; ServerGeneratedBridge pump; other PumpOutboundStreamAsync; RpcMethodFacts.ToDescriptor. Enclosing async-state-machine names also match. Full names are preserved in CSV; summary previews are capped at six names and 240 characters each and explicitly mark truncation. Inlining, ReadyToRun code, late capture, missing providers or drops can prevent observations. Empty counts mean **not observed**, never proven absent. The tool cannot establish call counts from JIT events; completed-call figures are marker assertions cross-checked against the sidecar, not independently counted RPCs.

## Official source checks (2026-11-04)

- NuGet official Microsoft-owned package: https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/4.0.0 (4.0.0 published 2026-10-14).
- Pinned TraceEvent source: https://github.com/microsoft/perfview/blob/v4.0.0/src/TraceEvent/EventPipe/EventPipeEventSource.cs verifies EventPipe constructor, file versions 3–6 and `EventsLost`.
- Pinned CLR parser: https://github.com/microsoft/perfview/blob/v4.0.0/src/TraceEvent/Parsers/ClrTraceEventParser.cs verifies optimization-tier decoding, method load field versions and ReJITID >= event version 2.
- .NET 10 manifest: https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/vm/ClrEtwAll.man verifies keyword masks and tiering lifecycle `CompilationKeyword` (events 280–284).
- Official startup tracing mechanism: https://learn.microsoft.com/dotnet/core/diagnostics/eventpipe . Used only for local synthetic testing because diagnostic IPC sockets are unavailable in this execution environment. The production runner uses dotnet-trace and ordinary EventPipe.

## Synthetic-only tests

`tests/Program.cs` emits the fixed marker contract and invokes tiny fake-name methods, ThreadPool work and 20 induced GCs. Its completed-count payloads are synthetic test values, **not 44000 executed calls**. No SharpLink assembly or RPC is used.

Build `tests/Synthetic.csproj`, then launch the resulting DLL with its output directory argument and environment variables:

- `DOTNET_EnableEventPipe=1`
- `DOTNET_EventPipeConfig=Microsoft-Windows-DotNETRuntime:1000010019:5,SharpLink-MethodFacts-Phases:FFFFFFFFFFFFFFFF:5`
- `DOTNET_EventPipeOutputPath=<absolute local directory>/synthetic.nettrace`

Run `python tests/check.py <dotnet> <TraceParser.dll> <local directory>`. Tests verify a valid capture, exact marker matching, missing/short/tampered sidecars, malformed/missing trace, usage exit, observed method/tier/ThreadPool/tiering, separated complete GC/suspension spans and phase-overlap conservation. Keep generated fixture files local only. Provider keyword zero intentionally generated no custom markers in the startup test and correctly produced exit 3.
