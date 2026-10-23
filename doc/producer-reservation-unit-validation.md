# Producer selection-reservation ownership controls

These test-only controls validate production commit
`e2f4aded341abe90b6b99add06169434bc4ed725` against unchanged parent production
at `038ce547`. No compatibility adapter, production test hook, counter, timer,
or registry is added. The comparison copies the same six authored test files
onto each source arm.

## Executed inventory

`ProducerReservationOwnershipTests` adds **60 selected unit cases**:

| Cases | Ownership boundary |
| ---: | --- |
| 10 | One or three outgoing parameters; early response/EOF or caller cancellation while disposal remains held |
| 3 | One real sink task faults while two real sibling disposers remain held; members finish sequentially |
| 24 | Request serialization, synchronous/asynchronous writer, enumerator creation, synchronous/asynchronous MoveNext and Dispose failures, across three producer shapes |
| 3 | Zero-stream unary, one-way and server-stream selection transfer before serialization |
| 1 | Direct pending registration cannot consume an unrelated selection |
| 9 | Cancellation, deadline and close selected before pending registration returns |
| 2 | Supported and unsupported health selection transfer |
| 3 | Last producer release advances planned source retirement without self-wait |
| 3 | A three-member group completes synchronously before writer return/task tracking |
| 2 | Each of two retry attempts transfers before serialization, with success or error on the second response |

Every ordinary cardinality control holds one unrelated selection reservation.
The tested call must own exactly one additional reservation, independent of the
number of parameters, then leave the unrelated reservation intact. This detects
double release that a saturating zero-count assertion would miss. The refresh
controls explicitly release that unrelated selection first so only the real
held producer can prevent retirement.

The group writer calls the actual `IRpcClientStreamSink.SendClientStreamAsync`
with generated-style stream IDs `1..n`, then awaits `Task.WhenAll`. It does not
simulate group completion or detach user work. Held disposers await test-owned
tasks rather than blocking pool workers. Failure cleanup releases every gate,
consumes invocation/operation results and joins tracked producer lifetimes.

## Source-bound outcomes

On official .NET 10.0.13, with `DOTNET_PROCESSOR_COUNT=1` and
`TUNIT_MAX_PARALLEL_TESTS=8`, the candidate passes all 60 cases. The unchanged
parent fails **26 intended reservation assertions**, with **34 passing positive
controls**, zero skips. The failures are the 10 held-group cases, three
first-fault groups, three refresh cases, nine registration boundaries and the
direct pending-registration case. The earlier candidate checkpoint also passed
ten consecutive 60-case runs. Final-source runs and manifests are retained in
the local `artifacts/producer-reservation` evidence directories; no raw archive
publication is part of this change.

Builds have zero warnings/errors. Maintainability and project-reference guards
pass. `git diff --check` passes. Workspace-based `dotnet format` remains blocked
by the environment's MSBuild named-pipe permission restriction; no permission
bypass or formatting waiver is claimed. Full integrated CI, real TLS/ALC proof
and performance evidence are separate from these focused units.

## Explicit preexisting terminal characterization

A stronger diagnostic initially expected `ConnectionClosed` when a test closes
the pending table during duplex stream registration. Both source arms instead
return `DeadlineExceeded`: the existing
`PrepareGeneratedServerStreamAsync` missing-entry branch selects deadline when
the original caller token is not canceled. The exact failing expectation and
exception traces are retained as `test-terminal-diagnostic.log` on both arms.
The final test explicitly characterizes this unchanged duplex-close behavior;
it does not endorse that precedence or claim to repair it. The other close
shapes retain `ConnectionClosed`, and caller-cancellation token identity is
checked. Reservation release remains exact in all cases.

## Reproduction

Build `test/SharpLink.UnitTests/SharpLink.UnitTests.csproj` in Release with the
repository's pinned SDK (`-m:1 -nr:false -p:UseSharedCompilation=false`). Run the
produced DLL directly with the intended runtime and:

```sh
DOTNET_PROCESSOR_COUNT=1 TUNIT_MAX_PARALLEL_TESTS=8 dotnet \
  test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/ProducerReservationOwnershipTests/*'
```

Use the executed summary/report for the 60-case inventory, not a discovery-only
test listing. No existing workload budget, count, assertion or parallelism
setting is relaxed.
