# First-binding safety and root-layout preflight

This stage establishes correctness and collects actual compiled root evidence.
It makes no throughput comparison and cannot establish performance acceptance,
readiness, or promotion. The failed validator V is excluded. There is no local
SDK; local verification is reconstruction, source review and parser checks only.

## Frozen source and test population

The inherited helper is `f1607bf4f584abeb31c6bc65a5ac4b4aff380448`.
All original helpers, measurement sources, budgets and validators stay unchanged.

- G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`
- A production: `4cb4ce7a13b06c3a6f3213e30215b6769ed49f90`
- G2 with identical tests: `1edc4423f5157a8fb92cb7074349a25badc38030`
- A with identical tests: `a7cb5c6ef89222a82ca3b1e7a356345ee7b42902`

Production changes only the first-binding await in `ClientConnection` and the
generated server pump. Direct completed success consumes the result directly;
other completion states convert once to the existing task. The generated deadline
branch, late-send observation, subsequent-item sends and all ownership/lifecycle
code remain. The source subtree of each production root must equal its safety
overlay. A fifth A+tests root receives only the original experimental writer hook.

Both complete default suites and the candidate complete experimental suite must
report exactly **2186/2186** successful tests, none failed or skipped: the original
2128 plus the same 58 new cases. The six focused populations are respectively
12/12/12/2/4/16 and are independently checked on both default arms. They cover
immediate and pending first binding, sized/unsized paths, completed fault/cancel,
deadline precedence and abandonment, one-time enumerator consumption and cleanup,
and zero AsTask conversion bytes plus reference identity on the actual four
first-send helper paths. Detailed source assumptions are in `safety-rationale.md`.

The original allocation gates, full 120-row warm/cold lifecycle matrix with its
<=0.01 B/request delta limit, actual-session construction and production callback
allocation probes remain unchanged. Their legacy F/G labels explicitly map to
G2/A. The candidate keeps format/reference/maintainability checks and all original
writer source checks, 171/171 writer checks and its full experimental suite.
API snapshots, workload sources and lifecycle helpers are byte-identical.

The first run, `37841272536`, remains failed evidence: 30 timer assertions failed
on the G2 control before candidate validation. The test-only correction recognizes
the live connection's existing disarmed scheduler timer. It asserts ownership
0 before connection creation, 1 at the live baseline, 2 during a deadline wait,
1 after pump cleanup, and 0 after connection disposal. Abandoned cleanup uses a
bounded return-to-baseline wait; full timer drainage is required after disposal.
All 58 cases, production bytes, suite populations, and safety/codegen gates remain
unchanged. The retry must pass both complete arms; this correction is not a pass
or exclusion of the failed run.

The second run, `37842691039`, passed G2's full 2186-case suite, all 58 focused
cases and all four allocation checks. A built with zero warnings or errors, then
stopped on one test-file whitespace diagnostic before its tests and later gates.
The next input changes only that space to a newline plus indentation; test logic,
populations, production and every gate stay unchanged. Both prior runs remain
historical evidence; the retry must still complete all required gates.

## Matched actual JIT roots

Both pristine production arms build the original RPC project once using pinned
SDK10.0.112 and runtime10.0.12. The tiny standalone metadata probe loads those
actual Release binaries, closes both real async root state machines over byte[],
and records every field, awaiter and managed sizeof with assembly hashes. Both
roots must lose the lease-valued ValueTask awaiter and become strictly smaller.
Exact observed reductions are retained; there is no assumed byte reduction.
Managed JIT sizeof does not establish NativeAOT layout or allocation rates.

Exactly four diagnostic processes execute: G2 then A for TCP Client100x16 and
G2 then A for SHM Server1x16. Each retains the original 30 warmups, five-second
window, 200000-operation cap, original R2R0/tiering1/PGO1/QuickJitForLoops1 flags,
and four-CPU affinity. These executions obtain code from the original byte[]
unsized RPC paths. Their raw reports and failures are retained as diagnostics;
there is no throughput calculation, comparison, retry, resampling, extra warmup,
promotion forcing or fallback workload.

The original JIT parser is reused unchanged. Its strict Dynamic-PGO verdict stays
in the evidence. The previous capture was Tier1/Synthesized PGO and remains
inconclusive for its original Dynamic-PGO question. This separate layout capture
may compare matching Synthesized-PGO bodies under that exact label. It retains
every tier/version and requires matched canonical-reference wrapper and MoveNext
signatures, exact tier/PGO/profile/OSR labels; an Int32-only pair cannot qualify.
Generated state-machine ordinals are the sole signature normalization and are
mapped to the same two source roots. Raw text is never normalized. Actual generated
RPC source is archived for independent byte[]/unsized-codec path mapping.

## NativeAOT and review boundary

Both pristine production roots feed the original minimal real-RPC NativeAOT host.
Its sole adaptation remains generated JSON metadata outside the measurement loop.
There is no native workload execution. Original Release/linux-x64/PublishAot=true/
StripSymbols=false publishing is retained. Track-source maps before adaptation,
after adaptation and after builds must agree; adapter hashes must match arms.

Full executable/symbol publish archives preserve file modes. Full nm and objdump
streams are losslessly compressed with xz. Focus includes actual root wrappers and
MoveNext methods, real sized/unsized first-send helpers and deadline handling. An
absent or inlined helper is recorded, never counted as mechanism success.
Independent inspection must map byte[] to canonical roots; inspect real NativeAOT
allocation/layout, zeroing and spills; and trace direct success, task reuse,
completed fault/cancel, deadline and late-observation paths. A green collection run
leaves this proof pending and does not authorize a performance screen.

Artifacts are separate safety/index, JIT G2/A, NativeAOT G2/A ELF, and NativeAOT
G2/A code packages. Every artifact's retained file population is checked below
24MiB before upload, with individual hashes. ELF and full disassembly are split
up front because the historical combined native evidence exceeded this limit.
Each package is checked before entering the upload directory and gets its own
readiness output. A later copy, hash or budget failure fails the job while valid
earlier packages still upload; a bounded safety manifest retains packaging errors.
Existing official checkout/setup-dotnet/upload-artifact actions remain SHA-pinned.
No external machine, local SDK installation, or security change is requested.
