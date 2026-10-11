# Fixed-dump SharpLink managed reader

Diagnostic-only candidate. No production repository edits, pushes, workflow dispatches, tests, live-process attachment, or target execution. Local .NET is unavailable. The previous revision compiled in CI run 38147629312, but failed before reading fields because captured ELF version metadata was unavailable. This identity-guarded revision requires its own CI validation.

## Run on Linux x64 with .NET 10 SDK

Restore the captured, hash-matched assemblies to the paths recorded in the dump before running. Supply the matching runtime's `libmscordaccore.so`, not the analyzer SDK's DAC merely because it is convenient. Nonzero versions retain strict DAC version checking. This captured Linux dump has missing version metadata (ClrMD reports 0.0); only that exact case admits an explicit logged fallback after matching the captured and local coreclr GNU build IDs and the pinned official-runtime DAC SHA-256. Any identity mismatch fails closed. The analyzer disables symbol-server lookup; no new module substitutions are attempted.

```sh
./run-reader.sh /path/024-round-3.dmp /path/matching/libmscordaccore.so /path/managed-output
```

Build separately if preferred:

```sh
dotnet build ManagedAbiDumpReader.csproj -c Release
dotnet bin/Release/net10.0/ManagedAbiDumpReader.dll /path/024-round-3.dmp /path/matching/libmscordaccore.so > reader.jsonl 2> reader.stderr.log
```

Only the exact dump SHA-256 `904d23ea529f02e7f8845edfcfa83fec03223e774c6c9cc65dabab740a1fad95` is accepted. `Microsoft.Diagnostics.Runtime` is pinned exactly to **4.2.750101**, official Microsoft/dotnetframework NuGet owners, released October 7, 2026. It supports net10.0. This is deliberately a separate tool rather than a replacement for SharpLink or SOS.

## Output and scope

JSONL streams each finding immediately. Fixed-address objects are read first, so a later heap-walk error does not destroy those findings. Records include `object`, `value-type`, `reference`, `scalar`, `census`, `error`, `bound`, and a terminal `summary`.

The heap walk examines type identities to locate only ten fixed root types, with at most 64 objects per type and 512 emitted objects overall. It does not print the rest of the heap. Only explicitly named reference fields leading to allowed support types are traversed. Object-reference depth is four; inline struct depth eight; instance fields are capped at 160 per object or struct. Strings are never decoded. Character scalars are numeric, not text. Arrays, delegates, task continuations, environment, and heap/stack/handle roots are not traversed. Exception references identify addresses and types only, not messages or stacks.

Every primitive and reference field uses the public DataReader's checked byte-count read at ClrMD's field address. Missing bytes produce explicit errors rather than silently becoming zero/null. This matters because ClrMD's convenient `ClrInstanceField.Read<T>` and `ReadObject` return defaults on read failure. Static reads are limited to the ten named counters/signals in DynamicPluginService, keyed by that type's MethodTable and module AppDomain. No target initializer or getter runs.

Expected investigations:

- CancellationTokenSource `_state` / `_disposed`, through tokens/registrations or invocation cancellation
- CreditWaiter `Node`, `Rejection`, `Key`, `Completion._task.m_stateFlags`
- SendState `Credit`, `Completed`, `AbortException`, `Lease`
- StreamFlowController connection credit, waiters, pending state and terminal reference
- ClientConnection state and cancellation; PendingRequestTable scalar counters
- PreCreditSerializedBudget `_reservedBytes`, `_waiterCount`, terminal reference
- SendPump `_queuedBytes`, stop/fault flags, pump task
- PooledAsyncStreamDispatcher<int> wait source, completion/disposal/lease fields
- Fixed helper/credit-waiter/pump/service async objects, inline `StateMachine`, state integer, awaiter task and cancellation token
- DynamicPluginService's `_serverStreamDisposed` and six other named signal references plus three counters

Type/module records include module basename/address, MethodTable, collectibility, AssemblyLoadContext address and LoaderAllocator handle address. These are metadata/address observations, **not proof of a strong root or successful unload**. Merely inspecting a dump cannot add a strong reference to the original process. No root enumeration is performed. Counts include heap-resident objects regardless of liveness.

Exit 0 means all reads attempted within the declared scope succeeded without hitting a bound. Exit 2 means partial results (including a depth/count bound); exit 1 means fatal/partial. The shell wrapper additionally preserves raw build/runtime stdout/stderr, input hashes, exit codes and an independently checked terminal-summary result. A native DAC crash or timeout is not converted into success. Upload only these diagnostic outputs, never the dump itself or an analyzer-generated core.

## Primary sources consulted

- Official package/version/owners: https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime/4.2.750101
- API introduction: https://github.com/microsoft/clrmd/blob/main/doc/GettingStarted.md
- Read-only dump loader: https://github.com/microsoft/clrmd/blob/main/src/Microsoft.Diagnostics.Runtime/DataTarget.cs
- Explicit DAC/version validation: https://github.com/microsoft/clrmd/blob/main/src/Microsoft.Diagnostics.Runtime/ClrInfo.cs
- Field addressing and default-on-failure semantics: https://github.com/microsoft/clrmd/blob/main/src/Microsoft.Diagnostics.Runtime/ClrInstanceField.cs
- Checked memory reads: https://github.com/microsoft/clrmd/blob/main/src/Microsoft.Diagnostics.Runtime/DataReaders/IMemoryReader.cs
- Static slot semantics: https://github.com/microsoft/clrmd/blob/main/src/Microsoft.Diagnostics.Runtime/ClrStaticField.cs
- Module/collectible context: https://github.com/microsoft/clrmd/blob/main/src/Microsoft.Diagnostics.Runtime/ClrType.cs

API sources were inspected on October 11, 2026; the package pin is exact, but the public main-branch sources are not a package-source commit guarantee. The required CI compilation must verify the packaged API before any conclusion is drawn from output.
