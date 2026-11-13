# #775 static Bind slice: functional result and continuation

Base: dev `160d0cd9a0550de5bf00c63b6007376c02debc20`.
Tested subject: `f21a5fc36a6fff729c167a66d18e3b0981775534`.
Status: **nonshipping static vertical slice passed its functional matrix; no PR/merge or performance acceptance.**

## Four real transport cells passed

[GitHub run 43354605843](https://github.com/SunSi12138/SharpLink/actions/runs/43354605843)
ran the existing RpcSession/parser/send pump, ClientConnection pending/reservation/P2C,
server admission, generated Proxy/Stub and actual codecs. It did not substitute a scalar
model or another RPC stack. Each JSON report was checked against the frozen exact case
set and source SHA, with no missing/duplicate/extra cases.

| Host | TCP | SharedMemory | Runtime |
|---|---:|---:|---|
| Linux x64 | 50/50 | 50/50 | .NET 10.0.15 |
| Windows x64 | 50/50 | 50/50 | .NET 10.0.15 |

The 50-case inventory covers bounded binding slots/creator tokens, local-only construction,
absence of old Get/raw-channel APIs, finite Prepare and waiter-cancel isolation, real BindAck
without service activation, ready synchronous Prepare/coalescing, generated/manual Unary,
metadata/custom codec, OneWay receiver completion, unknown method/full-token rejection
before business decode/activation, foreign local owner/assembly, equal numeric tokens on
different physical Sessions, producer suppression for unsupported shapes, queued Unary/
OneWay resumption, cancellation and drained task ownership at Stop.

### The ordinary regression is still red on Windows

The initial subject `03ef5628` / run 43350986775 passed 2212/2212 default UnitTests on each host.
After the experimental Stop-task fix, the latest ordinary regression on source
`394dd4f158dd1f457ff515ebe5f728fc4132d652`,
[run 43353655418](https://github.com/SunSi12138/SharpLink/actions/runs/43353655418), was:

- Linux: 2212 passed / 0 failed / 0 skipped.
- Windows: 2211 passed / 1 failed / 0 skipped. Existing
  `HugeBuiltInJitteredRetryDelayShouldRemainCancellable` timed out at its original two-second
  Stop wait (line 201). This observation is pending in #387; its cause is not established.

No retry, quarantine, timeout/assertion change or investigation was added for that failure.
The final probe-only subject has identical production/build inputs to `394dd4f1`, so it
explicitly reused those already-executed results, **including the failed Windows result**.
Its default step was skipped, and the retained artifact says this is not a passed gate.
The functional matrix does not turn the earlier red regression green.

## Use the nonshipping source

On a GitHub runner, with the repository-pinned SDK:

```sh
dotnet build eng/775-slice/Probe/SharpLink.BindingSlice.csproj -c Release \
  -p:SharpLink775StaticSlice=true
dotnet eng/775-slice/Probe/bin/Release/net10.0/SharpLink.BindingSlice.dll tcp
dotnet eng/775-slice/Probe/bin/Release/net10.0/SharpLink.BindingSlice.dll sharedmemory
python eng/775-slice/verify.py artifacts/775-slice "$GITHUB_SHA"
```

The ordinary build retains the current product contract. The explicit development profile
contains one scoped receiver: no old IRpcChannel type, raw client cast or Get facade. Its
slice assemblies are nonpackable. This is an implementation vehicle, not a second shipping
compatibility mode. Local work was development/basic builds only; all RPC execution was
hosted. No latency/CPU/allocation benchmark was run.

## Frozen experimental scope

- Private magic `0xB7`, Bind/Ack types 240/241 and ABI
  `sharplink-experimental-775-scoped-static-v1`; these do not allocate the final 3.0 grammar/ABI.
- Existing 15-byte header; Bind body ContractId64+Hash128 = 24 bytes. Ack status64+ContractId64+
  BindingId64+RegistrationId64+generation64 = 40 bytes. Ordinary route BindingId64+MethodId64=
  16 bytes, retaining existing deadline/metadata/compression framing.
- Static registrations and fixed endpoint/pool only. Three streaming entries, client-stream
  OneWay and dynamic registrations fail closed. Receipt/group retirement and slot recycling
  remain outside this slice.
- 64 slots per physical Session, 256 declared methods per binding, 64 live local sites and 64
  simultaneous Prepare waiters. Slots do not recycle before Session destruction; wire IDs
  cannot expand the table, and distinct creators do not alias a previous token.
- Bind has one original 10-second monotonic budget including ordered queue publication/
  backpressure and Ack. Correlation is installed before publication; an early Ack cannot
  publish a route until enqueue completes. Actual fast-peer publication races still need
  deterministic transport controls before broader integration.
- RpcLocalContract contains issuer/generation/contract/hash scalars. Generated and manual
  channels share one weak-cached exact site. Failed factories roll back unpublished demand
  without canceling concurrent successful construction. Factories queue work, never wait
  for the network. A handle's Service and Prepare retain the same receiver.
- Prepare requires a positive finite timeout no greater than SharpLinkTimer.MaximumDelay;
  individual cancellation does not cancel shared Bind work, and wakeups do not restart its
  deadline. Ready Prepare is synchronous and sends no fresh Bind.
- Immediate/queued static dispatch carries the original registration and immutable method
  object; persistent decode changes the byte owner, not that captured object. This is not
  the complete five-family once-per-RPC facts implementation.

### Error order in the implemented slice

| Boundary | Order |
|---|---|
| Local selector | null where applicable → terminal client → exact issuer/generation/assembly/contract/hash → static-only support |
| Unary/OneWay | codec-null → local terminal/receiver/kind/no-stream → caller cancellation → one interceptor/control generation → original deadline → confirmed exact Session reservation → serialization |
| Prepare | invalid timeout → caller cancellation → terminal client → one original readiness deadline |
| Server | bounded frame/envelope → full token and method/shape → existing request admission → decode/activation/invocation |

The inherited Unary envelope deadline rejection precedes binding lookup and remains
pre-business; its final protocol ordering is still a #775 design/review obligation.

## Source corrections and checks

The historical #862 static mechanism was adapted onto actual dev, without its obsolete
mechanical file move or broader held stacks. Publication now has an ordered budgeted
commit; current queued continuations retain their captured method object. A real
experimental Stop race was found in source review: work queued before supervisor
registration could arrive after Drain. Demand tasks are now registered under the existing
pool/Seal publication gate before execution, stop pulses do not create work, and the
site-cache gate is not held while entering that publication gate.

Local SDK 10.0.302 profile Client/Server/probe and ordinary UnitTests Release builds had
zero warnings/errors; the temporary feature-band override was restored, not committed.
The project-reference guard passed 10 projects / 15 active references. The original raw
admitted-dispatch comparison reported two failures on conditional source text. The guard
now selects the explicit compilation profile and passed 5 tests: default compilation
retains the full original resource skeleton and exact string/comment tokens; conditional/
literal controls and queued-profile identity checks were added. Unknown conditionals fail.
Canonical formatting,
maintainability, API/ABI/package and platform gates remain unaccepted for product integration.

## Failure history retained

Before the first run, acceptance required the exact frozen case inventory and subject on
each host/transport. Unexpected build, assertion or timeout fails that subject. Repairs
require a changed subject and retained failures; retries-to-green, sleeps, removed checks
and relaxed budgets are not accepted. These are functional controls, not performance gates.

| Run / subject | Actual result and correction |
|---|---|
| 43350986775 / 03ef5628 | Default Unit 2212/2212 on both; probe stopped before RPC because MaxQueuedBytes was missing. Added a 1 MiB fixture queue bound. |
| 43351755387 / b3b6f623 | Collector directory depended on skipped default step; no probe, no artifact. Saved original job logs and made directory creation unconditional. |
| 43352032227 / 545fe097 | Profile built; client fixture omitted mandatory request-timeout selection. Selected recommended `UseRequestTimeout()` (30 s), without changing Prepare/queue/watchdog budgets. |
| 43352421151 / 226c690f | Pure table fixture looked for obsolete server field alias. Changed it to inspect the actual ServerServiceModuleRegistry owner. Source review also led to the separate Stop ownership fix. |
| 43353282296 / 688c4485 | Collector propagated handled git-diff status 1. Settled its status and compared against actually executed default inputs. No probe ran. |
| 43353655418 / 394dd4f1 | Linux default 2212/2212 then 36 TCP controls; second-client fixture dereferenced a builder whose transport owner had transferred. Captured endpoint before Build. Windows default 2211/2212; probe skipped. |
| 43354605843 / f21a5fc3 | Four functional cells 50/50. Prior Windows regression failure retained; no performance acceptance. |

No subject was rerun unchanged to seek green. The inventory evolved 40 → 49 with bounded-table
controls → 50 with strict Stop-supervisor evidence; no assertion was removed or relaxed.
Original raw artifacts/logs/TRX and source history are preserved offline. Temporary Action
and its validation branch are removed after evidence capture; historical runs remain.

## Next owner and completion boundary

The next #775 work is deterministic Bind publication/backpressure/early-Ack/cancel/drain
controls and global quota ownership on this real path, then the reviewed production
API/combined facts ABI and grammar. Follow with registration-group retirement, receipt,
ABA/reclaim/ALC, all five families/RequestAccepted/OneWayStream, and physical decode/producer/
disposal obligations from #931/#933/#964. Do not adopt those closed branches wholesale.

Resolve or explicitly disposition the recorded Windows regression before final canonical
acceptance. Package/ref/forwarder/external-consumer, Trim/NativeAOT and final platform tests
belong to the integrated head. Performance remains a separate GitHub-only stage with A/A
validity, precision, budgets and stop conditions registered before running. #732's No-Go/
Hold/INCONCLUSIVE evidence remains in force. Keep #775/#805 open; no PR is proposed now.
