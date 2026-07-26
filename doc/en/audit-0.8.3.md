# SharpLink 0.8.3 Deep Audit

Chinese: [`../audit-0.8.3.md`](../audit-0.8.3.md)

Starting from 0.8.2 commit `422305d`, this batch audited immutable topology, asynchronous Client/Hosting lifetime, and metadata allocation. Four P2 issues were demonstrated by failing mutation/lifecycle probes; the fifth used a three-launch allocation benchmark.

Endpoint snapshots now clone endpoints and freeze nested attributes. Client shutdown uses asynchronous cancellation without skipping cleanup on callback failure. Failed connection and HostedService startup paths preserve the primary exception together with cleanup errors. Metadata decode adopts its validated array, reducing two-entry decode from 280 to 224 B/op. A public `params ReadOnlySpan<T>` change was tested and withdrawn because .NET 10 already kept construction at 80 B/op, making the binary signature change unjustified.
