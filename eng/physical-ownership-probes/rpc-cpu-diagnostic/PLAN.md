# C1 — baseline-only real user CPU attribution (PREPARATION, not authorization)

## Question and fixed scope

What consumes the current TCP RPC process's user CPU, and how much of the remaining
cost is associated with bootstrap/supervision, dispatcher, logical wrappers,
cancellation, server context, runtime, transport, or unresolved frames? This is a
three-process diagnostic, **not** a benchmark, QPS acceptance, true fixed-cost proof,
or causal proof that deleting an attributed stack would save its inclusive share.
BS1 and W1 already found no QPS gain; do not rerun them here.

Product SHA `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab`, tree
`e5b72625377ffdcf5f5470e2ddd69be12a449d8e`; SDK10.0.102,
private Microsoft.NETCore.App + Microsoft.AspNetCore.App10.0.14, pinned framework
archives and no framework roll-forward. Framework file hashes, product binary
hashes/source stamps/MVIDs, tool request/parent and runner image are retained.
The hosted run builds the exact source and isolated harness/decoder. Local
compile-only verification succeeded for module/probe/decoder with existing SDK10.0.102
(and its installed10.0.2 targeting packs), rehashed retained BS1 dev57f libraries,
and offline cached TraceEvent4.0.0. No probe, gate, capture or runtime10.0.14
execution has occurred. Compile logs/receipt are retained separately; compilation
does not establish executing runtime identity or real stack/phase coverage.

Fixed order: static-client-intercepted stream1, static-client-intercepted stream100,
dynamic-client-intercepted stream1. Each uses original real RPC Operation and cold
module/channel/wrapper/registration/ordered checksum identity checks: one worker,
one connection, loopback TCP, no request deadline, no telemetry listener, default
PGO/tiering/workstation GC. Each has10s active warmup +20s observation, checked only
after each256 completed operations. Complete the first batch past deadline.
1,000,000-operation phase cap is a failure, not an abbreviated phase. No per-call
timestamps, latency arrays, percentile output, forced GC or allocation profiling.
Cold setup/checks and shutdown remain captured but are outside observation.

## Authority and host gate

Current REQUEST is `not-authorized`. Workflow can run only on the bounded validation
branch, first-attempt push, exact message, single-parent request commit changing
only REQUEST relative to independently reviewed tool HEAD/tree. Parent/root must
release the quiet window after independent review. A passing source review is not
execution approval. No workflow_dispatch, retry or replacement.

Hosted `ubuntu-24.04`, existing perf only. Read version/kernel/paranoid/UID/effective
caps/seccomp/image metadata; require nonroot UID and zero effective caps. Exactly
one owned trivial-child command: `perf stat -e task-clock:u -- /usr/bin/true`.
Missing, denied, unsupported, not-counted, timeout or nonzero exit stops before any
SDK/product build. No sudo, sysctl, setcap, perf installation, privileged launch,
external PID attachment, alternate event or alternate host. The local environment
is known unavailable and receives **no local capability probe or installation**.
SDK/private framework setup is separately permitted build preparation, never a
perf installation or security change.

## Capture and fidelity

Exact record arguments are generated/tested in contract.py:
`perf record -e task-clock:u -F 199 --strict-freq --call-graph fp,64 --user-callchains --clockid mono -T --no-buildid --no-buildid-cache -o <new-data> -- <own-dotnet-child>`.
Default inherit remains enabled for own-child threads; no `-a`, `-p`, `--per-thread`,
kernel samples/stacks or other-process access. If host perf does not support these
frozen options, stop rather than simplify/retry. Child `DOTNET_PerfMapEnabled=3`
requests perf maps only (no jitdump). Exact child PID map/perfinfo files are copied
into the case evidence, including first failure; unresolved symbols remain unknown.
No symbol service/network lookup (`DEBUGINFOD_URLS` empty), build-id cache writes,
stack substitution or inferred logical async ancestry.

Direct-file EventPipe only, startup through shutdown, streaming with circular
buffer setting hexadecimal40 (=64MiB). Providers:
`Microsoft-Windows-DotNETRuntime:0x19:4,SharpLink-RpcCpu-Phase:0xffffffffffffffff:4`.
Keyword0x19 = decimal25 = GC1|Loader8|JIT0x10. Level4 = Informational. This excludes
Verbose5 GCAllocationTick and excludes SampleProfiler completely. Method load and
method-version/tier clues, loader, GC lifecycle/suspension and our phase events are
available. MethodJittingStarted and IL-to-native-map are Verbose5 and deliberately
unavailable: **no complete JIT compilation timeline or steady-tier warmup proof**.
Do not expand capture to fill gaps. Unexpected allocation records are disclosed,
not analyzed as allocation profiling.

Linux CLOCK_MONOTONIC nanoseconds bracket a single Environment.CpuUsage snapshot
at each phase boundary. UserTime and PrivilegedTime come from the same snapshot;
total is their sum. EventPipe marker also contains monotonic boundary/read-end
values. Perf uses mono clock and nanosecond text decoding. EventPipe timeMs is
aligned with markers with measured uncertainty, not silently treated as identical
wall clock. CPU metrics are process user/system/total microseconds divided by
completed RPC operations in the same observation interval. Phase marker and CPU
read overhead remain real diagnostic overhead, no subtraction.

Task-clock hrtimer sample period is sampling weight, **not exact measured CPU**.
Sample attribution scaled by process user CPU is explicitly an estimate. Inclusive
family-presence shares overlap and are separate from disjoint nearest-leaf shares;
unresolved frames/samples remain in denominators. fp64 may truncate or fail native
or managed unwinding; >=64-frame chains are potentially truncated. Retain losses,
unresolved symbols, unparsed records, tier/version clues, startup/warmup/teardown,
and observation-boundary ambiguity. Never drop earlier data, extend warmup, repeat
captures, report unknown as zero, or turn async physical stacks into logical ancestry.
System CPU has an independent denominator but no user-mode stack attribution.

## Bounds and retention

Per case:180s including record, decode and analysis; total capture stage600s.
Case aggregate file bytes, including live exact-PID maps, polled every100ms; stop
at256MiB observed. This is a **sampled supervisory bound, not a hard filesystem
quota**: transient overshoot and termination grace up to3s are reported. Raw perf
and EventPipe, map files, stderr, exit ledger and partial outputs are retained.
No retry after unsupported flags, permission, empty/broken trace, content/identity
failure, loss/decoding error, timeout or cap. Kill/reap only our process group.
Subsequent fixed cases do not run after first failure. Lost events and first-failure
stderr are evidence, not permission to replace a capture.

Artifact scope is only this new run's rpc-cpu directory. Never include old LF1,
wakeup archives, user secrets, CLI HOME or private signing/certificate material.
No merge, release, deletion, GitHub APPROVE or old-archive public upload is authorized.

## Primary reference checks

Pinned runtime event manifest: https://raw.githubusercontent.com/dotnet/runtime/v10.0.14/src/coreclr/vm/ClrEtwAll.man
Pinned CPU API implementation: https://raw.githubusercontent.com/dotnet/runtime/v10.0.14/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Linux.cs
EventPipe direct-file settings: https://learn.microsoft.com/dotnet/core/diagnostics/eventpipe
Perf security: https://docs.kernel.org/admin-guide/perf-security.html
Software-clock sample mechanics: https://raw.githubusercontent.com/torvalds/linux/master/kernel/events/core.c
Independent review source notes are retained separately; references do not imply
that any host guarantees perf access or complete FP stack resolution.
