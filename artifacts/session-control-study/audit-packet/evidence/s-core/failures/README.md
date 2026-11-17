# Failed-attempt evidence

The canonical S results are in `evidence/s-core/F03`, `F05`, `F06`, `F07`, `F09`, `F10`, `F18`, `F18b`. The attempts below failed or were invalid **before** the canonical result and are preserved as logs, not deleted.

| Log | Attempt | Classification |
|---|---|---|
| `F03-attempt-1-sequence-no-match.log` | F03 `attempt-1` | test oracle looked up a contract id in the replacement service-module manifest incorrectly |
| `F03-attempt-2-wrong-hash-oracle.log` | F03 `attempt-2` | test compared the service-module hash with the contract hash; wrong oracle for a service-only replacement |
| `F10-attempt-1-second-client.log` | F10 `attempt-1` | second client was built after the plugin was loaded, producing a static duplicate; cleanup then masked the first failure |
| `F18-attempt-1-test-root.log` | F18 `attempt-1` | test held the old collectible `Assembly`/ALC locals, so the ALC correctly remained alive |
| `F04-attempt-1-overstrict-session-wait.log` | F04 `attempt-1` | waited 10 s for a replacement session after a changed-hash contract replacement; timeout, no client call observed. Corrected attempt records the fail-closed `Unavailable` without requiring a new session. |

The first F05 run also failed before any candidate assertion because the plugin was loaded before the client was built (`DuplicateAssembly`). Its stdout is preserved in the raw tree but it reused the same attempt directory as the later pass; it is **not** used as evidence. The canonical F05 result is `evidence/s-core/F05`.

`evidence/s-core/F18b-mixed-attempt/` contains two identical F18b runs under one attempt name; it is preserved only to show the attempt-naming error. Canonical F18b is `evidence/s-core/F18b`.
