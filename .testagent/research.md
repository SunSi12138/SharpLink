# 0.8.30 regression-test research

## Bounded target inventory

- `SharpLinkServerHostedService` Run observation: successful completion after `StopAsync` is suppressed, but the fault path ignores `_stopRequested` and can report an expected stop failure as critical and call `StopApplication`.
- `SharpLinkServerHostedService` terminal lifecycle: a completed Stop is not a barrier for a later Start, which can publish a new server that the cached Stop task no longer owns.
- Generator task-shape emission: Proxy/Stub code decides ValueTask semantics with `ReturnType.Contains("ValueTask")`. A valid `Task<ValueTaskPayload>` is therefore emitted as though the outer type were `ValueTask<T>`, producing incorrect return/await code.
- Public endpoint addresses: `SharpLinkNamedPipeAddress` and `SharpLinkSharedMemoryAddress` still accept NUL and path separators even though the concrete transports reject the same logical names. Resolver/configuration failures are therefore still delayed for these public entry points.
- Local hosting health checks construct a new completed `Task<HealthCheckResult>` on every poll even though the three results are immutable constants; this is recurring managed allocation in a monitoring path.

## Comprehensive performance scan checklist

The .NET performance-pattern skill was run against framework `src/**/*.cs` on .NET 10. Exact signals: async-void 0; `.Result` 4 (all completed ValueTask/Task or generated text after manual review); synchronous `.Wait` 1 (shutdown gate, manually proven to pulse before waiting); Substring 8 (Generator cold path); stackalloc 60 (none inside an accumulating loop); literal IndexOf 1 and StartsWith/Contains 3 (Generator cold path); cultureless ToLower/ToUpper 0; triple Replace 0; params 2; LINQ-on-char 0; static mutable Dictionary 0 versus static FrozenDictionary 0; new List 45; new Dictionary 40; CurrentCulture comparer 0; LINQ-chain signals 142 repository-wide and 26 in Runtime/Client/Server (all reviewed; one former hot State allocation was fixed in 0.8.29); HttpClient 0; uncached JsonSerializerOptions 0; compiled/generated/constructed Regex 0/0/0; unsealed/sealed classes 19/269; ContainsKey 21 (no hot same-key double lookup retained); string.Format 0; JsonSerializer calls 3 (Generator manifest tooling); byte-array construction 10 (bounded protocol/configuration or ownership arrays).

The scanner influenced this batch only by elevating the health-check allocation and the string-based Generator task-shape bug. Cold registration LINQ, one-time collection construction, intentionally extensible public option/context types, and code-generation string slicing are rejected as non-hot or design-required rather than mechanically rewritten.

## Acceptance checklist

- Expected Run faults after hosted Stop never request application shutdown.
- Stop-before-Start and duplicate Start are rejected without publishing an unowned server.
- Task versus ValueTask emission is a semantic model field, not a substring test, and `Task<ValueTaskPayload>` uses Task code in both Proxy and Stub.
- Both public pipe-backed address constructors reject the same invalid logical-name characters as concrete transports.
- Repeated local health-check calls preserve status/description and allocate zero bytes after warm-up.
- Existing behavior and hot-path allocations remain stable.

## Deferred hypotheses

The low-level protocol writer accepts raw headers that the parser rejects, unknown custom `EndPoint` snapshots retain their source object when the type lacks a recognized clone path, and multi-cluster deferred unregister polling is not tracked in Faulted state. These require deliberate public API/lifecycle decisions and remain candidates for later audit rather than opportunistic changes in this batch.
