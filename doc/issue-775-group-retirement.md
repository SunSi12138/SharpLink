# #775 dynamic registration-group retirement slice

Base: nonshipping `72813255f9aac2cf062b7407083edd6bb36a8c7d`, dev160d0cd unchanged.
Stage: implement and validate group admission cutoff, physical reclaim, ABA and ALC.
Receipt is gated on the hosted lifecycle matrix; no performance experiment is scheduled.

## Criteria frozen before the first hosted group run

- Windows/Linux x64 × real TCP/SharedMemory. Retain the previous exact99 control inventory
  with its60s watchdog, plus a separate **67-case lifecycle inventory** with a120s process
  watchdog. The lifecycle work is not a shortened replacement for the previous controls.
- One dynamically registered collectible module owns two services on two physical Sessions.
  Same-hash and changed-hash replacements close the old striped module gate before publishing
  the complete new registry. A held cut proves all four old tokens reject while the registry
  still contains the old group; no business activation/decode or retargeting is permitted.
- A pre-cut non-cooperative call remains protected after caller cancellation, throughout
  asynchronous per-call service disposal. Replacement cannot report physical release early.
  A separately held custom Codec must retain the original registration and invocation after
  retirement. Physical release and unregister then make the old server ALC collectible while
  the server, clients, idle bindings and newer generation remain alive. A deliberate app root
  is the negative control. Use up to10 full GC/finalizer cycles, no sleeps.
- Hold a real BindAck until replacement and invalidation finish. The scalar Ack must not root
  old code; the late Ack cannot publish readiness. A new Bind gets a new incarnation.
- Exercise70 real Release/Invalidate/rebind cycles on one Session. Old Request/Release/
  Invalidate cannot invoke or evict the new entry. Saturated uint32 incarnation counters
  reject without wrapping, expanding storage or reserving quota.
- Unexpected failure stops that subject and blocks receipt. Retain the original failed run;
  change a concrete cause before another subject. No relaxed assertion, retry-to-green or
  performance inference from watchdogs. All RPC/GC lifecycle execution is GitHub-hosted;
  local work is source/development, basic builds and source guards only.

## Ownership implemented in this subject

The private profile has magicB8, ABIsharplink-experimental-775-group-v2; no production
version is allocated. Dynamic server Bind validates under the same registry gate as group
retirement and records the registration's own immutable publication generation.

Actual request dispatch acquires the original module protection before reading collectible
binding payload/method/Stub/Codec. One captured selection travels through immediate and
queued paths and persistent decode; invocation activation consumes that protection rather
than trying to acquire the retired module again. The outer dispatch keeps it until all
invocation/service-disposal work returns. Complete method-facts ABI and all streaming shapes
remain outside this Unary/OneWay slice.

Replace prepares retirement batches and fallible codec/bookkeeping work, closes the shared
old module gate, clears its binding payloads and publishes the complete replacement. Notifications
are bounded and outside the cut. Unregister uses the same group cut. Module release has one
physical owner; duplicate drain callers join it. Assembly-keyed operation bookkeeping is removed
before ReferencesReleased is published.

Slots now recycle with a32-bit incarnation and never wrap; old immutable entries lose their
collectible payload before reuse. Client invalidation stores only64 per-slot high-water marks,
evicts exact old cached Acks and rejects invalidated late Acks. The existing owner-wide128
binding/64 operation caps and64-slot Session tables remain in force.

Local builds found and corrected private Fault access/local-variable shadowing, and the
fixture generator rejected a service implementing two contracts; the fixture now has two
separate services in one module. Basic profile/plugin builds then passed with zero warnings/
errors. Ordinary compilation remains subject to the exact-token/build-input collector and
basic host build; the unchanged Windows Unit Stop failure from #387 is retained, not rerun.

## Hosted result / receipt gate

First subject4b1e05d7/run43379674272 passed default basic builds but failed profile compilation:
the new completion publisher used a nullable unregister-result struct. Corrected to the
nonnullable result value; no RPC ran on that subject. The same repair adds five previously
missing real dynamic admission-queue controls before its first functional execution,
extending62→67 without removing any prior control or changing budgets.

Second subject8f4d2e55/run43380346207 passed the previous99-control matrices on Linux,
then the new fixture sent a raw custom Codec payload where the generated method expects
per-argument framing. The server rejected it as an invalid argument length. The fixture
now constructs the actual generated Proxy over the deliberately stale/scoped test receiver,
using the existing registration's codec provider; no hand-built substitute framing is added.
The67 lifecycle assertions and all budgets remain unchanged.

Third subject118e522f/run43380821006 passed all previous99 controls on both platforms/
transports. Same-hash TCP then proved the group cut, four post-cut rejections, zero business
decode and retained physical ownership after caller cancellation, before waiting for missing
invalidation until its original120s watchdog. The new control types had been added to parser/
payload/phase handling but omitted from the request-ID-zero control header classification.
That concrete protocol error is corrected. Drain ownership now starts before best-effort
notifications; a publication failure is logged, faults only that Session and cannot lose the
already-committed old module cleanup owner. No lifecycle assertion or deadline is relaxed.

Fourth subject42a83cea/run43381753829 passed the control plane, same-hash cut/reject,
physical disposal, old-A collectibility and unregister, then failed the immediate replacement-B
ALC collection check on both hosts. The next discriminating change isolates the Assembly
argument and unregister/unload work in a non-inlined helper before the checking frame runs,
following [.NET's unloadability guidance](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability).
It keeps the server/clients alive, all67 assertions, the deliberate-root negative control
and the same10 GC cycles. The failed observation remains unresolved until that subject runs;
it is not treated as harmless or permission to proceed to receipt.

Fifth subjecte603b0ae/run43383452697 passed both replacement variants, their ALC controls,
the held/late Ack ALC control, physical Codec and dynamic queued-generation controls, then
failed the final ABA ALC check on both hosts. That fixture acquired a process-wide catalog
snapshot immediately before its GC check; the snapshot includes the very collectible
plugin being tested. It now uses the fixture's already captured static Probe manifest,
avoiding an unrelated catalog root in the checking frame. No production change, extra GC
cycles, server/client teardown, assertion removal or receipt implementation is introduced.

Sixth subject4b15d94c/run43384276147 still failed the final ABA ALC check on Linux;
Windows also exposed a real unrelated-route NoBoundRoute during changed-hash publication.
The latter came from the inherited profile clearing every route on each Manifest refresh;
it now prunes only incompatible contract/hash routes, retaining unrelated bindings.
The ALC oracle now separates the whole metadata/reuse/unload operation into one non-inlined
method and checks only its WeakReference from the outer live-fixture frame, per the same
.NET guidance. This also excludes JIT temporaries for registry snapshots, not just catalog
snapshots. Assertions, GC count and live-server/client conditions are unchanged. A pinned
GitHub-only dotnet-dump helper collects targeted allocator roots if the assertion still
fails; raw heap stays ephemeral outside uploaded artifacts. No local diagnostic execution
or receipt implementation is introduced.

Seventh subject57db0c72/run43385682574 passed the previous99 controls in all four cells
and every group TCP assertion before the final ABA ALC observation on both hosts. Neither
group SharedMemory cell ran after that failure. Windows allocator roots show only handle
type10, which .NET10 defines as weak interior-pointer handles; these do not identify an
external strong owner. Linux allocator-object analysis returned incomplete output. The next
diagnostic subject therefore captures the context object, surviving fixture/async/module
objects and managed stacks, with analyzer exit status and full mapped-image dump data.
Only targeted text is retained; the raw heap is never uploaded. There is no product/fixture
behavior change and no extra GC cycle. ALC remains a failed gate, not a waived oracle.

Eighth diagnostic subjecta1b06857/run43387208326 reproduces the failure on both hosts and
identifies the same external strong root: ExecuteReuseAndUnloadAsync.MoveNext's native
stack slot → pre-unregister FrozenDictionary → ServiceRegistration[] → old RuntimeType →
LoaderAllocator. The awaiting collector was invoked inline by the worker's Task.SetResult,
so merely marking that async worker NoInlining did not pop its snapshot-bearing frame.
The incarnation-exhaustion check now runs in a **synchronous** non-inlined helper, which
returns before unregister and cannot retain that registry snapshot in the collector's stack.
This targets the measured fixture root; all67 assertions, ten GC cycles and live owners
remain unchanged. The next hosted matrix must establish the result before receipt starts.

Pending. Receipt implementation starts only after both transport/platform matrices pass.
Keep #775/#805 open; no PR, merge, final API/facts ABI/grammar, platform or performance
acceptance is implied by this development profile.
