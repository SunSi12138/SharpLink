# Common-source same-version TCP fixture

Each arm **must build both projects independently**, with its own five package
assemblies and its own `SharpLink.Sdk` package's generator. Never copy
`ScalarStreamModule.dll` or emitted generated sources from one arm to the other.
The probe intentionally has no compile-time module reference so that static
module initialization occurs before builders, while dynamic registration occurs
after builders.

Build properties: `LibraryRoot`, `GeneratorPath`, and `Arm=OFFICIAL|DEV`.
`LibraryRoot` also supplies the identical four Microsoft 10.0.12 dependency DLLs.
Keep this directory's `Directory.Build.props` and `.targets`; they make the
fixture independent of the enclosing development checkout's build properties.

Executable: `PhysicalTcpProbe.dll`.

```
MODE ITEMS WORKERS 0 0 OUTPUT validate
MODE ITEMS WORKERS WARMUP_SECONDS SECONDS OUTPUT measure RAW_TICKS_OUTPUT
```

Supported modes are `static-intercepted` and `dynamic-intercepted`; items are
1 or 100. The orchestration pins one worker, two verified distinct guest-core CPUs, SDK 10.0.102 and private
runtime 10.0.14, with the arm's exact SHA in `SHARPLINK_BENCHMARK_SHA`.
Validation mode refuses nonzero warmup or measurement durations, never creates
latency arrays, and publishes zero operations with null timing metrics.

## Allowed version adaptations

Only `probe/ArmAdapter.cs` is arm-conditional. The difference is the expected
arm identity/source SHA and generated manifest API (official 2.0.3 API 4,
dev 57f06f1 API 5). No business method, contract declaration, functional check,
transport, builder configuration, interceptor, timeout, cancellation, or measured
operation has an arm-conditional branch. The API 5 manifest is positively
required rather than relaxing its check to fit API 4. Each compiler generates
its own ABI from byte-identical `module/Contract.cs`.

Both pinned versions positively require the general logical owner, intercepted
wrapper, and physical dispatcher. Dynamic additionally requires the exact module
channel and deferred forwarding outer stream; static requires the exact client
channel and general logical outer stream. Neither pinned version is expected to
have the later completion-owned optimization. Actual wrapper instances are
traversed before warmup, and telemetry-off is verified.

## Correctness and lifecycle

Timed `DownloadAsync` is the prior scalar business method, with exact ordered
values/count/checksum validated for **every** operation. It contains no fixture
counter or instrumentation. A separate cold-only method verifies before-first
and after-first error delivery, pending-read cancellation, early break, consumer
exception, explicit enumerator disposal, and sanitized unhandled server error.
Every scenario requires the server producer's `finally` completion and zero
active server producers/client logical calls. A successful normal call after all
failures establishes recovery. The waiting scenarios block server production
until cancellation, so cleanup cannot pass merely because the producer already
completed naturally. The safety guard is not a request timeout; configured
request timeout is disabled on both arms.

Cold correctness and positive path proof execute before each measured process's
warmup, as well as in the dedicated validation phase. They are excluded from the
timing window. Client/server stop and disposal must complete before success JSON
is written. All per-operation latency samples and elapsed duration are raw
integer Stopwatch ticks, with integer frequency and start/end ticks. Floating
summary numbers are convenience fields, not acceptance evidence.

This is a same-version pair inside each process, over actual loopback TCP. It is
not mixed-major interoperability, and no cross-major compatibility is asserted.
It is diagnostic only, not final performance acceptance or evidence that missing
#931/#933 have been integrated.
