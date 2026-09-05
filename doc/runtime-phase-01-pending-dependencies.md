# Runtime Architecture Phase 01: explicit pending dependencies

`PendingRequestTable` now has one construction contract: the caller supplies capacity,
`IRpcCodecProvider`, `IPendingCallOwner`, and `TimeProvider`. All four arguments are required
and non-null. The table borrows these services and never disposes them.

The production owner is `ClientConnection`. It receives one `SharpLinkRuntimeContext` and
passes that Context's codec provider and time source to its pending table. Phase 01 uses the
system provider supplied by the Context. Phase 08 owns public `UseTimeProvider` configuration,
monotonic `RpcDeadline`, and replacement of the remaining native Timer/Stopwatch scheduler;
this phase only establishes the explicit dependency and ownership channel.

## Hidden RuntimeContext audit

The removed production fallback was:

```text
PendingRequestTable -> new SharpLinkRuntimeContextBuilder().Build().Codecs
```

It retained a child codec provider while losing the disposable Context owner. No equivalent
fallback remains in `PendingRequestTable`, and every production, test, and benchmark call site
now supplies an explicit dependency set.

The repository-wide audit also found these separate Phase 02 targets, all of which are now
resolved by `doc/runtime-phase-02-session-construction.md`:

- `SharpLinkRuntimeContext.Default`, including the pre-binding defaults in `RpcSession` and
  `SharpLinkClient`;
- nullable RuntimeContext constructor fallbacks in `SharpLinkClient` and `SharpLinkServer`;
- the nullable codec fallback in `PooledAsyncStreamDispatcher`.

The test-only codec fixtures remain explicitly named test dependencies; they are not production
fallbacks and never participate in Session construction.

## Executable ownership evidence

`PendingRequestTableTests` verifies that null codec, owner, or time dependencies fail at
construction; terminal races notify the owner exactly once and balance its active count; an
injected time source controls the capacity-deadline check; and repeated table disposal does not
dispose caller-owned codec, owner, or time resources. The Phase 00 five-way race continues to
cover Response, Cancel, Deadline, Disconnect, and GoAway with 100 seeded repetitions.

## Pending hot-path comparison

The Phase 00 `PendingRegisterAndComplete` benchmark was run on the same Ubuntu 26.04 / Ryzen
9 7950X host with the same explicit no-op owner in both sources. Each side used three launches,
three warmups, and twelve measured 100 ms iterations (36 result samples):

| Source | Mean | P50 | P99 | Operations/second | Allocated |
|---|---:|---:|---:|---:|---:|
| `dev` `e797060` | 61.40 ns | 61.37 ns | 61.92 ns | 16,286,297 | 0 B |
| Phase 01 | 61.33 ns | 61.34 ns | 61.45 ns | 16,304,597 | 0 B |

The measured mean changed by approximately -0.1%, well inside the Phase 00 3%–5% manual
noise envelope. The constructor dependency changes add no per-call allocation or lock
contention; setup and dependency construction remain outside the measured method.
