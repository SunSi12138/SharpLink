# #775 static Bind slice — nonshipping implementation

Base: `dev@160d0cd9a0550de5bf00c63b6007376c02debc20`.
Status: development slice; hosted functional validation pending. No performance acceptance.

## Run the actual transport path

```sh
dotnet build eng/775-slice/Probe/SharpLink.BindingSlice.csproj -c Release \
  -p:SharpLink775StaticSlice=true
dotnet eng/775-slice/Probe/bin/Release/net10.0/SharpLink.BindingSlice.dll tcp
dotnet eng/775-slice/Probe/bin/Release/net10.0/SharpLink.BindingSlice.dll sharedmemory
python eng/775-slice/verify.py artifacts/775-slice "$GITHUB_SHA"
```

Transport execution is assigned to temporary GitHub Actions. Local work is development
and basic builds only. The ordinary build retains the existing product contract; the
explicit nonshipping profile has one scoped receiver and no old channel type/raw client
cast/Get facade. The profile disables packing. It is not a second product mode.

The implementation reuses the existing `RpcSession` frame parser/send pump,
`ClientConnection` pending-call/reservation/P2C path, server admission, generated
Proxy/Stub and codec ownership. The historical #862 static mechanism is input, not an
adopted stack or performance baseline. Publication ordering/budgets, scoped factories,
pre-Connect preparation and current queued continuations have been reworked on dev.

## Frozen experimental boundaries

- Private magic `0xB7`, Bind/Ack types `240/241`, experimental generated ABI
  `sharplink-experimental-775-scoped-static-v1`. These do not allocate the final 3.0
  grammar/ABI. Production magic `0x89` is not interpreted as this binding dialect.
- Existing 15-byte frame header; Bind body is ContractId64 + full Hash128 (24 bytes).
  Ack is status64 + ContractId64 + BindingId64 + RegistrationId64 + generation64
  (40 bytes). Ordinary request prefix is BindingId64 + MethodId64 (16 bytes), with
  existing deadline/metadata/compression rules retained.
- Static registrations only, fixed endpoint and its ordinary connection pool. Dynamic
  modules and client-stream shapes are rejected explicitly. No receipt/group retirement,
  slot recycling or full #732/physical-stream acceptance is claimed.
- 64 slots per physical Session, 256 declared methods per binding, 64 live local sites,
  64 simultaneous Prepare waiters. No wire number causes allocation/table expansion.
  Slots never recycle before Session destruction; the full token must match.
- Bind operations have one original 10-second monotonic budget covering ordered queue
  publication/backpressure and Ack. Correlation is installed before publication; an early
  Ack cannot publish a route until enqueue completes. Different creator operations do not
  alias a previous token. Late issued replies cannot revive completed operations.
- `RpcLocalContract` contains issuer/generation/contract/hash scalars only. Manual and
  generated receivers share one weak-cached exact site. Factory failure rolls back an
  unpublished site without canceling a concurrently successful factory's demand.
- `CreateProxy` and `CreateContractChannel` are local; they queue bounded background
  demand. `RpcProxy.Service` and Prepare capture the same receiver. Prepare has a required
  positive finite timeout no greater than `SharpLinkTimer.MaximumDelay`; reconnect and
  wakeups do not restart it. Canceling one waiter does not cancel shared Bind work.
- Immediate and queued static invocation keep the original registration and immutable
  method object. Persistent decode changes the byte owner, not the captured method.
  This is the static slice, not the complete once-per-RPC facts contract.

## Error order for this slice

| Operation | Frozen order |
|---|---|
| Local selector | null input where applicable → terminal client → exact issuer/generation/assembly/contract/hash → static-only support |
| Unary/OneWay | codec null guards → local terminal/receiver-contract/kind/no-stream guards → caller cancellation → one interceptor/control generation → original deadline → confirmed exact Session reservation → serialization |
| Prepare | invalid timeout → caller cancellation → terminal client → one original readiness deadline; a ready receiver completes synchronously without a new Bind |
| Server request | frame/flags/bounded envelope → full token and method/shape → existing per-request admission → decode/activation/invocation |

The inherited unary envelope deadline rejection currently precedes binding lookup; it
remains pre-business. This slice does not label that ordering as the final #775 protocol
decision. Streaming methods remain explicit unsupported entries, not removed features in
a proposed shipping API. No weakened correctness, retry or performance policy is introduced.

## Hosted validation fixed before the first run

Run once per Windows/Linux host and per TCP/SharedMemory transport. Compare reports to
the committed exact case list, reject missing/duplicate/extra cases and wrong source SHA.
The exact inventory is 40 cases per transport/host. Cases cover local-only construction, public API absence, finite preparation/cancel isolation,
real BindAck, synchronous ready preparation, generated/manual RPC, metadata/custom codec,
OneWay receiver completion, zero service activation at Bind, full-token/unknown-method rejection before business decode/activation, queued Unary/OneWay resumption,
foreign owner/assembly, equal numeric tokens on distinct physical Sessions, unsupported
producer suppression, cancellation and operation cleanup.

Any unexpected build/assertion/timeout fails that subject. Keep original logs; investigate
and change source before a new attempt. No retries-to-green, sleeps, skips, budget changes
or performance inference from these functional watchdogs. Default UnitTests build/runtime
is a single ordinary regression run, not a restart of #387 investigations.

No latency/CPU/allocation benchmark is scheduled in this stage. A later performance run
requires separately registered A/A validity, precision, budgets and stop conditions, and
must use GitHub Actions. #732's No-Go/Hold and INCONCLUSIVE evidence remains in force.

## What still prevents integration acceptance

Final public caller migration and combined complete-facts ABI; actual production grammar
and quotas; receipt/group retirement/ABA/reclamation; all five families and physical
decode/producer/disposal ownership (#931/#933/#964); collector/source parity and ordinary
maintainability/API gates; final canonical package/Trim/NativeAOT/multi-transport checks;
valid performance evidence. Keep #775 and #805 open and do not merge this profile as a
shipping feature.

## Development checks

Local SDK 10.0.302 (only installed feature band): profile Client/Server/probe Release builds
and ordinary default UnitTests Release build completed with zero warnings/errors. The
local global.json feature-band override was restored; CI uses the repository pin unchanged.
The project-reference guard passed (10 projects / 15 active references).

The original admitted-dispatch source guard reported two differences because it compared
literal conditional branches rather than the selected compilation. Its default-path check
now selects only the explicit experimental symbol, preserves exact string/comment tokens,
and still compares the full original resource skeleton. Unknown directives fail; literal
and conditional controls plus explicit queued-profile identity checks pass (5 tests).
No default resource assertion or budget was removed. Full canonical/maintainability gates
remain integration obligations, not implied by these basic checks.
