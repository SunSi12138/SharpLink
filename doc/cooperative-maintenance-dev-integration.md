# Cooperative maintenance integrated with current dev

## Candidate and release boundary

The ordinary merge `ea1acc9e4584f4ac0ca2e6df6b1a33f26d432117` retains both
`92a8936eb6656d6bf427ec4fbf807dde5903434a` (#933) and
`ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c` (dev plus physical ownership) as
parents. Its production source tree is
`2bb7adfa59119695e627ab711f26178abc68c485`. Subsequent evidence-tool and document
changes do not change that product tree. The current dev product arm is
`cc7c458b559295e0fc61ec0062bb220faf880a76`.

This candidate implements the restricted #933 capability: host-authorized,
whole-server authenticated TLS cohort, compatible implementation-only replacement,
fixed endpoints and permanently sealed runtime/catalog configuration. Opt-in
rejects all current, retained or physically reclaiming waiting-capable admission
programs. Wire 2.4, optional capability bit 7, frame 17 and the 144-byte stages
1–11 remain unchanged.

Issue #775 remains open: session-bound BindContract/BindingId, bounded token/ABA
management, explicit binding preparation, cross-control/data receipts including
ExactRegistration, dynamic topology/reconnect binding and client contract/codec
replacement are not supplied by this maintenance capability. The optional
GenerationControl package remains separate and is not an ExactRegistration receipt.

## Resolved ownership boundaries

- The latest intrinsic-send implementation and its telemetry-reentrancy tests are
  retained. Queue publication, node claim, live-successor traversal and wake-slot
  ownership survive synchronous cancellation/Stop callbacks.
- #935 still closes both writers before the terminal pump drain. Session disposal
  uses #933's single-flight, forced-yield physical shutdown owner: abort pending
  I/O, join the writer, then dispose the transport. Custom uncooperative writers
  can continue to prevent physical completion.
- #930's queued selected-registration identity and admitted dispatch remain
  unchanged from the prerequisite arm. They continue to operate outside enrolled
  maintenance. The [reader-prefix argument](maintenance-reader-prefix-proof.md)
  now audits the actual integrated branches rather than claiming #930 is absent.
  Five changed regions were reviewed; the intrinsic-send pin now covers the
  whole owner file, including resume/retirement.
- Physical exception-carrier source is identical in both merge parents. The newer
  mapper regressions are retained: standard cancellation category/message/token
  survive; the application mapper receives the original exception instance.
  Custom outer subtype and TaskCanceledException.Task are not copied to the carrier.
- GenerationControl source, generator support and its optional package wiring are
  retained. Maintenance adds no ordinary per-RPC maintenance table, epoch, hash or
  counter. Its existing producer reservation changes still require cost acceptance.

## Focused local evidence

Release unit and integration builds succeeded with SDK 10.0.102 and zero warnings
or errors. The executable reports identify macOS 27.0.1, Arm64, .NET 10.0.11 and
TUnit 1.73.19. Test concurrency was 8; the integration assembly retains its existing
NotInParallel policy.

| Check | Result |
| --- | --- |
| Combined unit discovery | 2,440 cases |
| Combined integration discovery | 556 cases |
| Maintenance/producer/callback/budget/send/mapper focused unit group | 165/165 passed |
| ServiceRegistrationTests | 25/25 passed |
| SessionTransportQuiescenceTests | 12/12 passed |
| RuntimeAssemblyIntegrationTests, including maintenance and queued replacement | 131/131 passed |
| Reader-prefix review triggers and reader inline-branch guard | 2/2 passed |
| Admitted-dispatch source parity | 3/3 passed |
| Historical/integrated source reconstruction | 7/7 passed |
| Producer archive/profile source binding | 4/4 passed |
| Project-reference boundaries | 10 projects; 15 declared/active references passed |

The historical full-suite collectors now require 2,440 regular cases; the lease
collector requires those cases plus its eight unchanged overlay controls (2,448).
These are inventory requirements, not claims that the full suites have run here.
The focused .NET runs had zero failures, skips, cancellations or timeouts.

Raw build logs, discovery output, TUnit JSON/HTML and source-preparation provenance
are retained locally under `artifacts/maintenance-integration/`; `summary.json`
records the machine-readable inventory and outcomes. They have not been uploaded.

The first historical Python reconstruction attempt used the host's older Python,
which rejected `tarfile.extractall(filter='data')` before source reconstruction.
The documented modern Python runtime then passed all seven cases; no guard was
removed. This environment failure is separate from product/test results.

## Source profiles and remaining acceptance

`eng/maintenance-integration-sources.json` pins all three feature arms:
**cc7 dev / ac17 prerequisites / combined candidate**. Maintenance native/TLS and
stream tools expose `integrated-dev` and `integrated-prerequisites`; use independent
fresh probe directories and retain each comparison's environment and provenance.
The historical reconstruction entry point and numerical results remain unchanged.

The producer `integrated` profile compares the previous #933 final head with the
combined candidate. This answers the integration increment only, not the feature
cost relative to ac17 or cc7. Its historical c603-to-933 profile remains available.
Both the preparer and summarizer select the profile's exact source identities.
All three integrated source preparations were exercised against the actual merge;
no probe, native campaign or timing workload was launched during this integration.

Independent review and the existing complete correctness, API/package/ABI,
AOT/Trim, historical negative-control and x64/Arm64 allocation gates remain due
against the final head. Performance acceptance remains open. Prior #933 green
jobs and prior timings do not certify this combined tree; the documented +16-byte
Unary/Retry PGO frame reservations and streaming-entry costs remain evidence to
explain, not numbers canceled by unrelated improvements.
