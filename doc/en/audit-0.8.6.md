# SharpLink 0.8.6 Deep Audit

Chinese: [`../audit-0.8.6.md`](../audit-0.8.6.md)

Using 0.8.5 commit `0152887` as baseline, this batch proved and fixed five P2-or-higher defects: Stream transport cleanup no longer skips later resources; RpcSession teardown completes every phase and shares its outcome; connection-scoped and server-wide service cleanup retain every failure; and asynchronous Hosted Server run failure now logs and requests Generic Host shutdown.

All five have pre-fix failing probes. Generator 83/83, Unit 369/369, Integration 228/228, Release build, and package smoke passed. See [`migration-0.8.6.md`](migration-0.8.6.md) and [`performance-0.8.6.md`](performance-0.8.6.md).
