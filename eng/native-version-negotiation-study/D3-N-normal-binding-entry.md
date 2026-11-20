# D3 N checkpoint: ordinary exact Bind and compatible replacement

An isolated N slice over P 2948de82, not a resumption of #775 N1, not Q2
code, and not a public API or wire allocation. The private minor/floor is
0x79A0. It runs the real generated Unary and plain OneWay receiver/dispatch
on the existing client/server, not a mock service or raw peer. Public
receiver/ABI names remain those of P in this checkpoint.

- An explicit cold Prepare on the chosen Ready physical Session publishes
  a matching native Bind/BindAck. The target is the exact installed
  registration and its stored publication generation, not a latest
  ContractManifest generation. The first matching Ack establishes server,
  epoch and physical identity; later grants must match on that connection.
- The 16-byte ordinary head is BindingId64 plus MethodId64 with no CID
  fallback. The original authenticated receiver selects a weak exact target
  from its own table; an active invocation keeps the original lease. Old
  table entries do not root a collectible implementation. Per-Session,
  the private checkpoint uses up to 64 allocations without slot recycling;
  it allows only one bounded explicit Prepare at a time.
- No claims of final authority negotiation, receipt transaction, Bind
  invalidation/release, late result semantics, compact reuse/ABA, complete
  method facts/ABI, producer admission, or other shapes. Current tests
  require ready authenticated TCP and SharedMemory peers only.

Windows x64, SDK 10.0.115, actual runtime 10.0.15. Discover and execute
exactly two native cases: tcp and shared-memory, once on the first source.
Each must use two physical authenticated peers, independent binds for
unrelated static B and dynamic A, genuine generated Unary/OneWay, and equal
initial per-session token numbers whose authority is distinct. Hold a
normally admitted R1 call during compatible Replace. Explicitly bind
exact R2 first on one peer and then the other without changing either
B route or reconnecting; confirm unrelated B and each new A on the actual
peers. Verify remote OneWay totals R1=5/R2=24, original invocation returns
42, old service disposes once, and the old implementation ALC collects
with both peers still serving B. Check actual source, transport, identities,
outcome and five ordered event records per transport. Retain first failure;
no timeout changes, no unusual negative case, no release or performance run.
