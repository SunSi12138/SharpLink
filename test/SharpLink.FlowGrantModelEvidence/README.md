# Deterministic flow-grant evidence oracle

This is a newly authored serial conservation and admission model for Issue #735.
It does not link any production runtime, transport, serializer, generated RPC,
or code from the closed #800 / #803 experiments.

Run it with the repository's .NET 10 SDK:

```sh
dotnet run -c Release --project test/SharpLink.FlowGrantModelEvidence/SharpLink.FlowGrantModelEvidence.csproj
```

A successful execution writes one JSON document and exits 0. An invariant or
scenario failure throws and exits nonzero. Preserve stdout, stderr, exit status,
source SHA and compiled-binary provenance together. JIT PGO on/off runs can use
the same built DLL. This project does not claim NativeAOT or RPC evidence.

## What is being modeled

One authority owns free connection credit F. A stream owns an unspent connection
grant G, stream credit C, producer-reserved debt R, and writer-owned debt S:

```
connection window = F + sum(G + R + S)
stream window = C + R + S
```

Idle grant is deliberately excluded from stream debt. Otherwise a late
WindowUpdate could repay an unused grant to F while the stream can still spend G.

A reservation consumes G/C and becomes producer-owned R. Handoff changes R to S
without returning credit. Only an unhanded, unsettled ticket may be returned as
unsent. Abort settles its stream's grant and debt exactly once; a later update
has zero effective delta. Complete releases idle grant but retains debt until
updates settle it. Object reuse changes the lease; an old handle cannot reserve.

Pressure admission reclaims parked grants independently of the stream producers.
It admits requests in FIFO order among streams with stream credit. An eligible
head lacking connection bytes stops later requests; a stream-credit-blocked
head can be skipped. This describes the model's fairness rule, not a claim that
all current production admission details are identical.

## Coverage and update schedule

The 27 count rows cover:

- Grant targets 256, 1024 and 4096 bytes.
- Items of 16, 64 and 4096 bytes.
- 1, 16 and 128 active streams, each sending 1024 items.
- Unchanged current default windows: 1 MiB per stream and 16 MiB per connection.
- An eager peer-consumption model with the current default update thresholds:
  first return a stream's pending consumed credit at 524,288 bytes; otherwise,
  at 8,388,608 bytes of connection pending consumption, return pending credit
  for every stream. Completion returns each stream's actual remainder.

The JSON includes the exact update schedule and numbers of threshold flushes.
All three grant sizes use the same producer order and receiver update policy.

Eighteen scenario checks run six scenarios at all three grant sizes:

1. Reclaim grants from inactive producers so another stream can advance.
2. Oversized borrowing, partial repayment, full repayment and excess updates.
3. A stream-credit-blocked head does not starve another eligible stream.
4. Abort, late update, unsent cleanup and original-exception identity.
5. Duplicate refund rejection, writer-owned refund rejection, tombstone and
   same-object/new-lease ABA rejection.
6. Eligible FIFO admission, cancellation wake and independent control events.

Two newly authored invalid controls are also recognized:

- Counting idle grant as stream debt violates connection conservation on update.
- Disabling owner reclamation strands all credit in an inactive stream's grant.

The JSON therefore contains 20 check descriptions. These negative controls are
not historical red/green reproductions of #800 or #803.

The scenario windows are intentionally tiny algebra fixtures (for example,
stream 2 / connection 4 bytes for a 6-byte oversized item). They are labeled as
toy scenarios and never contribute to a production performance claim.

## Meaning of the counters

AuthorityVisits, Refills, LocalDebits, WindowUpdates, PressureScans and related
values count explicit **logical events in this model**. Refills includes
attempts that cannot currently acquire connection bytes. LocalDebits includes
admitted reservations, including the dedicated oversized path. An authority
visit is not a measured lock acquisition, context switch, queue command or
thread handoff.

The model runs conservation checks by scanning state and allocates
Ticket/Request objects as an ownership oracle. Those operations intentionally
make its CPU/allocation unsuitable for production comparison. It reports no
nanoseconds, lock/CAS counts, B/item, latency or RPC throughput.

## Boundaries that still require production tests

Serial atomic transitions do not establish .NET memory ordering, lock ordering,
multi-producer safety, lost-wakeup freedom or concurrent linearizability.
Production needs a state-local synchronization protocol for lease/terminal/epoch
validation and grant consumption, with owner reclamation participating in that
same protocol.

The model assumes a well-formed peer returns only writer-owned bytes. It applies
the #840 effective stream delta, but it does not solve identification of an old
duplicate update while new debt exists; Protocol v2 does not add a generation to
these modeled messages. Model handles identify generations for the test oracle;
that must not be mistaken for wire routing behavior.

No actual frame is serialized or buffered. No prepared-byte limits, pooled
backing capacity, state table capacity, Request/Data/Complete wire ordering,
compression credit, codec snapshot, deadline, consumer Dispose or FlushAsync is
implemented. The independent Progress operation only checks that the model
authority can accept a control event. It cannot prove a blocked network writer
can transmit a progress frame.

A successful model run is a prerequisite mechanism check, not a production Go.

## Review history retained

Before the first build/execution, review found that an initial artificial
64-items-per-stream update schedule would exhaust the 16 MiB connection window
for 128 streams of 4096-byte items before any update occurred. The harness was
corrected to model the existing connection-consumption threshold described
above. Neither the production windows nor load were increased or reduced.
This was a pre-execution harness defect, not evidence of a production deadlock.

The first actual Actions run, `38164657442`, failed during compilation with
`CS9124`: the primary-constructor parameter `connectionWindow` was both captured
as state and used to initialize `Free`. The repository's warnings-as-errors
policy remained enabled. The follow-up replaces that primary constructor with
an explicit constructor and readonly configuration fields; no analyzer or
warning is disabled. The original failing build log is retained as
`evidence/new/38164657442/grant-model/build.log`. That failed run executed no
model scenarios and provides no model-success evidence.

