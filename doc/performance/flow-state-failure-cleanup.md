# Preserve a failed measurement when final cleanup fails

This research-only increment preserves the existing ready-writer, admission,
full-population collector and separately compiled diagnostic changes. It does
not change shipping `src/`, credit, socket options, timeouts or scheduling.

## Demonstrated boundary error

The old case wrapper used `finally` to await `DisposeAsync` with a ten-second
limit. If measurement had already failed and disposal then threw or timed out,
the final exception replaced the measurement exception. A deliberately constrained
local TCP diagnostic exposed that masking; it did not establish the cause of the
hosted-runner slowdown. No constrained socket option is part of this change.

`PhaseBTransportFailure.RunCaseAsync` records the primary measurement exception,
runs the failure description, and always invokes the same cleanup. Measurement
failure remains first when cleanup also fails. A throwing diagnostic sink is
retained as another cause, not allowed to prevent cleanup. Cleanup failure after
a successful measurement still fails the case; successful cleanup does not turn
a failed measurement into a successful sample. ExceptionDispatchInfo preserves
the original exception when it is the only failure.

This helper runs once at a case boundary, outside the measured item loop. It adds
no per-item synchronization, polling, timer, counter or completion. It is not a
claim of zero additional per-case allocation. The existing 45-second case limit,
ten-second cleanup limit and all required performance cases remain unchanged.

## Deterministic checks

Five checks are added to the seven existing failure-arbitration checks:
measurement plus cleanup failure, cleanup-only failure, synchronous cancellation
with exactly one cleanup, a throwing diagnostic sink plus both other failures,
and unchanged successful result. An isolated mutation using the previous
try/catch/finally policy compiles and fails the new primary-cause check. The
fixed helper passes it. The mutation is not in the delivery tree.

Both the ordinary Benchmark and minimal reflection-disabled native host link
these exact files. The checks therefore run under hosted JIT and NativeAOT CI.
A locally executed managed version of the minimal host is not native evidence.

## Previously completed evidence, not attributed to this fix

Exact head `9628bbc6782dc8a9696e250caa5a984c53d69a43`, Ready Writer run
36018718001, completed the unchanged JIT and native populations. These ZIPs
were downloaded, SHA256 checked and independently revalidated; regenerated
summaries matched the originals byte-for-byte:

- JIT artifact 10816087303: 48 processes / 768 rows; SHA256
  `8693906f71a5a0c1cf0a4b3ac6c338cc5c83653bdbdfc0888c83858e2710976b`.
- Native artifact 10815623160: 8 processes / 128 rows; SHA256
  `f37788e6810d32637f8810928ff71865c912d10e62fe63de9248b26af5df557b`.

Those results belong to that head, not this cleanup fix or subsequent admission
changes. A successful later run does not establish a root-cause fix for earlier
slow TCP cases. The captured earlier failure made slow continuing progress,
not a demonstrated permanent lost wakeup. Diagnostic timing is excluded.

## Acceptance boundary

This fixes lost diagnostic evidence, not the original performance slowdown or
complete production integration. New-head CI is required. Keep #735 open and
#742 Draft while dynamic lifecycle/ABA, original waiter FIFO, duplicate/clamped
wire permission, cancellation/deadline, receive batching, cold/retained memory
and full-duplex generated RPC remain unaccepted. No test, timeout, failed
artifact or negative performance control is removed by this increment.
