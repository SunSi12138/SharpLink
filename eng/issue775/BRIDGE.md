# #775: exact no-stream bridge experiment

This is a new two-file candidate, not the rejected detached BindingSite patch. Base is
6cfb09e7dd6b68695f92d28f726340bc8eb39e1d. CI applies SHA256-locked bridge.patch in an
isolated checkout, creates a deterministic LOCAL commit, and records source/tree/diff.
CI has read-only repository permissions; the candidate is not pushed into src/.

Hypothesis: the extra static-binding argument plus an unused empty-stream argument
prevents a cheap forwarding boundary. For exactly RpcNoClientStreams, omit the stream
argument from the internal forwarding call, not from IRpcChannel/generated/public ABI.
The specialized entry is the entire old entry body with default(RpcNoClientStreams)
substituted and experiment conditionals resolved. A byte-for-byte body check rejects
drift. Keeping the duplicate entry is an experimental cost, not an approved design.

Shape rejection still runs FIRST and returns ValueTask.FromException. All other stream
writer types retain the old call and actual writer. The new entry retains null-codec,
pre-cancellation, interceptor generation, control resolution, BindingSite, telemetry,
logical-call accounting and all exception/finally behavior. Core emission, P2C/admission,
retry, queue limits, server, authorization, persistent fix and budgets do not change.
Default-compiled source remains empty for both experimental files.

Before any performance test: both old/candidate must pass 443 smoke checks, existing
416 control-site semantic assertions, 12 persistent cases, plus 46 bridge assertions
across TCP/SharedMemory and exact/custom writer types. Bridge checks preserve faulted
ValueTask vs synchronous exceptions, failure precedence, no pre-Prepare serialization,
no stream producer activity, 2048 generated business completions and later Unary.
This does not prove all races, every custom writer behavior, or production readiness.

The existing bounded sender diagnostic drives four actual generated caller Tier1 cases
per arm on one VM, same SDK/two CPUs/tiering/PGO. No timing slots are executed; diagnostic
elapsed time is not a performance score. Header coverage alone cannot prove a tail jump:
review complete old/new channel and client listings, including fallback/error paths.
Only after semantics/default regression and mechanism review may a separately frozen
candidate be compared against original dev and this old static source with unchanged
canonical ABBA/BAAB, allocation budgets and +512 control. No repeated favorable selection.
PR remains Draft; no merge or Replace/Receipt/Streaming production expansion.
