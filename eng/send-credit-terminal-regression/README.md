# Send credit terminal accounting regression

The original independent correctness experiment started from fixed dev `f8ba89121c87a6410a0139c44d7db30eab877f41` and excluded the lease-capture and snapshot fixes. The live current arm now tests the actual integrated HEAD, including those fixes when present. Neither arm includes the B2 pilot or directional-lock changes.

The same one-case fixture runs against unchanged old dev and the actual checked-out product. `CompleteSendStream(error)` selects a terminal without returning actual outstanding credit. A later refund of an admitted but never-published item must therefore remain valid. In contrast, `AbortSendStreams` already refunds all outstanding credit, so the identical late refund must be ignored. `AbortException` alone cannot distinguish those two accounting states.

The fix adds one boolean settlement marker, set only by the existing full-abort refund and cleared on pooled-state reuse. It does not introduce a byte balance, window change, or second state map. The effective WindowUpdate calculation from #840 is unchanged. The integrated controller preserves the lease fix's under-gate capture and two-line call required by its diagnostic anchor, while remaining within the original file-LOC ceiling.

The runner changes no production code. Old dev must reproduce exactly `completeBefore=56, completeAfter=56, completeRetained=1` while its full-abort control remains at 64 with no retired state. The current arm must pass four focused runs and four complete 2,085-case unit runs with zero skips. All original source trees, product diffs, binary identities, exits and fresh HTML reports are retained. Unexpected failures fail the workflow without automatic retry.

This fixture establishes controller accounting. The separate B2 generated-bridge fixture forces the server-owner terminal before a blocked admitted serializer is released; that is a distinct integration test, not a claim already established by this controller fixture.
