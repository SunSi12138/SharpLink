# #775 deterministic static controls

Base: local nonshipping slice `2360a0ca5b782095f02d976ca303fc7890bb9871`, on dev
`160d0cd9a0550de5bf00c63b6007376c02debc20`. This stage gates expansion into dynamic
retirement/receipt. No performance experiment is scheduled.

## Acceptance frozen before the first hosted run

- Windows/Linux x64 × actual TCP/SharedMemory, once per changed subject, **99 exact cases**
  per cell: the previous 50 plus 49 deterministic controls. Missing, duplicate, extra cases
  or wrong source fail collection. Existing 60-second process watchdog is unchanged.
- The actual server/parser/send pump/queue and generated RPC remain in use. Instance-owned
  profile-only publication seams pause before send or after actual enqueue; real received
  Ack provides the fast-peer barrier. The transport wrapper gates the actual output Flush;
  a 1024-byte queue owns exactly 1020 bytes, leaving no room for the 39-byte Bind frame.
- Reuse the repository ManualTimeProvider: six seconds of pressure plus four to the original
  10-second Bind deadline; later operations retain their own deadline while ordered behind
  the predecessor. No sleeps, polling, timer-budget expansion or retries-to-green.
- Stop must wake Prepare while physical publication cleanup is held. Both explicit
  ClientConnection disposal and client Stop must wait for that cleanup; operation/binding
  leases remain owned until exit, then counts and supervisor reach zero.
- Each client/server owns at most 128 live binding reservations and 64 active control
  operations across its physical Sessions, alongside existing 64-per-Session slots and
  client-wide 64-site/64-waiter limits. Coalescing at capacity needs no additional slot.
  Two physical Sessions exercise the global operation cap; a separate client must progress.
- Binding-capacity boundaries use explicit real lease/table occupancy to fill 127 slots,
  then real Bind consumes slot 128 and another Session must receive ResourceExhausted.
  Server operation saturation uses 64 explicit leases, then a real Bind must receive a
  Capacity Ack without another waiting control operation. Refusals use the existing
  bounded normal send queue synchronously; a full/closed queue fails transport, without
  an unbounded refusal task. These are deterministic boundary controls, not 128-client load.
- Unexpected build/assertion/timeout stops that subject and blocks widening. Retain original
  failed runs and fix a concrete cause on a new subject; do not relax budgets or assertions.
  Performance is a later GitHub-only stage with separately preregistered A/A criteria.

## Implementation and ordinary-build boundary

Added client/server aggregate budget ownership. Successful bindings retain their lease
until the Session table/cache closes; in-flight operations retain leases through physical
cleanup. Close now seals new Bind, cancels publication and joins its finished owners.
Ack commit also checks the original monotonic deadline before publishing a route.

The ordinary compilation remains token-identical to the prior source, including strings/
comments; build inputs remain unchanged. The collector verifies this rather than rerunning
the unchanged ordinary Unit suite. Its existing Windows Stop failure (2211/2212, source
394dd4f1/run 43353655418) stays unresolved in #387; Linux remains 2212/2212. No paused
investigation is restarted. Default/profile basic builds are still required on the hosts.

Local profile build initially caught five fixture compilation errors (missing Sdk import
and explicit server Stop timeout), corrected without running RPC. The ad-hoc exact-line
default-source comparison also rejected added blank lines; exact-token comparison retains
all strings/comments and passes. Existing admitted-dispatch controls pass all five tests.
The final profile Release build has zero warnings/errors. Local RPC/performance is not run.

## Hosted result and expansion decision

Pending. Keep dynamic retirement/receipt out of this stage until the frozen matrix and
ownership checks pass. Even a pass does not complete production API/facts ABI/grammar,
five-family physical ownership, package/Trim/NativeAOT, ordinary regression or performance
acceptance. #775/#805 remain open; no PR is proposed at this stage.
