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
  production friend assembly. Their source inventories remain intact; runtime execution is
  pending migration/build, not assumed green from the historical profile.
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
