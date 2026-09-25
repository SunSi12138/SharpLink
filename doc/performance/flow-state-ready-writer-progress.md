# Protocol progress while DATA credit is exhausted

Continue #735 on the existing Draft #742. This four-file increment preserves
all changes through `f76d1f1dfa84ba38239e247ce9f48573ab92d62b`, including the
queue-budget fix, full-population collector, TCP/standalone-owner diagnostics
and retained-evidence audit. It does not alter shipping `src/`, scheduling,
credit, socket options, defaults, case timeouts or any timing population.

## Deterministic actual-pump boundary

Two new checks exercise A-ready and B3-ready independently. Two real 4 KiB DATA
frames exhaust the negotiated 8 KiB stream/connection window. Before the peer
returns any credit, an actual Ping must traverse SendPump and be observed on
its byte stream; a third DATA must not bypass the hard window. Serialized
key-only WindowUpdate frames then travel through the peer SendPump and parser
and restart the third frame. B3 already has that frame queued, so the restart
requires a credit-only wake rather than a new producer notification. Final
checks require three released frames, 12288 credited bytes and zero prepared
bytes, with no queue rejection.

The fixture reads one frame at a time and does not mark the next buffered frame
examined. Five-second test bounds only fail a stuck operation; no sleep/retry
is used to infer ordering. A temporary mutation masking progress readiness
while the experiment is attached fails the Ping wait; the mutation is restored.
This rejects a broken integration policy, not proof of a shipping SendPump bug.

The existing test runner and minimal native host link the exact same added
source. The native linked-source allowlist is extended by this one reviewed
file without weakening its exact-set/no-executable-reference checks. All
existing queue-pressure and diagnostic tests are retained.

## Review and evidence boundaries

The existing protocol-progress and credit-only restart checks passed before the
final rebase. The combined f76d1f1d tree is separately built and checked before
publishing this increment. New-head CI must still run the real native host,
complete JIT controls and the full runtime unit suite. Reflection-disabled
managed execution is not described as NativeAOT execution.

Earlier local complete-unit invocations included an unchanged health-supervisor
log wait timeout (1896/1897, exit 2); an isolated passing replay is not claimed
as its root-cause fix. Failed hosted B2/ready-writer cases and all negative
performance controls remain recorded and are not replaced by successful retries.

The captured 0991a079 TCP failure continued receiving about 364/365 4 KiB items
per five seconds, with one complete connection window in flight. That is slow
progress, not proof of permanent lost wakeup. The separate TCP observer and
owner snapshots remain responsible for investigating the cause; these two
protocol-progress checks alone do not resolve it.

Existing exact-head be54089d JIT/native measurements remain historical evidence,
not performance results for this change. Full dynamic lifecycle/FIFO, duplicate
wire-credit semantics, stalled-transport cancellation/deadline service, receive
batching, cold allocation and complete default RPC acceptance remain open.
Keep #735 open and #742 Draft until those boundaries actually pass.
