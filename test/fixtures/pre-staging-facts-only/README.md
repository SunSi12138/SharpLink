# Historical facts-only quota source

These exact source files predate the selected shared Runtime/Server metadata staging policy.
They are preserved, not compiled or relabeled as current passing controls.

SHA-256:
- `ServerDeclaredMethodFactsQuotaTests.cs.txt`: `0a8fe27ffbc796cfb057fc94a2851f646d66679d416a2822bd8f922ac8f3e649`
- `ServerDeclaredMethodFactsTests.cs.txt`: `24ea07f09b9fe23f3e52f4c16b0d1528432373626659e14863d64a77fc46f31b`
- `ServerStaticFactsPreparationTests.cs.txt`: `e866d0c1e201714d60bd5d35cbc1c163975d5649a08685831c88a73bdf0d8c5b`

Current controls count each simultaneous framework collection separately, share method counts
only within the exact prepared registration/contract domain, and retain unserved Runtime metadata.
The former synthetic reduced facts-only budgets and 384-byte whole-server totals no longer describe
the selected policy. Current scalar-retention tests retain the resolved `RpcMethodFacts`, not a
post-disposal table. The separate paused table-reader accounting issue remains an explicit gate;
field clearing alone is not evidence of reader lifetime closure. No test execution is asserted.
