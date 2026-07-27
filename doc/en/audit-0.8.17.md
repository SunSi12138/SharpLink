# SharpLink 0.8.17 Deep Audit

Chinese: [`../audit-0.8.17.md`](../audit-0.8.17.md)

Using 0.8.16 commit `0e4e1a7` as the baseline, this batch verified five P2-or-higher defects: concurrent multi-cluster assembly unregister calls duplicated one child operation and could replace its original rejection with a route-restoration conflict; TLS snapshots shared or omitted mutable chain policies and Server snapshots omitted supported RSA-padding settings; handshakes accepted required capabilities outside the supported set and unknown negotiated response bits; partition admission pools retained caller-mutable configuration; and state stores plus writer pools accepted aggregate resource configurations without hard bounds.

The complete pre-fix Unit run executed 427 cases: all 422 prior cases passed and exactly five focused probes failed. The probes directly observed two child unregister calls and a replaced exception, shared or missing TLS policies, accepted inconsistent capability sets, a source object changing live partition limits after Build, and accepted aggregate capacities beyond the new bounds. The final implementation shares one coordinator task among concurrent unregister callers, deep-clones TLS and admission configuration, validates negotiation integrity at the payload-codec boundary, and bounds aggregate stripes, initial map entries, and retained writer memory.

After the fixes, the non-incremental Release build completed with 0 warnings and 0 errors; Generator 83/83, Unit 427/427, Integration 228/228, the seven-package pack, and fresh-cache package smoke all passed. The Integration gate also confirmed that unknown request capabilities must remain negotiable and produce `Unimplemented`, so the codec rejects inconsistent request sets and unknown negotiated responses without closing future request extension bits.

See [`migration-0.8.17.md`](migration-0.8.17.md) and [`performance-0.8.17.md`](performance-0.8.17.md).
