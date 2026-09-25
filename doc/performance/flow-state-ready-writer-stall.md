# Ready-writer stall investigation

This continues #735 and the same Draft #742. Do not roll back Phase A or treat a
research benchmark as the complete production RPC path.

## Exact observed head

`727fb20d6d7ceb195b7b599d7bef4851c1a5882c` adds first-fault arbitration but does
not claim to fix the underlying stall. Its Ready Writer run `36002046312` has
successful correctness and NativeAOT jobs and a failed JIT job. PR Fast,
Package Smoke, CodeQL and the older Phase B / Phase A jobs succeeded.

The downloaded source archive exactly reconstructs tree
`ccdcd323503a31e1f16723a48faad7a6705b70a4`; the disposable measured tree is
`1aad6d2d888c9033cb32028bbd2b68cede9e4867`. Shipping sources are not replaced by
the experiment's generated SendPump hooks.

## Native evidence is now available

Artifact `10809030937`, ZIP SHA256
`56b5b52e15d0f3606856173518cddf2129e645ff7910b737bf0173f8850c54ea`, contains
8 processes and 128 complete samples. Its summary was regenerated from the raw
reports with the same validator. This is actual native execution, not the
reflection-disabled managed-host check from an earlier increment.

At c128 with an identical 8 KiB prepared-byte budget on both sides:

| Transport | Item bytes | Quantum | Throughput change B3 vs A | CPU-time change |
|---|---:|---:|---:|---:|
| SharedMemory | 16 | 16 | +65.45% | -40.74% |
| TCP | 16 | 16 | +50.02% | -36.24% |
| SharedMemory | 4096 | 16 | +2.66% | -5.20% |
| TCP | 4096 | 16 | +4.24% | -3.72% |

The tiny control uses 2048 items/stream and an 8 KiB connection window; the
4 KiB control uses 128 items/stream and a 512 KiB connection window. Two
independent AB/BA launches each contain four rounds for each mode/quantum.
Quantum-one controls and per-launch results are retained. These limited,
fixed-lifecycle, balanced-wire controls do not prove full production Go.
They belong only to the head/tree above, not to a later diagnostic commit.

## JIT failure is retained

Artifact `10809315020`, SHA256
`5f26cb584fd6361632ed680544a5b08fc4ef49b6f0b160fccd1b9756a3cfc333`, stops at
`24-tcp-pgo0-b4096-w524288-budget0-r0`: A-ready/q1, round 1. The case timed out
with both sessions connected, 6674 items received and 27336704 bytes returned.
The process exit was -6. Its partial population must fail the full-plan gate;
there is no replacement of failed samples or extension of the 45-second case
limit. Cancellation has already completed sibling tasks when the existing
exception-time status is printed, so it cannot identify the original wait.

Five pristine local processes (48 measured samples each) and twelve separate
locally instrumented processes did not reproduce that stall. This does not
invalidate the hosted-runner failure and is not a root-cause fix. Local timing
from these reproduction runs is not substituted for CI performance evidence.

## Separate, opt-in diagnostic executable

`ReadyWriterDiagnostic=true` only defines the diagnostic symbol when
`ReadyWriterExperiment=true`. The separate `stalled-jit-diagnostic` job samples
live queue/credit ownership, pump await state and participant tasks before
cancellation. It uses an independently compiled binary and separate artifacts.
Timers/reflection do not exist in ordinary JIT or NativeAOT measurement builds.

Snapshots are concurrent observations, not atomic ledger proofs. The diagnostic
build prints a snapshot every five seconds while a case is active, retains the
original failure, and stops its process loop on the first nonzero exit. It does
not nudge a signal, drain a queue, retry a failed sample or change credit/flush
policy. Cold and fully settled captures are checked for both A-ready and B3-ready.

Diagnostic report metadata carries `DiagnosticCapture=true`; all timing
validators reject it. A synthetic negative check first failed before adding
this rejection. The linked-source inventory includes the conditional file
without weakening exact-set and no-executable-reference checks.

## Acceptance boundary

The underlying hosted JIT stall still needs a demonstrated cause and a
regression. Successful native measurements and local repetition are not a
substitute. Dynamic stream lifecycles, global FIFO compatibility, duplicate
credit handling, full RPC integration and default-configuration acceptance
remain open. Keep #735 open and #742 Draft.
