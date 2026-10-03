# Prepared cancellation while the connection writer is blocked

This increment continues #735 / #742 from `b01d523932051a0a94594f0cdf406587fcf1e674`.
That parent already fixes complete NativeAOT population collection. Do not apply
an older collector patch over it or relabel a previous head's timings.

## Boundary and red control

A connection writer paused in transport Flush cannot service another owner
command. Previously, cancellation woke a producer's capacity waiter, but left
already prepared packets in the rings until SendPump reached its final Stopped
callback. Those packets could remain retained indefinitely behind blocked I/O.

Eight real-Pipe cases fail against the parent coordinator: A-ready and B3-ready,
16-byte and 4096-byte items, with and without a throwing external cancellation
callback. Each fixture first reads the actual accepted DATA bytes, holds the
writer's Flush completion, queues two unadmitted packets and blocks one further
producer on ring capacity. No delay is used to assume that output is blocked.

The revised callback drains only the prepared rings under their existing stream
gates, wakes capacity consumers, and returns the reference controller's debits
for those unadmitted packets. A B3 frame is not debited until writer selection,
so the callback never changes B3 connection/stream credit or release accounting.
The already removed writer-owned frame cannot be found in a prepared ring and
must remain outstanding. Repeated cancellation and final pump cleanup are
idempotent with respect to packet return and unsent refund.

HasWork and selection reject canceled preparation, including a recheck under
the stream gate. This prevents a stale ready notification from admitting more
canceled DATA or keeping an idle writer spinning. The callback never edits the
owner's linked ready list or its in-flight batch. Its cold cleanup counter uses
an atomic increment only because final stop may clean a different stream at the
same time; no new per-item RMW is added to ordinary publication.

Completion is deliberately not fabricated by the cancellation callback. The
fixtures require zero writer releases and a still-pending durable completion
while Flush remains held. After release, the real SendPump settles its one
original frame. Final stop joins cancellation outside the stream gates and
includes any prepared-cleanup errors. External callback exceptions are still
observable by the caller of CancellationTokenSource.Cancel.

## Scope and verification

The increment changes only guarded research C#, its exact native source
inventory, and this document. Shipping `src/`, protocol, socket options, window
sizes, timeout limits, FIFO policy and evidence populations are unchanged.
The two new C# files are explicitly named in the native inventory; the exact
set comparison is retained. An unlisted or missing linked source still fails.

Run the ordinary shared-source checks through the existing command:

```sh
python3 eng/test-ready-writer-native.py
python3 eng/prepare-ready-writer.py --apply  # disposable checkout only
dotnet run -c Release --project test/SharpLink.ReadyWriterAotEvidence \
  -p:PublishAot=false -- --ready-writer-self-test
```

All existing self-checks remain. New tests cover queued versus producer-held
versus writer-owned buffers, no fake debt refund, callback exceptions, repeated
cancel, and resume/final-stop ordering. The separate diagnostic build runs the
same checks; NativeAOT CI builds the same reviewed source inventory.

This closes one prepared-memory retention boundary. It is **not** complete
logical-call cancellation/deadline arbitration, dynamic stream retirement or
pool reuse, duplicate/clamped wire compatibility, default/full-duplex RPC
integration, or a fix for the observed TCP receive-window slowdown. It does
not shorten the latency of unresponsive transport I/O or forcibly reclaim its
writer-owned bytes. Newly added cancellation reads on the normal selection
path require the new head's performance matrix; older speedups are not reused.
Keep the issue open and PR Draft until the remaining acceptance is complete.
