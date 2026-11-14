# #775 production integration and formal PR

## Outcome and current baseline

Deliver a non-draft PR against `dev` that implements the selected single contract-channel
architecture in the ordinary, packable product. Do not merge the PR. The full #775 and #805
requirements remain the acceptance contract; a passing experimental profile is input evidence,
not the production deliverable.

- Product base: `160d0cd9a0550de5bf00c63b6007376c02debc20` (remote verified 2026-11-14).
- Accepted OneWay handoff: `228ab66cc843cd7f6bc8ca6047181282a4a6a874`.
- Production branch: `codex/775-production-integration`.
- Workspace: `C:/Users/sunsi/Desktop/SharpLink-775-Slice`.
- The original `C:/Users/sunsi/Desktop/SharpLink` has unrelated deletions and is not an edit target.
- User authorized local runtime/performance testing. Hyper-V management was denied to this
  session; the user confirmed VM shutdown before local load testing. Do not bypass that denial.

## One production path, with explicit compatibility boundaries

Promote the already validated profile into the ordinary source and remove the alternate raw
channel/Get/GetWithMetadata mode, not preserve it behind a default identity or compatibility
facade. Generated/manual consumers use `IRpcContractChannel`, local factories return a service
and exact-site finite-budget Prepare handle, and five-family guard/policy/deadline/physical
ownership boundaries remain unchanged. RuntimeContext and standalone serialization stay usable.

Selected production compatibility boundary:

- Generated API6, exact identity `sharplink-3.0-api6-contract-binding-v1`; regenerate all
  manifests/proxies/Stubs together. Old API5 and private profile locators fail preflight.
- Keep Protocol v2's framing/public type family, advance wire minor/floor to5, and use normal
  magic0x89. Negotiation rejects minor4 before any business frame; private magic0xBC is rejected
  by the parser. Do not silently accept old ContractId Request semantics.
- Allocate BindContract17, BindAck18, BindingInvalidated19, BindingRelease20,
  RequestAccepted21 and OneWayInputRequest22. Preserve the tested bounded payload grammars,
  including OneWay's independent count1..127 and its no-per-call-Ack contract.
- Record precise API/ABI/wire deltas for #805. Final package/type/member extraction and
  independent review still apply; these choices do not approve future declarations by wildcard.

## Required work and evidence

1. **Production source and callers.** Remove the profile fork and nonpackable switch; migrate
   all generated/manual/SPI/Hosting/multi-cluster callers, tests, demos, samples, benchmarks,
   API tools and documentation. No raw receiver casts or old Get facade. Preserve complete
   owner-bound Codecs and manifest identities; update precise negative consumers/version tests.
2. **Complete routing.** Integrate CallSite snapshots with the existing endpoint/selector policy,
   exclusions/retry/reservations, reconnect/blue-green replacement and immutable topology
   publication. Remove the fixed-endpoint-only restriction without replacing Picker policy by
   "first connection". Never split a Session from its token or retarget an admitted stream.
3. **Remaining correctness/cleanup.** Validate five-family error/auth/mapper/backpressure
   combinations, original facts/registration/Codec and both physical owners. Complete #931
   physical-tail protection, #933 producer-group/Session and publication/retirement cleanup,
   and #964 exception-safe frame/batch/queue disposal against actual production ownership paths.
   Preserve the existing deterministic inventories and counterexamples; do not count renamed
   or disabled cases as retained evidence.
4. **Ordinary regression/platform/package gates.** Run the ordinary Release solution and
   Unit/Generator/Integration/LoadTest regressions; package/reference/implementation/forwarder
   extraction; external consumers, SDK/Hosting/DI; full/partial trimming and supported NativeAOT
   platforms. The historical #387 Windows Stop failure remains unresolved and its investigation
   paused; retain that boundary, and do not relabel it green or silently relax its budget.
5. **Performance.** Before measurement freeze exact subject/baseline, A/A validity, workload,
   warmup/sample budgets and stopping rules. Measure startup and steady state, short-stream P99,
   static/dynamic and interceptor/no-interceptor paths, NativeAOT supported boundaries and
   allocations. No assumed3% threshold, retry-to-green or unqualified positive claim. Reuse valid
   historical evidence only for matching subjects/inputs.
6. **Formal PR.** Freeze the actual candidate, reconcile complete #775/#805 requirements with
   effective artifacts and evidence, fix acceptance-relevant failures, create a non-draft PR
   against dev with migration/compatibility/performance results and honest residual scope.
   Do not mark the goal complete on a draft, a prototype-only PR or merely queued CI.

## Execution record

- VM prerequisite resolved by user confirmation after the session's permission denial.
- Created the production branch from the accepted handoff; dev remains the verified base.
- Next: promote the validated source path, then use ordinary compilation to drive the complete
  caller migration. No paused investigation has been restarted.

### Production-path progress

- Promoted86 source files from the validated profile to the ordinary product; no alternate
  SHARPLINK_775 source branch remains. Ordinary Abstractions/Runtime/Client/Server/Hosting
  basic build passes without the profile flag. Removed the nonpackable switch, changed
  generated API6 and wire2.5/floor5, and allocated the six explicit frame types.
- Multi-cluster factory now captures a child route once and delegates CreateProxy/options
  to that child's exact local factory; no legacy Get/GetWithMetadata facade is retained.
- Moved deterministic fixtures to test/SharpLink.BindingTests and gave them an ordinary
  production friend assembly. Their source inventories remain intact. Ordinary execution at
 3400cdbf and subsequent routing candidate d086cd4c is recorded below.
- Migrated demo and QuickStart/ProductionTemplate factory use to explicit finite Prepare.
- System SDK10.0.302 selects vulnerable ILCompiler/runtime10.0.10, causing NU1903 during
  ordinary solution restore. Advisory GHSA-p3jp-9v83-76w9 fixes .NET10 at10.0.14 or later.
  Installed official SDK10.0.115/runtime10.0.15 in the user-only directory
  C:/Users/sunsi/.codex/dotnet-775-10.0.115; official release SHA512 was verified for binary
  installation integrity. System SDK installation and NuGet security auditing remain unchanged. Pin global.json
  to10.0.115 in the same100 feature band so CI also selects a patched ILCompiler instead
  of relying on a host having a newer SDK installed. Local commands select the user-only
  SDK explicitly. Baseline/candidate performance will use the same patched toolchain.

- Ordinary SDK115 solution build now reaches real caller compilation (no vulnerability
  audit bypass): all demos and QuickStart/ProductionTemplate build. It reports153 unique
  compiler errors in old tests/benchmarks/AOT/load callers, mainly required Stub facts ABI,
  test-client factories and retired Get/raw channel use. This is the migration worklist,
  not a reduced regression scope; full solution acceptance is still incomplete.

### Ordinary-path controls and routing acceptance (frozen before execution)

- At3400cdbf, the ordinary production path passed all seven unchanged deterministic
  inventories on Windows/TCP and Windows/shared-memory:488 cases per transport,
 14 JSON reports verified for exact source, transport, names and uniqueness. This
  does not substitute for the unfinished full solution/platform/performance gates.
- Multi-endpoint integration now publishes one immutable bound routing snapshot from
  the existing topology writer. Endpoint generation, candidates, retry exclusions and
  Session/token pairs share that publication. Selection uses the existing topology's
  captured policy/cursors, original admission outcome and expansion/retirement owners.
  Blue/green redirects cannot borrow another physical Session's token.
- Added25 frozen real-RPC routing controls in routing-expected-cases.json, for both
  TCP and shared memory: all four built-in strategies, live custom policy replacement,
  invalid/throwing selectors, retry exclusion, dynamic same-ID generation replacement,
  original-stream physical completion, no extra reconnect, Stop route/quota release.
- Routing acceptance: each exact inventory must pass within the120-second process
  watchdog with original10-second finite Prepare and no relaxed budgets. Stop on the
  first exception, timeout, unexpected endpoint, token/Session retarget or quota leak;
  retain its raw log and fix the evidenced cause before a new candidate is run.
  Then retain the seven existing488-case inventories unchanged for the new source.
- Full TUnit/Generator/Integration/LoadTest migration and gates remain pending. Local
  reports explicitly label execution=local; source accepts SHARPLINK_TEST_SOURCE instead
  of requiring the GitHub environment variable. Performance A/A criteria are still to
  be frozen before any performance run; no performance conclusion is asserted here.

### Routing result and ordinary Unit ABI migration

- d086cd4cbf943c71adac64296074bfb93dcc3e64 passed all25 new routing controls
  plus all488 existing controls on each transport locally under runtime10.0.15.
  All16 reports match exact committed inventories and source; no watchdog or budget
  was relaxed. Logs are artifacts/775-integration/*-d086cd4c.log. This establishes the
  bounded routing controls, not complete reconnect/policy/platform/performance acceptance.
- The ordinary Unit project now compiles with zero warnings/errors. Manual invocation
  tests use a client-issued local selector and a simulated peer's actual Bind/Ack instead
  of casting the client to a raw channel or fabricating a default bound identity. Finite
  Prepare is explicit bootstrap work, outside policy/deadline/clock observation boundaries.
  Remaining producer/consumer timing expectations must still be exercised and migrated.
- Unsupported lifecycle-only test doubles implement the required API6 members explicitly.
  Stub fixtures consume readonly facts, with explicit known-method resolution or no-method
  rejection. Dynamic invocation tests install/acquire actual Session-local bindings so the
  original registration remains protected through asynchronous acquisition and cleanup.
  Generated stream bridge callers pass one complete descriptor instead of three narrow facts.
- Retired the historical default-profile-equals-dev guard: equality to the old product is
  no longer the selected production contract, and its previous evidence remains archived.
  The resource-parity guard now checks six strict ordinary-path controls, permitting only
  the explicit bound/queued prologue, already-completed admission and whole-frame-vs-head
  owner differences. The ordinary source still has exactly one cold Bind Stub resolver.
- Explicit API meaning change: local CreateProxy is independent of remote identity and
  connectivity; incompatible peers fail finite Prepare without poisoning Session readiness.
  Handles retain their Service, while separate cold factories may construct distinct services
  sharing an exact-site cache. Old Get cache/readiness assertions have been migrated to these
  boundaries, not retained behind a compatibility facade. Hash-mismatch Prepare diagnostics
  retain the contract name/ID and both exact hashes.
- Next checks: focused post-Bind dispatch/resource/scope tests, then local factory/manifest,
  deadline and stream ownership tests. Do not count compilation as runtime acceptance.

### First focused runtime finding

- At74ef4d89, post-Bind SharpLinkServerInvocationTests passed27/27. ServiceRegistrationTests
  passed24/25 and exposed a real production defect: AcquireBoundAsync tested root-provider
  presence instead of Singleton lifetime, so an instance-backed dynamic singleton fell into
  per-call scope activation and failed with Service scope factory is unavailable. Historical
  group fixtures used factory-backed singletons, so their passing evidence remains valid.
- Corrected the lifetime branch rather than changing the failing fixture to a factory. Expanded
  the same deterministic regression to instance Singleton, factory Singleton, Connection and
  Call lifetimes, with exact activation counts across two invocations and one cold resolution.
  The original failed source/log is retained; no budget or failure expectation was relaxed.

### Focused runtime results and cold-demand correction

- The protected-lifetime fix414cc018 passes all28 ServiceRegistration cases, including
  all four lifetime forms and failure/cleanup aggregation. Request scope suites pass5/5
  and6/6 without changing their100,000-call ownership control.
- Migrated the simulated peer to real nonempty, stable per-Session server/epoch identities.
  Its manifest refresh helper preserves those identities; raw malformed-frame injection
  remains raw. Discovery7/7, contract/Prepare5/5, remote-observation21/21 and protocol
  manifest4/4 pass, including two new empty-server/epoch rejection controls.
- Deadline clock1/1 retains the exact8-sample criterion. Logical deadline2/2 passes.
  Timed Unary emission initially failed its unchanged no-per-call-framework-task assertion:
  repeated cold channel factories and Prepare joins were dirtying the same binding demand.
  New-demand registration is now idempotent and a Prepare join does not mark an in-progress
  capture dirty; actual topology/manifest publications still do. The strict3/3 timed Unary
  suite then passed. Publication/early-Ack inventories must recheck this scheduler change.
- Deadline publication initially passed7/11. The old startup Ping fence preceded the new
  Bind frame, leaving Bind in the business observation queue; the OneWay-input positive
  assertion also expected the retired Request shape. Replaced that fence by an ordered new
  peer Ping/Pong, asserted OneWayInputRequest plus count1, and strengthened negative business
  publication checks to reject either request frame family. All11/11 then pass with original
  budgets, producer-not-started and Request-before-Cancel assertions retained.
- Logs for these migrations are artifacts/775-integration/unit-*-identity-wip.log,
  *-demand-wip.log and *-fence-wip.log. Failed logs remain. These focused runs do not imply
  the full ordinary Unit suite, full solution, platforms or performance are accepted.

### Scheduler/lifetime revalidation and downstream caller migration

-23d4a8bde61017180ad696a178fc65b3c09b7a4e passes all25 routing plus488 existing
  controls on each transport, with all16 exact source-bound reports verified. This includes
  publication/early-Ack, retirement, physical owners and ALC controls after the scheduler and
  protected Singleton fixes. Reports for3400cdbf (14), d086cd4c (16) and23d4a8bd (16) are
  retained under artifacts/775-integration/reports-<source>; each comes from its raw RESULT log.
- Migrated AOT smoke, Chaos, LoadTest/hold-capacity, StreamLoad and benchmark bootstraps to
  finite Prepare outside measured RPC loops, retaining startup cancellation where available.
  These projects compile in the ordinary solution build. Performance has not been run.
- ProxyCacheBenchmarks retains its class entry point but explicitly replaces retired Get-cache
  rows with cold CreateProxy and retained Service rows. The dynamic factory row is labelled
  ViaReflection; no old cache metric or performance result has been relabelled equivalent.
- Migrated176 connected Integration declaration sites to explicit handle/finite Prepare/Service.
  Disconnected client-interceptor short-circuit construction stays local and does not Prepare.
  Dynamic/local-negative cases, inline calls, metadata, fixture fields and raw protocol controls
  remain explicit migration work. The next solution build reports159 downstream errors, chiefly
  remaining retired Get callers; this is not a full-build pass or a reduced test inventory.

### Missing visibility is not a permanent incompatibility

- Rechecked #775's current cold-wait contract: local factories are independent, Ready is
  not bound Ready, and only a definite mismatch should fail explicit Prepare immediately.
  Found that the promoted profile classified a missing advertised contract as HashMismatch.
  Corrected absence to remain pending within the original finite monotonic deadline, without
  speculative Bind or producer work. An advertised different hash still fails immediately.
- Also found that a late failed Bind capture could publish its exception after a newer
  topology/manifest publication. Failure publication now validates the same captured tuple
  as success; new topology/manifest publications invalidate old cold errors. This does not
  add a hot-path lookup or alter selection/guard/producer boundaries.
- Added two deterministic gated Unit cases before running: absent manifest remains pending
  with zero Bind operations then completes its original wait on later real Ack; a held old
  HashMismatch reply cannot poison two waiters while the newer Bind/Ack is held. Gates pause
  actual simulated wire replies, not the client binding work. Original10-second budgets and
  no-sleep/no-retry rules retained. Contract suite7/7 and observation21/21 pass.
- Corrected our earlier unregister test migration: absence is a cancellable finite wait, not
  immediate FailedPrecondition. The observation is still not a lease. Removed the unused old
  Get-time remote validation helpers rather than keeping a second validation mode.

### Ordinary solution build completed; runtime acceptance continues

- The ordinary Release solution now builds with zero warnings/errors under SDK10.0.115
  (solution-caller-migration-15.log,7.87s). This includes Integration, Unit, demos, samples,
  managed AOT smoke callers, benchmarks and load tools; it is a build result, not AOT/runtime
  or performance acceptance. Non-solution package/external fixtures and generated test-source
  strings still require their actual consumer gates.
- Inline Integration callers now retain explicit prepared handles outside tested RPC,
  interceptor, admission and metric boundaries. Disconnected short circuits remain local.
  Metadata uses RpcProxyOptions. Reflection-based dynamic consumers prepare the exact returned
  opaque handle before retrieving Service; coordinator consumers do not prepare a future route.
- New harness failures unwind their owned resources. Reentrant interceptor tests retain an
  already-prepared Service rather than doing cold work within the interceptor callback.
  Filtered remote services remain locally constructible; an unbound default call fails with
  Unavailable/NoBoundRoute before serialization, replacing the old Get-time rejection assertion.
- The malformed-frame simulated peer now processes bounded Bind/Ack and observes both actual
  bound business heads before injecting its oversized header; no fixed150ms startup race.
  Its handshake has nonempty server/epoch IDs. Other raw legacy/coalesced protocol controls
  still need runtime review against the new wire contract, as do state-only dynamic fakes.
- Next runtime order: real five-family call shapes, service lifetimes, selector policy/reconnect,
  malformed-frame fanout, then full ordinary suites and remaining physical/error combinations.
  Keep existing budgets and stop each focused batch on first failure; no passing count substitutes
  for a remaining required class. #387 investigation remains paused.

### First ordinary end-to-end regressions and test-contract migration

- Real call shapes pass4/4, service lifetimes5/5, static endpoints29/29, dynamic endpoints22/22,
  and transport/protocol controls42/42 on Windows/runtime10.0.15. The lifetime recovery test
  initially raced the server finally: application Dispose precedes admission release. It now
  observes the existing physical active-call diagnostic at zero before checking recovery;
  no retry or timeout extension was introduced.
- The raw cross-minor positive control now coalesces Handshake+real Bind, consumes its exact Ack,
  and sends a proven bound business head. The legacy negative coalesces Handshake+Bind+that head,
  verifies rejection before either dispatch, and joins teardown before asserting zero invocations.
  It no longer treats the retired ContractId request prefix as a valid current-wire control.
- Unit manifest negatives now include prior API5 and future API7, exact old ABI rejection before
  construction, and API6 metadata consistency. Builder18/18, locator15/15 and selection-generation
  races3/3 pass. Manual-channel connected fixtures perform finite Prepare explicitly before the
  invocation; pending capacity controls supply the actual OneWay input count.
- Cancellation5/5, timeout2/2, pending/admission4/4, retry-outcome reuse1/1, shared retry deadline1/1,
  runtime admission4/4, runtime retry policy7/7, runtime timeout5/5, structured configuration4/4 and
  runtime breaker6/6 pass without changing their measured deadlines or policy-capture boundaries.
- Full Unit attempts are not passes: the first exposed stale API5 assertions; the second missing
  explicit Prepare in a selection race; the third a missing input count. Each failure is retained.
  The migrated retry-behavior class passed11 cases but the known HugeBuiltInJitteredRetryDelayShouldRemainCancellable
  case hit its30-second framework watchdog on Windows. #387 remains open/paused: no investigation,
  relaxed budget or passing claim. Remaining session-refresh test peers must advertise and bind
  their real synthetic contract; replacement Ready is not replacement bound Ready, and no old
  token may be redirected. Full suites, platforms/packages and performance are still incomplete.

### Generator ABI gate and accepted-input semantics

- The complete ordinary Generator suite now passes295/295. Migrated current producer metadata
  fixtures to API6/exact contract-binding ABI, retaining legacy and wrong-ABI negatives. The
  migration exposed a production omission: referenced-manifest bootstrap still required API5.
  It now requires API6; referenced Codec identity validation also checks API/protocol, nonempty
  producer version and the manifest's actual owner, rather than trusting the exact-ABI string
  alone. Added API5/API7/protocol3 counterexamples carrying the claimed current ABI.
- Generated Stub checks use the single cold TryResolveMethod and complete cancellation facts;
  removed the uncalled old narrow cancellation emitter. Routed owner Codec and name-collision
  checks target IRpcContractChannel. The metadata documentation compiles against the built real
  Abstractions metadata/options/opaque handle API without adding that assembly to other mocked
  Generator test compilations. Updated calls-and-streaming.md for explicit finite preparation,
  default NoBoundRoute, per-call RequestAccepted and OneWay's independent bounded/no-Ack shape.
- ClientStreamingResultStressTests initially deadlocked its obsolete pre-Ack-input expectation.
  Its three migrated controls pass with the original10 iterations/10-second phase limits:
  no producer/deferred input before the gated interceptor terminal, captured A/B/C unwind after
  global X/Y/Z replacement, actual compressed input roundtrip after acceptance, and queued head
  accounting released before accepted payload exceeds MaxQueuedBytes. The latter is now an
  accepted-input control, not a claim that ordinary two-way producers still spool before Ack;
  OneWay deferred-buffer/rejection controls remain required under its no-Ack contract.
- Full Integration has advanced but is not accepted. Its next failure was a raw compression
  probe using an arbitrary ContractId/method1 as the retired request prefix. The replacement
  uses real Bind/Ack and an actual known Unary descriptor before injecting hostile compressed
  bytes. All3 CompressionPersistentDecodeReview cases pass: large compressed input still starts
  exactly one persistent decode provider despite a small declared output; Cancel remains
  responsive and all retained/decode/decoded-byte/call resources release. The raw helper reads
  bounded frames and checks exact Ack correlation/identity; it never invents a binding token.

### Post-cold-fix matrix and live admission generations

- cb355d24648d4d2b47ebf2e4015262b2e248afd5 passes all513 frozen controls on
  each local Windows transport, including the cold absence/stale-failure fixes and Generator
  ABI correction. All16 exact inventory/source-bound JSON reports verify and are archived in
  artifacts/775-integration/reports-cb355d24. Original watchdogs, finite budgets and GC controls
  are unchanged. This remains functional evidence, not a performance result or platform pass.
- Expanded runtime-disable and runtime-update stream accounting into separate two-way and
  OneWay controls rather than deleting the pre-admission ownership obligation. Two-way queued
  calls own only the bounded head/waiter and do zero producer work before RequestAccepted;
  explicitly queue-enabled OneWay still buffers real input with no per-call Ack. Both retain
  their captured generation across disable/update, exact physical byte/queue ownership, original
  kernel/concurrency lineage, one reclaim and reusable connections. The two suites pass4/4 and3/3.
- Capacity recovery now waits for physical active-call release after the blocker response;
  the original provider-zero-on-rejection and exactly-one-on-recovery assertions are unchanged.
  Remaining old two-way pre-Ack spooling tests need similarly explicit migration while retaining
  the independent OneWay budget/refusal proof. Full Integration/Unit acceptance is not complete.

### Preserve physical stream-budget controls under the new Ack boundary

- Split the obsolete two-way pre-Ack overflow expectation into its actual contracts. A queued
  two-way upload performs zero producer, invocation and pre-admission-byte work, then completes
  payload larger than the old buffer cap after acceptance. A held activation cut likewise has
  no producer/user work; accepted input uses active stream credit, not the retired queue budget.
- The physical global-byte/independent-admission-accounting and forced-stop controls now use
  explicitly queue-enabled OneWay input, where pre-admission production remains legal. Two
  connections really exceed the12KiB shared byte cap; the rejected OneWay has no business
  invocation/result and exactly one server resource_exhausted record with the stable
  server_pre_admission_stream_bytes reason. The surviving buffered input retains its bytes,
  replays, releases all accounting and leaves the rejected connection reusable. No per-call
  Ack or caller-visible ResourceExhausted response was invented for OneWay. Both cases pass.
- The OneWay activation race still proves the exact pre-admission-budget cancellation reason
  wins before invocation; both activation controls pass. The two-way queued-cap control passes.
  InterceptorIntegrationTests passes25/25 after moving its synchronization-context observation
  to the first real post-RequestAccepted MoveNext; its zero Post-count assertion is unchanged.
- Full Integration attempts have reached264 passing tests before the next old timing assertion,
  but are still incomplete. The passing focused controls above do not turn those failed full
  runs into a complete-suite pass. Raw logs and original failure sources remain retained.

### Canonical OneWay negatives use the frozen binding, not latest-registry mutation

- OneWayEarlyRejectionDrainIntegrationTests now passes3/3. The old fixture replaced latest
  services/Stub metadata after Bind, which correctly cannot alter the frozen original binding.
  Removed the fabricated uninitialized Draining module and replace/restore registry helpers.
- Immediate refusal uses an actual server admission permit held across the real RPC. Unknown
  methods use a client-issued contract channel and a genuinely absent MethodId, retaining
  the independent count1 grammar and typed input writer. The corrupt compressed head really
  invokes the sender provider while receiver request/item decompression and business execution
  remain zero. Known bounded shape permits drain, not the old unknown-shape Session termination.
- Each negative sends128 items beyond a128-byte window, then proves same-Session Unary recovery,
  zero client active/reservation/send-stream owners, zero server routes and full128-byte credit
  restoration. Conflicting/malicious count fail-close remains covered by the unchanged frozen
  OneWay84 inventory; successful safe drain is not relabelled as Session termination.

### Ordinary CI ownership

- Added Probe and its three buildable auxiliary fixtures to the ordinary solution. The old
  Stub consumer intentionally remains outside that build graph. eng/run-contract-binding-tests.py
  is the shared Extended/three-platform Release entry: exact source-bound16 inventories,
  source resolver guard, raw logs and an exact five-member CS0535 old-Stub rejection. It stops
  on first unexpected failure and removes only each expected stale report before execution.
  CI uploads evidence even on failure; binding controls run before later full-suite gates.
- Python syntax checks pass. The complete new runner and revised solution build must still be
  executed before this wiring is accepted; no hosted result or package compatibility is implied.
- Full Integration's next real failure occurs at initial Prepare for separated dynamic Contract
  and Service assemblies: Bind incorrectly compares the Service module's assembly hash while
  Manifest advertises the Contract owner's hash. Fix that cold identity/receipt boundary with
  a separated-owner regression before revising the later queued-retirement expectations.

### Separated Contract/Service identity correction

- Corrected the real cold-path failure: Bind resolves the registered Contract owner's manifest
  under the existing registry gate, independently of the implementation module. Receipt capture
  checks the actual dynamic Service module assembly as registration owner, pairs that Service's
  publication generation/ID with the Contract hash, and rejects a Contract-only assembly claiming
  Service ownership. Combined Contract+Service groups retain the same behavior.
- Added two ordinary real-RPC separated-owner counterexamples, default and ExactRegistration
  receipt. They require genuinely different Contract/Service assembly hashes, zero activation
  at Bind, correct scalar receipt ownership/hash/generation, wrong-owner precondition rejection,
  and successful business invocation on the exact prepared receiver. Both pass.
- Next queued-retirement migration must preserve the new pre-business physical owner, not
  reinstate the old zero-lease queue assumption: hold retirement while an admitted selection
  physically owns R1, never retarget queued work to R2, and prove ALC release after that physical
  exit. No fixture expectation has yet been changed to conceal the preceding HashMismatch.

### CI entry accepted locally; queued work completes on its pre-cut owner

- The new shared CI entry passes at e905e7e89467cbc7b155e20371a9c4c925961586:
  all16 frozen reports (513 cases per transport), exact five-member old-Stub CS0535 rejection,
  and the single cold server resolver guard. Raw logs/reports are retained under
  artifacts/775-integration/binding-ci-entry-owner-fix. Hosted three-platform execution remains
  pending; the ordinary expanded solution still needs its complete build gate below.
- Queued retirement's14 shape/replace/unregister controls now pass. A pre-cut bound request
  holds one original call owner (and the exact stream counter where applicable), keeps graceful
  retirement pending while admission is held, then completes on R1 without reacquire or forced
  cancellation. Each shape verifies its actual result/order/notification and R1 activation,
  with zero R2 activation. The same retained handle explicitly prepares its new binding before
  later traffic uses R2. The prior zero-lease/mandatory-Unavailable expectation was an old model,
  not the selected bound-call contract; its failed migration log remains recorded.
- The collectible queued-Service control also passes: its ALC remains alive while the original
  queued selection physically owns it, then retires and collects within the unchanged30-cycle
  GC budget after queue/physical exit. It does not claim collection before ReferencesReleased.
  Weak queued identity is separate from the outer bound invocation's lifetime protection.

- The expanded ordinary Release solution now builds0 warnings/0 errors (17.12s), including
  the binding fixtures. This is still managed build evidence, not NativeAOT execution.
- Retained the independent zero-budget cancellation controls as well as graceful completion:
  compatible replacement cancels only the original queued module, never starts R1/R2 business,
  and releases its physical queue owner; an unchanged queued call's actual handler token observes
  its own module cancellation. Both pass. Compressed queued controls2/2 preserve zero decode/
  activation after forced replacement and exactly one decode for live R1. Queue cancellation5/5
  passes, retaining explicit OneWay disconnect cleanup after local no-Ack send completion.

- LoadTest.Tests77/77 and StreamLoadTest.Tests21/21 pass their full ordinary suites. These are
  harness/configuration regressions, not performance measurements.
- The RuntimeAssembly class's first remaining failure was an obsolete draining diagnostic on
  a new call; its assertion threw before releasing an intentionally noncooperative handler, so
  teardown masked it with a server fault. The test now always releases that handler in finally,
  and requires structured Unavailable/NoBoundRoute or Retired for new work. The original20ms
  drain timeout, one surviving physical call and eventual service disposal proof are unchanged;
  that focused control passes. Already-admitted call cancellation diagnostics remain separate.

- Multi-cluster dynamic controls pass10/10. Two disconnected registration/removal tests now
  construct local services without unnecessary Prepare against dummy ports, retaining Created
  state and collectible removal. Controlled child fixtures delegate the new opaque factory,
  channel and RuntimeContext API to their real externally owned registration source; published
  replacement performs that source's real local generation change before injecting cleanup
  failure. No fabricated handle, default identity or unsupported factory bypass is used. Actual
  multi-cluster RPCs still prepare the exact returned handle before invoking it.

- The complete RuntimeAssemblyIntegrationTests class passes70/70, including the separated-owner,
  graceful/forced queued generation, physical ALC and multi-cluster controls. Full Integration's
  next run reached370 passes before a test's exception-capture helper evaluated the retired
  child's synchronous guard outside its try. Capturing the invocation delegate now observes
  both synchronous and asynchronous rejection; RuntimeMultiClusterIntegrationTests passes2/2
  and still proves the old proxy cannot retarget the replacement slot.
- Unit refresh/reconnect simulated peers now explicitly advertise the synthetic contract they
  serve. Preparation is outside the tested RPC/gate, and a fresh physical replacement is bound
  before new business calls. Refresh3/3, pre-cut failure/commit-reservation6/6 and reconnect8/8
  pass. Other refresh race/redirect fixtures still need the same explicit boundary migration;
  no automatic RequestAccepted or default peer contract was added.
- Admitted dispatch parity remains6/6; production reference boundary passes10 projects,
  15 declarations/15 active references with the patched SDK. A first boundary invocation used
  the system SDK path and could not resolve global.json; rerunning with the selected SDK fixes
  only that execution environment, without changing the guard or its graph.
