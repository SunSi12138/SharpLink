# PR753 one-pass EventPipe contract

Scope: one TCP Add c1 diagnostic experiment, not acceptance. No production source edits. Exact arms dev072a3a13fca0aa9186d2db44e38e5fc846969b07, safeac903d63c541d972f4268bb404b3c09ce63776e4, normalized1a24cc07036dd2c36f269d8df66cd81f85c14f08.

Tools: official NuGet dotnet-trace10.0.745401 and Microsoft.Diagnostics.Tracing.TraceEvent3.2.8; runtime10.0.12, same SDK and tiering/env/CPU4 policy as run37909799377. Explicit provider string:

`Microsoft-DotNETCore-SampleProfiler:0:4,Microsoft-Windows-DotNETRuntime:0x40034019:4,SharpLink-753-ProfileWindow:1:4`

CLR mask: GC1, Loader8, JIT10, Contention4000, Threading10000, JittedMethodILToNativeMap20000, Stack40000000 (hex). Informational avoids allocation ticks; this is not an allocation profiler. Default rundown retained. Buffer256MiB. Use standard `collect --providers ... --buffersize 256 --output ... -- dotnet exec APP.dll ARGS`; no collect-linux, perf, sysctl, ptrace, capabilities, or security/credential changes. Do not override sampling interval: pinned .NET10.0.12 sample-profiler code defaults to1ms; current .NET11 documentation's10ms or interval environment variable must not be assumed applicable. Record actual sample timestamps/interval distribution.

Materialize and hash exact three trees before adding only identical LoadTest marker source and boundary calls. Runtime/Client/Server entire source subtrees remain byte-identical to each pinned arm; preserve generated source/binary hashes. Build once per arm and use exactly the same diagnostic binary for traced/untraced pairs. Do not use instrumented/prior per-read counter sources.

Marker API (`SharpLink.Profiling753.ProfileWindow753.Log`, `.RunId`):
- Initialize Log and RunId once in startup, before warmup; SHARPLINK_PROFILE_RUN_ID is required in both trace modes.
- Only !isWarmup, immediately before `evidenceBefore = ...Capture()`: `Begin("tcp-add-c1")`.
- Only !isWarmup, immediately after `measurementStopped = lifecycle.StopStartingNewOperations()`: `Close("tcp-add-c1", measurementStarted, measurementStopped)`.
- Only !isWarmup, immediately after final `PerformanceEvidenceCollector.Delta` completes: `End("tcp-add-c1", success + failure)`.
- These three events include measurement+drain, with admission ticks recorded separately; CPU/allocation are independent whole-process counters covering almost the same outer interval. Never claim samples decompose those counters. Same source in all arms/modes, no per-operation calls.

Parser CLI: `dotnet ProfileParser.dll TRACE NORMALIZED_JSON OUTPUT_JSON [--calibration]`; self-tests `dotnet ProfileParser.dll --self-test`. Exit nonzero on invalid calibration/collection. Output and conversion artifacts must not overwrite prior files. Calibration CLI: `dotnet Calibration.dll REPORT_JSON`, unique SHARPLINK_PROFILE_RUN_ID supplied by runner. Normalized JSON fields:

- workload: `tcp-add-c1` or `calibration`; transport: `tcp` or `synthetic` respectively
- runId, processId, commit, runtimeVersion=`10.0.12`, stopwatchFrequency; beginTicks, startedTicks, stoppedTicks, endTicks copied from the final sidecar (SHARPLINK_PROFILE_METADATA)
- operations=items=operationsStarted=successful completed count; validationFailures=failure=cancelled=0
- processCpuMs, allocatedBytes (independent whole-process deltas)
- profileWindowSeconds=measurementSeconds+drainSeconds, measurementSeconds, drainSeconds

Main workload requests a15s admission interval (2s unchanged warmup); actual elapsed admission time must satisfy the unchanged formal lower bound14.85s and the diagnostic upper bound16s, c1 only, balanced profile, formal recording matching run37909799377, no tail observer. Require successful exact scenario and clean drain before normalizing; parser independently checks identity/count/window consistency. Report marker window minus lifecycle+drain difference; ≤1s tolerance is a guard against wrong windows, not expected precision. Full trace includes startup/warmup/rundown but attribution includes only the marked interval; report within-admission and drain sample counts separately.

Plan:3 Latin blocks D/S/N, S/N/D, N/D/S; each arm has one untraced and one traced process per block. Pair mode order U/T,T/U,U/T, fixed before running; 2:1 orientation imbalance is explicit. All18 outcomes retained; any invalid calibration/collection stops further dependent runs and preserves prior records. No retries and no second pass to tune sampling or search for a favorable result.

Mandatory fresh hosted calibration before actual arms: unique non-inlined busy functions before/inside/after marked interval; intentional Monitor and System.Threading.Lock wait contention inside; start/stop resolved callsites and positive durations for both; exactly one marker triplet/PID/runId; report matching operations/ticks; raw and converted loss0. Local parser tests exercise missing/duplicate/wrong-ID/boundary/loss rejection. Zero production Lock events is only no observed blocking contention after successful positive coverage. Lock emits after initial spin and waiter registration, so it cannot count/price uncontended fast paths, cache-line transfers, or all spinning.

Analysis per trace, never pooled as independent samples: managed/external/error/no-stack/resolution counts; full stacks and inclusive deduplicated methods; predeclared ReadOwnershipPipeReader, Lock/spin beneath reader, notification, ExecutionContext-under-reader groups; counterpart dev async state-machine reader path; GC collection context and ThreadPool context if emitted. Attribute caller roles only when a client/server stack proves it, otherwise unknown. Report sample hits/op plus raw hits and samples/sec; no ns/method or CPU percentage inferred from all-thread samples. Wait duration/RPC is summed observed thread blocking, not request latency or CPU. Boundary-crossing waits are separate, not proportionally priced. Whole-process CPU/RPC and QPS remain observer-effect diagnostics only.

Hotspot decision requires identifiable path and consistent direction in all3 blocks with reasonable counts (for sample groups ≥100hits with resolved leaf-through-outermost-reader prefix in each implicated trace and ≥90% of relevant hits meeting that resolution standard; contention groups ≥20paired waits in each implicated candidate trace); these are minimum descriptive evidence guards, not significance thresholds. Preserve3 per-process values, range and median, no p-values or per-sample CIs; samples autocorrelate. Low-count, unresolved, mixed, observer-sensitive, or no relevant hotspot => inconclusive and STOP. Even a hotspot is a lead requiring separate source review, not causal proof or authorization for a rewrite.

Estimated workload time18×17s≈5.1min; allow10–20 runner minutes for restore/build/calibration/rundown/parser. One job timeout30min, bounded child timeouts, disk budget2GiB, immutable trace/artifact chunks≤28MiB plus index/hashes to respect32MiB transfer cap. If calibration or size/time budget fails, retain failure and stop rather than weakening gates.

Observer sensitivity: for each block and candidate/dev pair, U=untraced relative delta and T=traced relative delta. Reversed sign, U=0 or |T−U|≥|U| for QPS or CPU/RPC blocks a source-cost conclusion. Preserve all overheads and values; no block is discarded.

Inlining caveat: calibration NoInlining methods prove event coverage and windowing, not visibility of production inlined/tiered fast paths. Absence of reader/lock frames cannot clear their cost. Groups use leaf-to-root callee ordering, not mere frame co-occurrence; unresolved relevant prefixes make attribution inconclusive.

Runner and artifact details: the isolated profiling workflow is path-filtered to its own files on the existing evidence branch and has contents:read permissions only. It does not rerun the prior timing or correctness workflows. SDK10.0.112 and actual runtime10.0.12 are required. No affinity or tiering options are set; the actual hosted environment must naturally expose4 CPUs. Known non-secret DOTNET_/COMPlus_/CORECLR_ settings are recorded; unknown prefixed settings fail closed with their values withheld. All arms enable compiler-generated source emission under obj for build-only provenance. Source, generated-source, runtime/copy, executable, PDB, deps and runtimeconfig hashes bind the executed files.

Collector/launcher status, validated completed child report and parser status are recorded separately. The pinned dotnet-trace launch mode propagates the child exit code unless the collector fails; on collector failure the independent child exit code is unavailable and the observation is invalid. Child console output is retained with --show-child-io. Workload success gates cover measured operations; the existing harness discards warmup outcomes.

Observer sensitivity is reported for every block and both candidate/dev contrasts. For QPS and CPU/RPC, U=candidate-untraced/dev-untraced−1 and T=candidate-traced/dev-traced−1. Zero U, a sign reversal, or |T−U|≥|U| makes that comparison observer-sensitive and source-cost attribution inconclusive. Every value and reason remains in observer-controls.json. These descriptive guards cannot create a performance acceptance pass.

The artifact includes the full originals plus a lossless deterministic tar/gzip representation split into28MiB files, an ordered part index, per-file SHA256 hashes and a whole-archive SHA256. At most32 transfer parts are uploaded individually. Overflow or packaging failure invalidates transfer completeness while the full original artifact is still retained; it never causes profiling to be repeated. Reassemble the parts in listed order, verify archive SHA256, extract without following links/traversal, then verify every indexed original file.

Local parser/runner tests and untraced functional integration are preparation checks. Local EventPipe collection was blocked by unavailable Unix-domain socket access; no local bypass is used. Therefore fresh hosted calibration remains a hard first-run prerequisite, and a calibration failure stops before the first RPC observation.

The marker lifecycle ticks must agree with the JSON duration within one100ns TimeSpan tick (absolute tolerance, no relative tolerance). This reflects Stopwatch.GetElapsedTime conversion precision, not permission to omit operations or alter durations.
