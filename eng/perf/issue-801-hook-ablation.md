# #801 Client/Server default test-hook source-ablation experiment

This audit experiment is intentionally separate from the existing
[exact-dev Roslyn + normal-vs-installed hook work](https://github.com/SunSi12138/SharpLink/pull/835).

## Source and isolation

The GitHub Actions job checks out two detached worktrees pinned to the **same
exact dev commit** (the PR base commit). A benchmark-only source file is copied
into both. The baseline worktree is otherwise unmodified, while the stripped
variant modifies exactly eight production source files, with exact byte-for-byte
pattern and file-count guards:

- Server admission: physically remove the two default-null static test delegate
  fields, their internal accessors, and three runtime reads/calls from
  CaptureAdmissionProgram. The other candidate-build and publication-lock hooks
  stay untouched.
- Client readiness: remove the concrete client instance's default-null
  BeforeReadyPublicationTestHook field and constructor assignment, plus its six
  corresponding only-for-test await sites across topology/refresh paths.
  The builder hook remains, but it becomes intentionally ineffective in the
  stripped benchmark variant; **this is not a shippable implementation**.

The temporary code is never pushed or merged. Tests depending on these hooks
would intentionally fail in the stripped build; that version is suitable ONLY
for the benchmark experiment. Source control diff, SHA and precise file hashes
are recorded in the artifact.

## Measured workloads

Both variants run exactly the same C# BenchmarkDotNet source under .NET 10
Release on one Ubuntu 24.04 runner. No test hook is enabled in either workload.

1. CaptureAdmissionProgramForTests forwarding to the actual production
   CaptureAdmissionProgram, plus the real generation-use ReleaseUse.
   This tests the expected steady-state Server request-admission branch cost.
2. SharpClientBuilder Build and Stop/Dispose without a network connection.
   This captures constructor/build and allocation footprint; it does NOT
   measure handshake/reconnect throughput.


3. Full Protocol v2 **real TCP loopback** AddAsync unary RPC. The Server has
   Admission enabled. This covers transport, generated proxy, serialization,
   dispatch, admission, and response handling; throughput/latency are still
   localhost benchmarks, not a remote production SLA.
4. Force-close an **already Ready TCP transport**, wait for the fixed-client
   reconnect coordinator to negotiate a fresh transport after a deterministic
   2-ms backoff, then complete a real unary RPC. Each benchmark invocation
   asserts the connection count advanced and the result is correct, with a
   bounded 10-second timeout. This is a connection-recovery measurement; it
   includes OS socket and timer scheduling and cannot isolate field cost.

The order is **base, stripped, stripped, base; stripped, base, base, stripped**
(ABBA + BAAB). There are four independent BenchmarkDotNet processes per variant.
The admission and Client-construction classes use 5 warmup / 12 measurement iterations;
the real RPC class uses 4 / 12, and reconnect uses 3 / 10 with one invocation per
measurement iteration. The job records ns/op, B/op, environment, run order,
and original BDN JSON/CSV/logs.

Results are exploratory, not a release performance assertion or a claim that
all test probes should be deleted. Interpret small differences as inconclusive
until repeated on controlled hardware with multiple run orders and GC/JIT
disassembly. In particular: changing a static test hook does not save memory
for every Server instance, and removing a single Client field may be affected
by CLR object alignment.

## Review / interpretation

- Verify both variants build; baseline PR Fast remains independent.
- Never use passing benchmark variants as proof that test-only race hooks can be
  removed without replacement tests.
- For a change to be proposed for merge, preserve deterministic race coverage,
  check active-generated/DI/NativeAOT paths, repeat an exact source A/B, and
  require production-representative request/handshake regressions.
