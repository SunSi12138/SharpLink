# First-binding awaiter: independent compiled-path review

Run: https://github.com/SunSi12138/SharpLink/actions/runs/37846827122  
Helper: `48aecc599cb8ee91219d727b10ccdad572a776d4`  
Production: G2 `398d484fb5b8ab8adb75a74db7d577d929d2dc77`; A `4cb4ce7a13b06c3a6f3213e30215b6769ed49f90`.

**PASS for the intended field/layout mechanism and the inspected completion paths.** Each actual byte[] root replaces one 56-byte lease-valued ValueTask awaiter with a 16-byte task awaiter, reducing its managed state by 40 bytes. Matching JIT wrapper instructions and NativeAOT state-copy instructions confirm the reduction. The retained lease remains 40 bytes. This establishes neither a CPU improvement nor throughput acceptance.

There is a material code-shape tradeoff: both captured JIT MoveNext bodies become larger in code bytes, despite smaller temporary initialization/stack reserve. NativeAOT client MoveNext code also grows and its frame/zeroing do not shrink. NativeAOT generated-server MoveNext does shrink. These runtimes and bodies are kept distinct below.

## Evidence and identity

The separate integrity reviewer verified the nine official ZIPs, source/binary maps, and inner archive contents. This review uses the extracted `run-37846827122-artifacts` directory. I independently re-disassembled the selected native methods from the verified ELFs and compared their instruction addresses/bytes with the streamed archived dump, within each ELF symbol's actual extent. All 16 selected entries match. `independent-native-extraction.json` records symbols, addresses, sizes and comparison results; `*-fresh.txt` retains the independent objdump output. The G2 server-box alias was followed into its emitted shared implementation, not treated as a missing body. Full native xz streams were processed line by line; no workload binary was executed.

Native ELF hashes:

- G2: `8e18ac39a35131476976372e0d73c90f9778506c135b7c25281dc098af881d59`
- A: `dde3dbd0f3a66764fd9ef7b4e09f1dc819235e15080e5b6678c9d22dcb2976af`

Managed layout/JIT runtime is x64 .NET **10.0.12**, SDK **10.0.112**, with the archived runtime hashes and R2R0/tiering1/PGO1/QuickJitForLoops1 settings. Both matched caller pairs, wrappers and MoveNext, are ordinary **Tier1/Synthesized PGO**, not OSR or Dynamic PGO. The strict earlier Dynamic-PGO question remains inconclusive. Native results are separately labeled **NativeAOT linux-x64**, with no JIT tier/profile claim.

The actual generated sources establish the byte[] route: generated Proxy lines 462–472 define the upload stream writer with `IAsyncEnumerable<byte[]>` and `IRpcCodec<byte[]>`, calling `sink.SendClientStreamAsync`; generated Stub lines 342–343 call `PumpOutboundStreamAsync` for `DownloadPayloadsAsync` with its byte[] response codec. The built-in byte[] codec remains `BlitArrayCodec<byte>`, which is unsized. The reviewed emitted roots are `System.__Canon` / `System___Canon`, not Int32 roots. Their first-send callsites reach the real unsized helpers: A JIT client block-026 line 567 and server block-013 line 681; A native client `0x555856` and server `0x55fbc4`.

## Managed fields and matching JIT initialization

The layout probe reflects the real Release state machines closed over `System.Byte[]`, uses managed `sizeof`, and records the loaded assembly hashes. Comparing every field by name/type/size finds exactly one field change per root:

| Root | G2 field | A field | State size |
| --- | --- | --- | --- |
| Client `SendClientStreamAsync<byte[]>` | `<>u__2`: configured ValueTask awaiter of Lease, 56 bytes | Configured Task awaiter of Lease, 16 bytes | 256 → 216 |
| Generated `PumpGeneratedOutboundStreamAsync<byte[]>` | `<>u__3`: configured ValueTask awaiter of Lease, 56 bytes | Non-generic configured Task awaiter, 16 bytes | 304 → 264 |

All other field names/types/sizes match, including the actual 40-byte lease and the generated pump's existing `sendTask` field. No new hoisted `send` ValueTask field compensates for the removed large awaiter. The original 80-byte expansion versus shipping dev is therefore only partly addressed: 40 bytes remain in each root relative to the earlier dev layout.

The matching JIT wrappers positively zero the corresponding smaller state region:

- Client G2 block-025: `[rbp-0x118, rbp-0x18)` = 256 unique bytes; A block-025: `[rbp-0xF0, rbp-0x18)` = 216. G2 emits overlapping vector zero stores, writing 272 bytes over that 256-byte union; A writes 216 bytes. This distinction avoids confusing store traffic with structure size.
- Server G2 block-012: `[rbp-0x148, rbp-0x18)` = 304 bytes; A block-012: `[rbp-0x120, rbp-0x18)` = 264 bytes.

The current run uses wider emitted vector stores than the earlier diagnostic; no instruction-count comparison with that older capture is substituted for these matched pairs.

## MoveNext: frame and code tradeoffs

Counts below are obtained from the entry zero stores/loop bounds and actual native ELF symbol sizes, not inferred from stack reserve or rounded next-symbol addresses. `layout-and-code-summary.json` records the full table.

| Runtime / root | Cleared temporary locals, G2 → A | Stack reserve, G2 → A | Code bytes, G2 → A |
| --- | --- | --- | --- |
| JIT client, TCP Client100x16 | 600 → 560 | 920 → 888 | 7,970 → 8,082 |
| JIT generated server, SHM Server1x16 | 1,088 → 952 | 1,256 → 1,112 | 11,947 → 12,262 |
| NativeAOT client canonical root | 568 → 568 | 792 → 792 | 4,734 → 4,840 |
| NativeAOT generated server canonical root | 888 → 752 | 1,112 → 984 | 7,362 → 7,131 |

JIT zero regions: client G2 `[rbp-0x290,rbp-0x38)`, A `[rbp-0x268,rbp-0x38)`; server G2 `[rbp-0x480,rbp-0x40)`, A `[rbp-0x3F8,rbp-0x40)`. Native client clears `[rbp-0x270,rbp-0x38)` in both arms; native server clears G2 `[rbp-0x3B8,rbp-0x40)` and A `[rbp-0x330,rbp-0x40)`.

These clears execute per MoveNext entry/resumption, not automatically once for every stream item. The source still allows many synchronous iterations inside an entry. Stack reserve and code size are not heap allocations, executed instruction counts, cache misses, CPU shares or timing. The captured pair does not show a larger A MoveNext frame; it does show larger JIT code and unchanged native client frame/clearing.

## JIT first-result, task and deadline paths

Client A `jit-A/tcp-Client100x16/methods/block-026.txt`:

- The real unsized first-send returns a lease-valued ValueTask. IG77 (`0x7E3`) checks its object field. A direct result follows IG79–IG81 without an AsTask call, copies the lease, and sets bound at the end of IG81.
- Task-backed values enter IG187 (`0x13A8`), test task completion flags, and use the direct successful-result branch only for success. Non-success/incomplete values reach the single AsTask call in IG194 (`0x1478`).
- Pending task await stores only its reference/options at state offsets `0xB8`/`0xC0` in IG195, then uses the task-based AwaitUnsafeOnCompleted at IG201. Resume/completed-task paths retain `TaskAwaiter.HandleNonSuccessAndDebuggerNotification` before accessing the lease result. The binding flag remains after successful result handling, not merely after task completion.
- Generic ValueTask source-status/result fallback instructions remain in the body. Their presence is not evidence that the production first-send helpers return an IValueTaskSource. The source return graph and the separately passed task-identity tests are the evidence for ordinary Task backing.

Generated A `jit-A/sharedmemory-Server1x16/methods/block-013.txt`:

- IG89–IG93 (`0x99D–0xA00`) handles direct success, copies the actual lease into state offset `0xA0`, then sets bound at offset `0x64`.
- Non-success/incomplete conversion occurs once at IG210 (`0x19A3`); the resulting Task is retained in the existing field at offset `0x48`. The code then checks deadline HasValue and provider, as the source requires.
- No-deadline handling at IG211–IG220 uses the 16-byte non-generic task awaiter at state offsets `0xE8`/`0xF0`; `HandleNonSuccessAndDebuggerNotification` precedes the cast/result retrieval in IG221. Lease retrieval occurs only after successful task completion.
- Deadline handling at IG222 calls the same `SharpLinkTimer.WaitAsync` with send task, captured deadline, provider and lifetime token. IG237 (`0x1DFA`) branches to result retrieval only for a true timer result. False sets `deadlineWon`, calls `TryCancelGeneratedLifetime`, calls `ObserveAbandonedGeneratedSendAsync`, constructs deadline-exceeded, and throws. This preserves the deadline arbitration/abandonment path for already-faulted/canceled as well as pending tasks; it does not bypass it by immediately throwing the send task's failure.
- Server1x16 only exercises one item per operation. Later-item code remains in the root, and source diff establishes that it is unchanged, but this cell is not evidence of execution of a later-item retained branch.

Source comparison supplies the once-consumption boundary: either one direct Result, or one AsTask followed only by uses of the returned Task. Reobserving an ordinary Task after a successful await/timer is permitted; it does not reconsume the original ValueTask. Completed faults/cancellation go through task non-success handling, while the generated deadline-present case retains timer precedence. Enumerator cleanup, cancellation ownership, unsent-credit ownership and first-send helper implementations are unchanged source. The separate safety evidence, rather than disassembly alone, establishes their tested outcomes.

## NativeAOT corroboration and limits

Canonical client wrappers are G2 `0x689dd0`, A `0x689e70`; generated wrappers G2 `0x68be90`, A `0x68bf30`. Client wrapper unique zero regions are 256 → 216 bytes. Generated wrapper total zero regions are 336 → 296 bytes, containing 32 bytes of return temporaries in addition to state payload 304 → 264. These must not be reported as 336/296-byte state machines.

The actual box-copy payload confirms NativeAOT state reduction independently of managed sizeof:

- Client `GetStateMachineBox`: G2 `0x683d65`/`0x683dc1` set copy length `0x100` (256); A `0x683cc5`/`0x683d21` set `0xD8` (216), then call BulkMoveWithWriteBarrier.
- G2 generated-server factory symbol at `0x1f2e0` jumps to shared implementation `0x674ee0`; its copy lengths at `0x674f65`/`0x674fc1` are `0x130` (304). A's actual factory at `0x684ba0` uses `0x108` (264) at `0x684c25`/`0x684c81`.
- Old native client resume copies/clears 56 bytes at state offsets `0xB8..0xEF`; A stores/restores only task reference/options at `0xB8/0xC0`. Old server uses 56 bytes at `0xE8..0x11F`; A uses task reference/options at `0xE8/0xF0`. This positively maps the removed awaiter rather than relying on symbol absence.

Native client A first-send call `0x555856` returns to direct-success inspection at `0x555952`. A direct-result object-null branch bypasses conversion; the existing-Task conversion path at `0x5559DF–0x555A04` obtains that same Task and joins task awaiting at `0x555A5B`, storing the smaller awaiter at `0x555A98`. Non-success handling remains before result access; lease storage and bound update remain afterward.

Native generated A first-send call `0x55fbc4` reaches the direct-success guard `0x55fccd`. Existing-Task conversion at `0x55fd63–0x55fd8e` preserves the Task. Deadline/provider checks at `0x55fdfb` select either timer wait (`0x55fe7c`) or no-deadline task wait (`0x55ff38`). The false-timer path `0x56083c–0x560862` marks deadlineWon, cancels lifetime, observes the abandoned send and throws deadline-exceeded. The actual result is stored before bound becomes true at `0x560063`.

Native generic AsTask expansion still contains an IValueTaskSource adapter path and a direct-result-to-Task allocation fallback (client allocation at `0x555a2e`, generated at `0x55fdc1`). **Those instructions were not removed.** For the supported helper return graph, object-null direct successes are diverted before conversion, and non-null ordinary Tasks take the reuse branch. Neither the code's fallback presence nor its bypass proves whole-program allocation neutrality; task identity/allocation tests and the relevant allocation gates are separate evidence. This review makes no claim that every allocation helper disappeared.

## Decision boundary

The intended **40-byte first-binding awaiter reduction is compiled and verified** in the actual managed byte[] fields, matching JIT wrapper initialization and actual canonical NativeAOT state payload copies. The JIT/native first-result and deadline control flow agrees with the narrowly reviewed source change. There is no hidden replacement large ValueTask field, but first-result stack temporaries/copies and the actual lease remain.

No performance screen or new measurement was run by this reviewer. The four diagnostic capture processes do not establish timing acceptance or a percentage of prior RPC loss recovered. This review does not establish complete Dynamic-PGO caller coverage, codegen for every workload cell, allocation totals of suspended operations, or CPU significance of entry clearing and code growth. Those limits remain even with the separately passed safety/allocation gates. Promotion/performance decisions require their own authorized evidence.
