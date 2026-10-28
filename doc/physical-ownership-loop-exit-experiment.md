# #931: inner loop exit experiment

Date: 2026-10-28. Status: **compiled, focused correctness checked, non-timed ARM64 mechanism observed; independent review and performance acceptance remain open**.

## Candidate and decision

Product commit `94adeae886c4ddfd0c02ec2dcb700cd97e9cad80` is based on
`ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`. The persistent worktree is
`/Users/sunsi/.codex/worktrees/ownership-931-loop-exit/SharpLink`, branch
`codex/931-loop-exit-experiment`.

The sole product edit is four added/four removed lines in
`src/SharpLink.Client/SharpLinkClient.Interceptors.cs`, inside
`InterceptedAsyncEnumerable<T>.GetAsyncEnumerator`. A false `hasNext` now breaks
the loop inside the existing inner try/catch; a true result reads `Current` and
validates nullability there. `yield return` stays outside that try/catch and
inside the existing outer try/finally. No helper, owner, field, cache, or extra
await was introduced.

The observed successful-path duplicate `hasNext` test disappears in the local
FullOpts ARM64 body. EH count, locals, object sizes, stack reservation and scalar
allocations do not improve. This is a narrow mechanism candidate, not a remedy
proven for the Linux x64/default-PGO dynamic100 regression. #931 remains No-Go
pending the existing client-only hosted ablation, independent review and any
subsequently authorized exact-candidate performance acceptance. No timing, push,
merge or release action was performed for this experiment.

## Equivalence boundaries checked

- Every deadline check, wait branch, catch filter and `ownershipTransferred = true`
  assignment remains at its original relative position. The inner cancellation
  terminal is still protected from a second outer transfer by that flag.
- `Current` is read only after a true result and remains inside both relevant
  catches. A false result now exits through the same outer finally without a
  default item assignment. A thrown `Current` carrier still transfers ownership.
- Normal completion/early reader disposal still cancels the lifetime before
  awaiting the original enumerator's disposal, and disposes the CTS afterward.
  Normal unassociated application failures retain original identity and the
  existing disposal-exception precedence.
- Abandoned work still starts with `ForceYielding`; it cancels the lifetime,
  waits for the pending move and its descendants, then disposes sequentially and
  waits for disposal descendants. CTS and module lifetime transfer code is unchanged.
- Mapper carrier transparency, dynamic channel lease handoff and owner-cycle
  handling are untouched. Existing retained-terminal/iterator, crossed-owner,
  codec and collectible-ALC checks were exercised.

The compiler does alter internal natural-exit lowering: the old `yield break`
sets its disposal-mode field before entering cleanup, whereas a normal `break`
enters the generated cleanup directly. Both execute the outer finally and
complete the public move with false; early `DisposeAsync` lowering is unchanged.
The retained native listings expose this difference for independent review.

## Focused correctness evidence

Environment: SDK 10.0.102, .NET 10.0.11, macOS 27.0.1, ARM64, TUnit 1.73.19.
The two Release project builds completed with **0 warnings and 0 errors**:

```sh
dotnet build test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release --nologo
dotnet build test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj -c Release --nologo
```

Existing focused unit classes: **Passed: 67, Failed: 0, Skipped: 0**; zero
cancelled/timed-out cases. Existing physical/queued integration cases:
**Passed: 5, Failed: 0, Skipped: 0**; zero cancelled/timed-out cases.

```sh
dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/(AbandonedClientOwnershipTests)|(AbandonedClientNestedOwnershipTests)|(AbandonedClientApplicationTerminalTests)|(AbandonedClientCodecOwnershipTests)|(AbandonedClientRetentionTests)|(AbandonedPhysicalWorkTests)|(AbandonedPhysicalWorkCycleTests)|(SharpLinkClientInterceptorDeadlineTests)/*' \
  --maximum-parallel-tests 8 \
  --results-directory artifacts/loop-exit-experiment/unit-results

dotnet test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll \
  --treenode-filter '/*/*/RuntimeAssemblyIntegrationTests/(DynamicStreamTerminalMustRetainPhysicalOwnersThroughSequentialDisposal)|(RetiringQueuedSelectionMustNotReleaseAdmittedPhysicalCleanupOrRetarget)' \
  --maximum-parallel-tests 8 \
  --results-directory artifacts/loop-exit-experiment/integration-results
```

The unit build preceded the product commit but compiled exactly its staged source
diff. The integration build followed that commit; its Client informational version
is `3.0.0+94adeae886c4ddfd0c02ec2dcb700cd97e9cad80`. No test source changed.
The first build shell wrapper attempted to assign zsh's read-only `status`
variable after the build; its wrapper failed, while the preserved compiler log
reports successful build with zero warnings/errors. The integration build and
both test processes exited zero.

## Non-timed scalar native comparison

The unchanged existing `eng/physical-ownership-probes/scalar/Program.cs` was built
once against the existing baseline Release libraries. Its identical executable
and common Runtime/Abstractions were used for both arms; only Client.dll was
replaced for the candidate. One FullOpts process per arm exercised the probe's
12 existing static/dynamic × timed/untimed × 0/1/100-item allocation shapes.
Here “timed” means a declared five-minute RPC deadline, not elapsed-time measurement.
The probe records allocations, layout and JIT, not throughput or latency.

The reused baseline Client is stamped `d51e6ed43ced6fc20a742b34f8f555ce04613ccd`.
The only later commit through ac17 changes documentation; the saved
`baseline-production-source.diff` from d51 to ac17 is empty. This is an
ac17-equivalent **production-source** baseline, not a falsely restamped ac17 DLL.
The candidate Client used here comes from the post-commit integration output.
Reflection metadata files record actual loaded locations, informational versions
and MVIDs.

| Quantity | ac17-equivalent baseline | loop-exit candidate |
|---|---:|---:|
| `MoveNext` IL bytes | 1920 | 1882 |
| EH clauses | 7 | 7 |
| Locals | 31 | same 31 types/order/pinning |
| IL max stack | 6 | 6 |
| Iterator / wrapper object bytes | 200 / 112 | 200 / 112 |
| FullOpts native body bytes | 5348 | 5308 |
| Main native frame reservation | 0x1B0 = 432 B | 0x1B0 = 432 B |

The 17 reflected iterator fields and their types are unchanged. Both native
bodies report FullOpts, ARM64, no PGO data. At baseline `G_M000_IG54`, `cbz w20`
selects the default/current result; `G_M000_IG72` tests `w20` again before yield.
Candidate `G_M000_IG54` sends false straight to cleanup (`IG72`) and true through
the getter to `IG71`, which yields without another `hasNext` test. The successful
path still contains the jump from the getter block to the yield block. This is
not a claim that every extra jump from #931 disappeared.

All 12 allocation rows are identical between arms: static untimed 752.0016,
static deadline 776.0016, dynamic untimed 1264.0016, dynamic deadline 1288.0016 B/call,
each for 0, 1 and 100 items. The fraction is the existing probe's amortized
harness residue. One local diagnostic process pair establishes no allocation
equivalence margin and no latency result.

No new Tier1/default-PGO or actual-TCP native capture was run for this candidate;
the local body above must not be compared numerically to the retained Linux x64
10.0.13 listings. The current hosted old/new Client ablation is a different
experiment and does not yet measure this candidate.

## Evidence location and next owner

All local evidence is under this worktree's `artifacts/loop-exit-experiment/`:

- `unit-build.log`, `integration-build.log`, focused logs and complete TUnit JSON/HTML.
- `candidate.patch`, `baseline-production-source.diff`, `dotnet-info.txt`.
- `scalar/commands.json`, `scalar/summary.json`, both arm `evidence/` directories
  with full native text, fields, allocations and reflection metadata.
- `scalar-built/`, arm `app/` directories and the small reflection-only
  `inspect-source/` diagnostic preserve the actual inspected binaries/inputs.

The author has not independently approved this design. The release owner's
independent reviewer should inspect the natural-exit lowering and the exact
product diff, then consume the focused evidence above. If the hosted ablation
does not support Client-path work, keep this isolated candidate and the original
No-Go. If this candidate later fails its authorized performance comparison,
do not stack more edits onto it: return to the technical decision tree's cold
ownership-helper alternative or retain the blocker. EH and frame reductions
were not observed here.
