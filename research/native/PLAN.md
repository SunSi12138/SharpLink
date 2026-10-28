# Fenced wakeup: frozen natural-tier native mechanism collection v2 (review revision)

**Status: source/design only; C# probe and decoder uncompiled, collector unexecuted.**
This is a request for independent tool/plan review, not permission to start probes.
The product candidate remains the single-variable fenced read-mostly Signal change.
No Tier1, ARM64, microcost, or QPS/P99 benefit is established by this plan.

## 1. Provenance and reviewed prior evidence

- D: `f724c7aaabdeb5e02bec23f32f6b3c709860e97c`.
- C hosted source: `e5a35b9bf63d162c3077aec389d402e28bd2fb53`, complete tree
  `ea401a81c4b5a52966f4281acc348f838b9fc935`, exactly equal to local reviewed
  `c17f434e8ec8d019625e64c7f51a4c8eb8e55981`; product is exactly
  `758de8eba8ac0143070ad9fb46db4aa63e73575e` (later commits documentation only).
- Independent review `/workspace/shared/sharplink-fenced-wakeup-independent-review-c17f434.md`
  PASS is research correctness/mechanism scope, not performance acceptance.
- Existing 88/88, both passing guards, initial wrapper-call native dump and final
  Tier0 `lock or [rsp],0` + state reread remain untouched. No backfilled result.
- Harness-only fix `0ce2cad`: four offline fixture groups pass; future runs now
  reject nonzero guards/info/build/tests and missing/duplicate/partial/skipped counts.
  Its stricter new dotnet-info exit record does not retroactively invalidate or
  fabricate a record for the independently reviewed old run.

## 2. Frozen matrix and runner identity

`plan.json` is authoritative: four cells (default/timed × c1/c8), D then C for
 each cell on each architecture, **16 fresh processes total**, no retries/replacements.
Each process performs exactly 20,000 validated unary calls, a fixed 250ms quiet
interval, then 20,000 calls; checksum and physical server-stop ledger required.
No adaptive warmup or forced tier. Absence of naturally generated Tier1 is an
**incomplete mechanism result**, not permission to rerun until desired code appears.
All failed builds, timeouts, incomplete traces and native bodies are retained.
Stop the matrix on an infrastructure/build/protocol failure; review an amended
plan before continuing. A missing Tier1 body is retained as a coverage gap while
remaining originally frozen cells may continue.

| RID | Standard hosted label | Required native process/OS |
|---|---|---|
| linux-x64 | ubuntu-24.04 | Linux x86_64 / X64 |
| osx-arm64 | macos-14 | Darwin arm64 / Arm64 |

Runner labels are not architecture evidence. Save `uname -a`, `sw_vers` or
`/etc/os-release`, CPU model, affinity/cgroup CPU limits, `dotnet --info`, runner
image version, and actual ProcessArchitecture/OSArchitecture. The probe rejects
Rosetta/emulated architecture. Standard labels only; no larger/paid-runner SKU or
security changes. Standard private-repository usage may consume billed minutes;
no workflow or hosted run has been created by this research agent.

Runtime is **10.0.14**, SDK **10.0.102**, both architectures identical versions.
10.0.14 is the official 2026-10-25 security release in checked metadata. Previous
10.0.2 evidence is not pooled with this new-runtime experiment. Official archive
URLs/SHA512 for SDK, runtime, ASP.NET runtime are frozen in `artifact-lock.json`.
Download only after approval; run `verify_archives.py RID ARCHIVE_DIRECTORY`
before extraction/execution; retain archives and verification JSON. Extract into
an empty private dotnet root, SDK first then both 10.0.14 runtimes. Save archive
and extracted host/CoreCLR/JIT/framework file hashes; version text alone is not
an official-runtime identity proof. Do not reuse preinstalled runner dotnet.

Sources: [GitHub standard runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners),
[official .NET release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
These mutable sources are provenance pointers; the committed SHA512 lock freezes
this experiment and must be independently cross-checked before acquisition.

## 3. Actual producer/consumer paths, not a stub benchmark

`probe/Program.cs` uses generated RPC proxies/dispatch, tracked cancellation,
normal client timeout, loopback TCP, both endpoint SendPumps and physical shutdown.
It does not reference WakeupSignal directly or replace queues/schedulers. c8
creates concurrent real producers on one connection. Timed cells configure
64KiB / 250µs flush policies on both endpoints. This is diagnostic path coverage,
not a claim c8 always executes the coalesced branch or triggers every timed race.
Requests use a bounded framework cancellation token; no product counters or
instrumentation are added. No `NoInlining`, `PrepareMethod`, tier controls,
ReadyToRun disabling, GC tuning, forced core counts, or sleeps inside product code.
The fixed phase delay is harness-only and is not used to select a favorable tier.

Default and timed cells are necessary but not sufficient: reviewer must confirm
from actual native bodies and trace that the relevant producer, ordinary consumer,
and timed consumer paths were materialized. TCP diagnostic coverage is not TLS,
IPC, streaming, ALC, cancellation-storm or fault-lifecycle product acceptance.

## 4. Build preparation and immutable receipt (separate approved stage)

Before any collector execution, review and approve this tool tree. After approval:

1. Create separate clean D and C worktrees, verify exact commits and `git diff`
   product scope. Copy the **same reviewed** probe/decoder tree outside product
   sources; do not edit either product. Preserve generated sources and build logs.
2. Build with verified SDK102: `dotnet build <probe.csproj> -c Release -m:1
   -nr:false -p:UseSharedCompilation=false -p:EvidenceSourceRoot=<exact-worktree>`.
   Build decoder similarly. Pin probe/decoder RuntimeFrameworkVersion14 with
   RollForward Disable; this selects runtime, not tier policy. No tier/GC MSBuild
   overrides. A compilation error requires a reviewed tooling revision, not a
   silent live edit or product change. No C# build has happened yet.
3. Retain exact command/environment, source/tool tree hashes, generated sources,
   output DLL/PDB/runtimeconfig/deps hashes, NuGet package/lock identities and
   package-audit output. Record product assembly MVIDs and source commit.
4. Independently audit a `build-receipt.json` per arm/architecture:
   `schema:2`, `arm`, `rid`, full `sourceCommit`, `sdkVersion`, `runtimeVersion`, absolute
   `dotnet`, `probeDll`, `sourceRoot`, `generatedRoot`, `buildLog`,
   `buildSucceeded:true`, `productDiffReviewed:true` (actual JSON booleans),
   `archiveVerification:[{component,path,sha512,officialUrl}]`, `files:[{path,sha256}]`.
   These are the complete allowed top-level keys; extra/duplicate file entries fail.
   Reviewer must communicate the exact receipt SHA256 separately before execution.
   Files must include all probe/runtime DLL/PDB/config/deps files, generator and
   generated source inputs, host/CoreCLR/JIT, archive verifier result, build log,
   source/tool provenance. Collector requires the externally reviewed receipt digest, validates strict schema and
   actual booleans, enumerates the required file closure and rehashes it. It rehashes
   all three archive byte streams against the official SHA512 lock and directly
   compares installed runtime/host/CoreCLR/JIT/framework bytes against verified
   archive members, without extracting anything. A rehashed counterfeit JIT is
   rejected even inside an otherwise self-consistent receipt. This remains a
   reviewed provenance chain, not a cryptographic source-to-binary build attestation.

Run only after explicit plan/tools review:

```
python3 collect.py --reviewed-plan-sha256 <plan-hash> \
  --reviewed-tools-sha256 <tool-lock-hash> \
  --reviewed-receipt-sha256 <independently-reviewed-receipt-hash> --receipt <reviewed-receipt> \
  --cell default-c1 --output <NEW-evidence-directory>
```

Collector refuses preexisting output, changed tool/plan/binary hashes, foreign
architecture, version mismatches and inherited COMPlus/DOTNET tuning. CORECLR_ /
COR_ profiling, COREHOST_ overrides, LD_ / DYLD_ native-loader overrides and MONO_ overrides are rejected
as well, even when their value looks disabled. Child DOTNET_ROOT is pinned to the
reviewed executable root, not inherited. It writes
launch metadata, stdout/stderr, native dump, phase ledger, startup EventPipe,
exit/failure status and SHA256 manifest. Runtime-startup file tracing avoids
requiring a diagnostic socket. No external attach or kernel tracing is requested.
Timeout180s and each raw-file cap64MiB are fixed stop conditions; overshoot between
100ms supervisor checks is possible and not a hard resource isolation boundary.
Supervisor observation is never used to report latency or throughput. A killed
trace may be undecodable; retain it as failed evidence. Matrix orchestration is
manual and must follow frozen order; collector does not silently retry.

## 5. Natural Tier1 / inlining / weak-memory review oracle

Observation-only environment: `DOTNET_JitDisasm=*`, JitStdOutFile, startup
EventPipe with fixed runtime+phase providers. Runtime mask **0x10400210b9**
retains the original mask and adds JitTracing **0x1000** plus Compilation
**0x1000000000**. This covers promised inline decisions and tier compilation
events; IL/native mapping keyword **0x20000** remains enabled. Capture all emitted code because
callee filtering can hide inlined bodies. No code-generation tier/ISA knobs.
Instrumented Tier0 and Tier1 code versions must not be conflated. Save raw trace
and decoder output; correlate MethodLoad/Jitting/inlining/tier flags, native
addresses, phase timestamps and all versions. Decoder explicitly serializes every
MethodILToNativeMapTraceData ILOffset(i)/NativeOffset(i) pair, with MethodID, ReJITID,
MethodExtent, ClrInstanceID, PID, event version, sequence and timestamp. It counts
map events and entries. PayloadNames alone is insufficient and is no longer used
for those arrays. ReJITID can remain zero across natural-tier versions: do not
merge records by MethodID/ReJITID; join to MethodLoad native ranges and chronology,
and mark ambiguous/missing joins incomplete. Decoder counts events lost; lost
or missing required events is a coverage gap, not a zero-event claim.

Review these obligations separately on each architecture and each natural tier:

1. **Producer:** actual SendPump.TryEnqueue / EnqueueAsync publication precedes
   Signal. Locate the full fence in Signal **or the inlining caller**, then a new
   state load, exact-Latched comparison, early-return branch, and unchanged
   armed/fallback RMW/continuation path. x64 may emit stack-local `lock or` or other
   valid full fence; ARM64 should be inspected for actual `dmb ish`/equivalent.
   Do not count a fence elsewhere in the method as sufficient.
2. **Ordinary consumer:** locate WaitAsync latch-consuming Exchange, then actual
   SendPump.RunAsync/MoveNext mailbox/stop recheck. Show the address/control-flow
   path, not just the callee source. Record framework Channel/SPSC implementation
   and any ReadyToRun body not present in JIT output as unresolved coverage.
3. **Timed consumer:** ConsumeLatched CAS, boundary waiter, queue/stop/policy
   identity recheck. Keep generation/token and continuation-ownership reasoning
   from the reviewed proof; do not infer it from assembly mnemonic counts.
4. **Fallback/ordering:** record actual shared-state Interlocked lowering, LSE
   CASAL versus LL/SC sequences where applicable. Hardware-selected LSE evidence
   does not cover non-LSE devices. Do not disable ISA features to manufacture
   another case under this natural-runtime plan.
5. **Inlining gaps:** absent Signal body means inspect real Tier1 producer
   caller/async MoveNext + inline events. If the exact path cannot be located,
   label `INCOMPLETE`, never `0 cost` or “optimized away.” Similarly a standalone
   Tier1 Signal body does not prove its inline caller representation.

`inventory.py` only indexes matching blocks/tier labels/fence hints and **always
returns accepted:false**. Acceptance is independent source/address/control-flow
review. Native bodies prove emitted code, not execution frequency of individual
branches. Raw managed events do not provide kernel ready/running/context-switch
proof; cross-layer delay causation remains unavailable. No “ThreadPool caused
P99” conclusion may be drawn from this capture alone.

## 6. Subsequent microcost experiment: separately approve after native review

**Discussion plan only; this tool review does not approve timing or formal acceptance.**

Question: does avoiding the shared latch-line write offset the added initial
read and per-producer full fence under real producer/consumer contention?
A local-stack locked instruction is **not free**, even if shared-line RMW vanished.
Use uninstrumented binaries and natural runtime, same CPU/runtime per pair.

Freeze independent paired runs for D/C, low contention (one producer, drained
mailbox), latched bursts (one producer), and 2/8/32 producers plus live consumer.
Use the existing real SendPump mailbox and WakeupSignal ownership protocol, not
just a repeatedly called isolated Signal with a permanently latched state.
Report 1P always-drained and continuously-latched synthetic cases separately as
mechanism bounds, not product wins. Preserve exact queued/completed payload
checksums and cancellation/stop after every run. Test baseline as well as C.

Suggested fixed pilot: each pair fresh process, 20k warmup then five fixed 1M
publication blocks per cell, 12 randomized D/C pairs, 120s maximum per block;
record scheduled-publish→consumer-completion, total CPU, allocations and
completed operations. No replacement of failed/slow blocks. Freeze offered-load
schedule before runs; blocked production remains part of delay, not omitted.
Record cache-coherence/HITM counters only in separate supported diagnostic runs;
missing hardware counters remain missing. Keep runtime tier phase separation;
Tier1 emission/steady-state stability must be independently established rather
than imposed. Interpret 1P drain regression as an important fast-path cost,
not dilute it by averaging with favorable many-producer cells. Proceed to RPC
only if realistic busy cases show a reproducible net reduction without unacceptable
low-load/low-contention regression. Exact microcost driver remains to be designed
and reviewed: this folder does not include or authorize timed execution.

## 7. Full RPC performance gate: O/D/C, no feature-removal victory

**Separate independent review required before any formal run. Neither native
evidence nor pooled microbenchmarks can substitute for the joint same-load target.**

- O is actual published official2.0.3 packages (hash+MVID), not merely source tag
  `4f9295ff`; D and C above. Same supported workload/configuration, runtime,
  hardware, timeout/cancellation, TLS/security, backpressure and completion oracle.
- Freeze independent capacity sweep first, then test common absolute arrival
  rates at 25/50/75/90/98% of O capacity plus one overload point. Also compare
  saturation maximum sustainable successful QPS with bounded backlog. Both
  constant-rate and seeded Poisson open-loop arrivals, plus fixed burst traces.
- End-to-end latency is scheduled arrival→caller result/exception completion,
  including harness queue delay. Never use service-only or enqueue/flush
  completion as RPC latency. Record offered/accepted/completed/faulted/canceled,
  queue backlog, deadline misses; no silently dropped arrivals. Physical teardown
  remains a separate required completion ledger.
- c1/c8/c64, many connections (including hot/cold mix), small/large payload,
  unary/streaming, TCP/TLS and existing supported transports. Diagnostics may
  stratify; overall claims cannot exclude slow functional cohorts to win.
- Frozen 12 randomized O/D/C process blocks per cell, fixed120s measurement after
  identical fixed warmup; retain every sample including failures, cold and tier
  transitions separately. Predeclare primary workloads and acceptable regression
  bound before execution. Use independent run/block uncertainty, report per-run
  P99 and bootstrap across independent blocks, not billions of correlated requests
  as independent trials. Revisit block count only under an amended plan, not
  optional stopping at success.
- Acceptance is **QPS ≥1.10×O and P99 ≤0.80×O jointly** on predeclared primary
  workloads, no semantic regression; D→C isolates this candidate. No guarantee
  this single fence candidate achieves both. Compare absolute load and saturation
  frontier; throughput from one operating point and P99 from another do not form
  a joint win. Diagnostics/native runs are excluded from timing.

Methods: [wrk2 coordinated omission rationale](https://github.com/giltene/wrk2),
[Kalibera/Jones replication and uncertainty](https://kar.kent.ac.uk/33611/45/p63-kaliber.pdf),
[.NET memory model](https://github.com/dotnet/runtime/blob/v10.0.1/docs/design/specs/Memory-model.md).
An M/M/1 tail formula is a motivating model, not an RPC distribution prediction;
real bursts, service variance, cancellation and ThreadPool adaptation must be measured.

## Revision provenance and receipt closure details

Old tool commit `4e2daeecccfaa131d078deae834db61095a690b7` is retained unchanged.
Its independent review was REVISE, not PASS; no one consumed the frozen16 cells.
The reviewer negative fixture remains in `/workspace/shared/sharplink-native-4e2daee-receipt-negative-review.py`
with its original result JSON. This revision rejects the equivalent malformed
receipt before Popen in an offline mocked test. New tests use synthetic local tar
archives and fake text DLLs only; they do not execute or validate CLR code.

The receipt closure contains all files in the probe output, generated-source
directory, selected core/ASP.NET/hostfxr14 directories, source `src/` excluding
`bin`/`obj`, repository-level props/targets/global.json/NuGet.Config, and build log.
Required named anchors include dotnet, CoreCLR, JIT, hostpolicy, hostfxr, CoreLib,
Runtime/Client/Server/Abstractions/Sdk product DLLs, probe runtimeconfig/deps,
WakeupSignal.cs, RpcSession.SendPump.cs and Directory.Build/Packages.props. Runtimeconfig
framework/version/roll-forward and forbidden tuning keys are checked, and deps
must contain required product libraries and resolve managed runtime assets.
Unexpected output files require new receipt review; symlinked closure entries
are rejected. Full installed runtime trees are compared to official archives;
The selected hostfxr must also be the newest installed hostfxr version, preventing
an unreviewed newer host from being selected outside the hashed14 directory.
SDK archive identity and reviewed build inputs/logs bind the build preparation.
The external receipt review still verifies exact source-commit provenance,
package/source-generator provenance and source-to-binary association; an asserted
commit string is not independently equivalent to a reproducible build.

Primary API verification for this revision (no runtime executed):
- [runtime10.0.14 manifest keywords and events](https://github.com/dotnet/runtime/blob/v10.0.14/src/coreclr/vm/ClrEtwAll.man)
- [TraceEvent4.0.0 explicit map accessors](https://github.com/microsoft/perfview/blob/v4.0.0/src/TraceEvent/Parsers/ClrTraceEventParser.cs#L10104-L10214)

Both corresponding raw.githubusercontent.com source pages were inspected; the
C# decoder remains uncompiled until the separate approved preparation stage.

The receipt source anchors are regression-checked against both exact frozen D/C
Git trees (`git ls-tree`), not merely files created by the synthetic fixture.
The prior invented SendPump.cs anchor is corrected; no product file was created.

## Source-binding amendment02: published equivalent tree, new honest commit identity

Connector publication cannot preserve the local commit author/date/parent metadata.
The local reviewed c17 commit was absent remotely, so using it unchanged as the
hosted checkout target would fail before preparation. The authorized publication
created remote `e5a35b9bf63d162c3077aec389d402e28bd2fb53`, direct parent
`f724c7aaabdeb5e02bec23f32f6b3c709860e97c`, with the **identical complete tree**
`ea401a81c4b5a52966f4281acc348f838b9fc935`. Remote commit/tree/parent were read
back through the connector, the object fetched read-only, and the full Git tree
diff against c17 is empty. `source-binding.json` retains the exact mapping and
remote metadata. The11-file delta contains only authorized research source/tests/
abstract model evidence; no raw traces, credentials or user payloads were published.

This explicitly changes the hosted source SHA, not the product bytes. New source
stamps can affect compilation/assembly identity, so all hosted D/C binaries are
built fresh from their frozen actual commits and their true identities reviewed.
Old local88-test/Tier0/prepared-only DLLs and receipts keep their original identities
and must never be relabeled as builds of the new remote commit. Runtime, workload
and per-cell protocol remain unchanged. This amendment must pass narrow review
before the validation branch is created and prepare CI is triggered.

## Pre-collection amendment04: ordered official runtime composition

Actual hosted prepare40716132624 has an independently accepted Linux bundle and
an ARM receipt failure. The reviewed installer already used Core14→ASP14 for the
runtime-only tree and SDK102→Core14→ASP14 for the build SDK. Official ARM archives
have13 differing overlapping dylibs, and all336 installed runtime files exactly
match that predeclared final composition. Original failure remains FAILED.

`plan.json.runtimeArchiveOrder` is now explicitly exactly
`[runtime, aspnetcore-runtime]`; validator rejects another/undeclared order.
Full regular-file AND directory closure is derived from the last writer in that
fixed pair, not whichever archive happens to match an installed file. Both
archives retain official SHA512 verification. Duplicate/alias/path traversal,
link/special/type-collision members and unknown/missing/modified installed entries
fail. A conflict replaced by the earlier official Core bytes still fails.

`runtime-final-members.json` records each final path's owner component, archive
SHA512 and member SHA256; native collector retains it before starting the probe.
This is prelaunch identity work, not ordinary-RPC instrumentation. Probe/decoder
sources, cell schedule, provider mask, natural-tier settings and limits are
unchanged. Original Linux bundle and schema2 receipt bytes remain unchanged.

The fixed byte-only ARM revalidation and honest original-build/new-validator
identity transition are specified in `research/hosted-wakeup/AMENDMENT-04.md`.
This amendment does not itself approve a recovered receipt or collection.
