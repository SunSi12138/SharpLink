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

Pending. Receipt implementation starts only after both transport/platform matrices pass.
Keep #775/#805 open; no PR, merge, final API/facts ABI/grammar, platform or performance
acceptance is implied by this development profile.
