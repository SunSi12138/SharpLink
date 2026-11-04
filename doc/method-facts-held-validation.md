# Held outbound stream facts and terminal ownership

This independent test-only slice extends the verified observer source
2a3bb3f0302a38b1e96beb672ad0c4464f01d796. No production code, D2 file or Cancel
regression is changed.

Existing source covers queued successful physical stream disposal for both
ServerStreaming and DuplexStreaming, and suspended ServerStreaming failure after
registry replacement/removal with immediate disposal. The new eight cases fill
the combined gap: both outbound kinds, normal/fault terminal, replacement/removal,
with actual MoveNext and DisposeAsync independently suspended in the server bridge.

Assertions bind the original call-state object and generation before registry
change and while DisposeAsync remains held. The dispatch remains unfinished and
both request permits remain held. After physical disposal joins, exactly one
invocation/disposal, no hot resolver query, no replacement invocation, the original
facts reference, and complete owner/permit drainage are required. Fault cases
add exact exception-reference and MethodFacts-reference mapper checks; successful
completion must never invoke the mapper. After dispatch and send-queue flush,
wire parsing requires exactly one stream-0 StreamComplete with the expected
success/Error status and mapped Internal error code, no response/data fallback
and no partial frame. Cleanup independently attempts bounded join,
registry restoration and bounded harness disposal while preserving primary failures.
A cleanup timeout fails the case; the existing outer process owner still owns
process termination/join. No timeout is counted as physical cleanup success.

The Cancellable flag represents a normal streaming request. No remote Cancel,
explicit cancellation, timeout race or previously blocked cancellation operation
is executed. Static registry mutation is not dynamic module retirement or ALC
unload. An extra original-registration snapshot lease is held for deterministic
restoration, so this does not prove release of the last registration reference.
The stream has no items, so input-stream data (including duplex inbound) and
throughput are not covered.
This is not a performance measurement or release acceptance.

Validation uses the unchanged proven process owner and native/JUnit classifier,
plus an independent 17-case catalog: nine existing path/math controls and eight
new singleton UIDs. Actual discovery must match exact source and all results must
pass in separate owned/joined processes. No raw runner artifacts are uploaded.
Local graph/source checks and all 20 existing classifier tests pass; runtime
results require the actual published commit's hosted run.
