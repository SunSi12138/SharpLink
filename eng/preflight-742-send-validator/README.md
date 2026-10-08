# Send-validator safety and codegen preflight

This first stage is safety and compiled-code collection only. No workload timing
is invoked or scheduled. A successful Actions run leaves the mechanism proof
pending independent inspection; it never establishes performance acceptance or
PR readiness. No SDK is installed locally, and no security setting changes.

## Frozen identities

The inherited helper is `211c7a94f7107158cd69cfb251ca33156487c48e`. All old
helpers, measurement sources, budgets, validators and workflows remain unchanged.

- G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`
- V production: `379e7ca76cc3431d464e8b3ffc56e245534b151c`
- G2 plus identical five-case test overlay: `9cf68caa7258c8ddec6a88f18376de3417a5e1de`
- V plus test overlay: `aaf1267b16b162bbd1c24c40fbb921856813d625`

Only the private send validator return shape and its two consumers differ in
production. The source subtree of each production arm must be identical to its
unit-test overlay. A fifth separate V+tests worktree receives the original
experimental ready-writer hook; it is never used as a native-codegen input.

## Safety before codegen

Both complete default suites must report exactly 2133 successful cases, no
failures and none skipped (the original 2128 plus five added cases). The three
new test methods are also run with exact populations 1/2/2 on both old and new
code. Existing malformed/stale, cancellation, terminal, waiter and refund tests
remain in the complete suites. V retains the original format, reference-boundary
and maintainability gates.

Both arms run the unchanged deterministic allocation gate. The original complete
120-row cold/warm lifecycle matrix retains its per-row <=0.01 B/request delta
gate. The original actual-session constructor/layout and production-callback
allocation probes are retained as separate evidence, with F/G legacy script
labels mapped explicitly to the current G2/V roots; no setup cost is subtracted.

The separate V experimental root runs all original writer-source controls,
171/171 writer checks, and the complete 2133-case experimental unit suite.

## Actual NativeAOT proof boundary

Only pristine production roots feed the same pinned minimal real-RPC host,
whose sole adaptation uses generated JSON metadata outside measurement. Tracked
source is hashed before host creation, after host creation, and after both builds.
Adapter-only source hashes are reported separately and must match between arms.
Both use the same original Release/linux-x64/PublishAot/StripSymbols=false publish
arguments. Full publish tarballs preserve executable modes, ELF and symbols;
hashes, full nm, compressed full disassembly, and focused raw disassembly remain.

Focus is ValidateSendLease, TryValidateSendLease/GetValidSendState and the four
controller methods TryAcquireSendCredit, AcquireSendCreditAsync,
AcquireResolvedContendedSendCreditAsync and ReturnUnsentCredit, including all
emitted overload symbols. Missing/inlined methods are marked explicitly.
Inspection must locate G2's out-reference assignment helper, trace V's matched
resolved paths and retain type/owner/Attached/generation checks plus throwing
admission/no-op refund behavior. Assignment helpers used elsewhere are legitimate.
Neither symbol absence nor workflow collection success passes this proof.

## Deferred plan only

After safety and independent compiled-path review, the accepted bounded screen
would use D/G2/V in all six permutations, with exactly 90 launches/198 rows:
TCP Client100x16; SHM Client100x4096; SHM Server1x16; SHM c8 sizes 1/10,000 with all
four operations in original unary/c2s/s2c/duplex order. RPC remains 30 warmups,
5 seconds and a 200,000-operation cap; c8 remains concurrency 8, 8,192/65,536 byte windows,
warmup 1/duration 2 and recording off. Same-host original JIT settings and four CPUs,
all raw failures/caps, no retry/resampling/exclusion. This is a plan, with no
timing runner or workflow enabled here. Controller micro work is also deferred
until code inspection establishes which original control is informative.

Full original acceptance remains necessary after any successful narrow screen.
