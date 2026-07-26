# SharpLink 0.8.1 Deep Audit

Chinese: [`../audit-0.8.1.md`](../audit-0.8.1.md)

Baseline: 0.8.0 commit `7a99fc6`. Five P2-or-higher changes were evidenced before repair: mutable authorization scopes; mutable endpoint and generated-manifest arrays behind read-only interfaces; synchronous/leaking built-in resolver disposal; generated semantic request values bypassing validating Codecs; and an intermediate allocation/copy in native `List<T>` decoding.

The repaired suite freezes authorization state, wraps every generated collection level, shares an idempotent async resolver-disposal task, preserves canonical 1-byte Boolean framing while validating both decoders, routes semantic values through built-in Codecs, and writes list payloads directly into List-owned storage. See [`../performance-0.8.1.md`](../performance-0.8.1.md) and [`../migration-0.8.1.md`](../migration-0.8.1.md).
