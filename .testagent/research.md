# 0.8.27 regression-test research

## Target inventory and evidence candidates

- A successful response with a missing payload bypasses its registered Codec and silently materializes `default(T)`.
- Supplying a consumer cancellation token to a pooled response stream replaces the call/lease token instead of preserving both cancellation owners.
- `SharpLinkBufferWriterPool.Return` can enqueue into a detached queue when `Dispose` wins between the pool snapshot and `Enqueue`, retaining an ArrayPool lease after disposal.
- A hosted Server run loop that completes successfully after startup is silently ignored, leaving the Host alive after its critical service exits.
- `AnonymousPipeClientTransportFactory` resets its one-shot gate after a failed attempt and permits reuse of an offer whose inherited handles may already have been consumed or closed.

## Acceptance checklist

- Payload-bearing calls always delegate even an empty payload to the registered Codec; payload-less acknowledgements reject unexpected bytes.
- Stream lease and consumer cancellation tokens remain independently effective and their registrations are cleared before pooling.
- Every Return/Dispose ordering either retains the writer in the live pool or releases its backing array; no detached queue remains populated.
- Unexpected successful Server run-loop completion logs/stops the Host, while normal application shutdown remains quiet.
- An anonymous-pipe offer remains permanently one-shot once the first connection attempt begins, including failure paths.
- Normal response, streaming, pooling, hosted-stop, and transport hot paths show no material regression.

## Audit guardrails

Keep-alive durations beyond the native integer-seconds range are a separate confirmed-looking boundary that still needs isolated evidence; it does not count in this batch. Direct string wire encoding and arbitrary unmanaged ABI/padding require a versioned compatibility design and are not opportunistically changed here.
