# R NativeAOT retained-send mechanism review

## Result

**PASS for the bounded compiled mechanism.** In the actual canonical byte-array client and generated-server roots, R preserves G2's 40-byte pre-callback ownership snapshot and replaces the second 40-byte outgoing value copy with an address of that snapshot. The inner sender uses the address directly for admission; it does not create a compensating unconditional whole-lease copy. Deferred admission and exception refunds retain their required value copies. Root state payload, temporary clearing and stack reserve do not grow.

This is **NativeAOT compiled-only evidence**. Neither ELF was executed by this preflight or this reviewer. It establishes no Native runtime path coverage, timing, allocation totals, throughput recovery, JIT tier/profile behavior or performance acceptance. The parent independently reviews the actual JIT captures; its findings are separate.

## Provenance and independent checks

Run [37855801421](https://github.com/SunSi12138/SharpLink/actions/runs/37855801421), helper commit `7b84cd6a60dc9f8d8e7dfa8e29dc82c4adc2d535`. Native production trees are G2 `398d484fb5b8ab8adb75a74db7d577d929d2dc77` and R `df4383c7d00ee4a31c129ae42958e013f4ff5385`.

Inspection started only after the integrity reviewer reported verified official ZIP SHA-256/size/CRC, inner file hashes, complete native publish archive checks, source archives and decompressed disassembly streams. Independently recomputed ELF hashes:

| Arm | ELF path | SHA-256 |
| --- | --- | --- |
| G2 | g2-readonly-lease-preflight-review/run-37855801421-native-elf/G2/SharpLink.Benchmarks | 0c21431a2db4bc7d1c0c8497c7a4124b8f594567dc3473e57211f8d4d8e01333 |
| R | g2-readonly-lease-preflight-review/run-37855801421-native-elf/R/SharpLink.Benchmarks | 3936ef2bc54768187a1667e3f18a193a270d3efd1e8814e1f81c1f67a66c0e24 |

Official artifact ZIP identities, already verified by the integrity reviewer:

| Package | Artifact ID | ZIP SHA-256 |
| --- | ---: | --- |
| native-G2-elf | 11584423027 | ebe8ffae37b9d643021f0edadedfa79c2980596c36cb10a040a1ea287ee09df0 |
| native-R-elf | 11584083590 | 74065562d51d674ac1a57ccbcc454949301b4ce5046bf19c2cbbfef314e353f8 |
| native-G2-code | 11584063717 | ca167bb493f9bd371effb2b69da1175f007a91a3d2ff5aaf9631626dd1cba1f1 |
| native-R-code | 11584423033 | 7f5fcafab223ee0b579f8e52729a6af0ff385c1e27624e043f974d5e76ebf6bd |
| native-index | 11584727091 | 55fdd231acdc1a179b833485b1b1d91d422e12c3b6a41a7e9011eee338f39096 |

Native code/source packages are under `g2-readonly-lease-preflight-review/run-37855801421-artifacts/native-{G2,R}-code`; native-index is adjacent. Full package records are `run-37855801421-package-integrity.json` and `run-37855801421-native-byte-integrity.json`.

Independently compared all 1,375 tracked src/test file hashes in each native source snapshot with the frozen author inventories. Before adaptation, after adaptation and after each build match exactly. The common minimal RPC host is byte-identical between arms; its only adaptation is generated JSON metadata outside measurement and explicit imports. Measured-loop SHA-256 is `e279118c41aec8e1dc2c9e6d00896a0f5644ce29535b6f94d4bd03a35a7bb658`. Compiler environment records no tuning overrides. This is the pristine production pair, not safety-overlay or experimental binaries.

`method-map.json` records independently resolved nm symbol sizes, addresses, ELF identity, exact objdump command and excerpt hashes. `independent-native-checks.json` records source checks, helper instruction comparisons and call-sequence comparisons. Raw extracts remain intact; no symbol absence or normalized excerpt is substituted for an inspected body.

## Actual byte-array source and canonical mapping

The retained generated Proxy source declares the upload writer's `IAsyncEnumerable<byte[]>` and `IRpcCodec<byte[]>` fields at lines 462–469 and calls `sink.SendClientStreamAsync` at line 472. Generated Stub lines 342–343 call `DownloadPayloadsAsync` and pass its byte-array stream and response codec to `PumpOutboundStreamAsync`. BenchmarkContracts lines 42/44 and BenchmarkService lines 81/89 retain the corresponding byte[] signatures. Exact generated source excerpts/hashes are saved as `{G2,R}-byte-array-generated-source.json`.

The built-in byte[] codec is unsized BlitArrayCodec<byte>; no host replacement changes that route. The reviewed symbols use `System___Canon`, not the separate Int32 instantiations. Sized fallback instructions remain in canonical roots, but their presence is not a claim that the byte[] path uses them.

| Body | G2 address / code bytes | R address / code bytes |
| --- | --- | --- |
| Client SendClientStreamAsync canonical MoveNext | 0x554F40 / 4,734 | 0x554EE0 / 4,696 |
| Generated PumpGeneratedOutboundStreamAsync canonical MoveNext | 0x55F0A0 / 7,362 | 0x55F020 / 7,324 |
| SendUnsizedStreamChunkResolvedAsync canonical sender | 0x68C6A0 / 1,201 | 0x68C5E0 / 1,207 |
| AwaitPreCreditBudgetAndRetainedFlowCreditAsync starter | 0x135080 / 305 | 0x135080 / 305 |
| Its MoveNext | 0x154A70 / 1,272 | 0x154A70 / 1,272 |

Both outer resolved dispatch wrappers are inlined into the roots. They are followed through their emitted snapshot, rejection and sizing sequence, not assumed removed because an outlined symbol is absent. The same canonical compiled roots support the requested client/Server100 source scenarios, but no Native scenario was executed here. A one-item server workload would not exercise this later-item branch.

## Client ownership and outgoing-copy proof

In both arms the hoisted lease occupies root offsets `0x80..0xA7`; its retained-send snapshot is local `[rbp-0x248..rbp-0x221]`.

- G2 copies state→snapshot at `0x555378–0x55539D`. Its second copy at `0x55543E–0x55546E` moves two references and three qwords into `[rsp+8..rsp+0x2F]`; the unsized call is `0x5554CE`.
- R copies the same 40 bytes state→snapshot at `0x555318–0x55533D`. That occurs before the outer client deadline check at `0x555395` and optional sizing callback at `0x5553C8`.
- R then emits `lea -0x248(%rbp), %rsi` at `0x5553DE` and stores that pointer into `[rsp+8]` at `0x5553E5`, before the unsized call at `0x555448`.

The passed address is the owned stack snapshot, not `root+0x80`. The first copy retains its size and callback timing. The second whole-copy is gone on this branch, replaced by a pointer. The sized alternative still copies the snapshot by value; that is intentional and outside the byte[] unsized path.

## Generated-server ownership and outgoing-copy proof

In both arms the hoisted lease occupies root offsets `0xA0..0xC7`; its retained-send snapshot is local `[rbp-0x390..rbp-0x369]`.

- G2 copies state→snapshot at `0x5600DF–0x560107`, then snapshot→outgoing argument at `0x560165–0x560195`; unsized call `0x5601F3`.
- R preserves state→snapshot at `0x56005F–0x560087`, before the optional sizing callback at `0x5600C9`.
- R emits `lea -0x390(%rbp), %rsi` at `0x5600EC`, stores the pointer to `[rsp+8]` at `0x5600F3`, and calls the inner sender at `0x56014D`.

Again, no mutable root-field alias replaces the original snapshot. The first whole-copy remains; only the second outgoing value copy is replaced.

At either R callsite the lease pointer takes eight bytes instead of a 40-byte value, so later outgoing arguments move down by 32 bytes. RpcDeadline remains a separate 40-byte by-value argument, now `[rsp+0x10..0x37]`. Its surviving vector stores are not lease copies.

## Inner sender: pointer consumption and no compensation

R's incoming lease pointer is `[rbp+0x18]`. Client/generated rejection and serializer work run with that pointer still referring to the caller's owned snapshot. R loads it into RSI at `0x68C802`, then calls controller `TryAcquireSendCredit` at `0x68C808`; G2 instead takes the address of its incoming value at `0x68C8BF` and calls the same controller body at `0x68C8C8`.

On successful synchronous admission, R branches to the same acquired-credit/rejection/publication sequence and calls `SendPacket` at `0x68C96E`. No whole-lease copy occurs on that path. The inner sender's only whole-lease copies are the explicitly conditional slow handoff and exception refund below. Its early/middle vector transfers read RpcDeadline at `rbp+0x20`, not the lease.

Controller admission at `0x13F710`, ValidateSendLease at `0x142610`, TryValidateSendLease at `0x142650`, and resolved async admission at `0x13F810` are byte-identical between the new G2/R ELFs. The validator reads state at snapshot+8, owner at snapshot+0, and generation at snapshot+0x10, retaining type/owner/Attached/generation checks under the controller gate. Native standalone out-reference transport remains; R does not contain V's validator change.

## Deferred and refund ownership is still explicit

R's failed-fast-admission branch reads the snapshot pointer at `0x68C879`, then copies all 40 bytes into slow-call `[rsp+0x48..0x6F]` at `0x68C87D–0x68C8A9`. It calls `AwaitPreCreditBudgetAndRetainedFlowCreditAsync` at `0x68C8DF`.

The starter copies incoming lease `[rbp+0x58..0x7F]` into its owned state local `[rbp-0x78..rbp-0x51]` at `0x135135–0x135149`, before passing the state to AsyncMethodBuilderCore.Start at `0x13516D`. Its future MoveNext derives the lease address from its own state+0x50 at `0x154C56`. No pointer to the synchronous caller's snapshot is retained across its return.

If controller flow credit must itself queue, unchanged resolved async admission copies its owned lease into the contended call at `0x13F8FC–0x13F926`, then calls `AcquireResolvedContendedSendCreditAsync` at `0x13F932`. That method captures request ID, stream ID, state and generation in the existing waiter. Its waiter/task allocations are pre-existing, not eliminated or added by R.

R's synchronous publication-failure funclet loads the original snapshot pointer at `0x68CA13`, copies all 40 bytes at `0x68CA17–0x68CA41`, and calls unchanged `ReturnUnsentStreamCredit` at `0x68CA55`. Deferred MoveNext likewise refunds from its own state+0x50 at `0x154EB2–0x154EE0`, then calls the same session refund at `0x154EF1`. The downstream validator/refund still checks captured identity rather than re-resolving a current generation.

Deferred starter/root, contended helper and controller refund retain the same instruction sequence except recorded direct-call address relocations to the same named targets. Session refund, admission and both validators are byte-identical. Thus the conditional copies were preserved; the removed caller copy was not silently shifted to unconditional work.

## Size, storage and allocation-site checks

- Client root stack reserve remains `0x318` = 792 bytes; temporary zeroed region remains `[rbp-0x270, rbp-0x38)` = 568 bytes.
- Generated root stack reserve remains `0x458` = 1,112 bytes; temporary zeroed region remains `[rbp-0x3B8, rbp-0x40)` = 888 bytes.
- Inner sender stack reserve remains `0xF8` = 248 bytes.
- Client wrapper still initializes a 256-byte state; generated wrapper still has 304-byte state plus 32-byte return temporaries. No state-size reduction is claimed.
- Actual native box-copy payload remains `0x100` = 256 bytes for client and `0x130` = 304 for generated server in both arms. The generated box factory is a thunk; this review followed it to its shared implementation rather than measuring the thunk as the factory.
- Root code shrinks by 38 bytes each; the inner sender grows by six bytes from pointer loading in its conditional slow/refund paths. These are static sizes, not speed metrics.

The client roots have 95 callsites each, generated roots 156 each, and inner senders 29 each. Named/indirect call sequences match; the generated root's existing local disposal funclet moves from offset `0x1C6A` to `0x1C44`, explicitly recorded rather than treated as a new target. No new allocation callsite, task construction, boxing path or helper is introduced in the reviewed modified chain. Existing error/slow-path allocations and arbitrary callback behavior remain. This is a bounded emitted-site observation, not proof of whole-RPC allocation neutrality; the allocation gates are separate evidence.

## Decision boundary

The hypothesized second-copy removal is real in both source-backed canonical compiled roots and the actual callee. The first value snapshot, callback ordering, deferred ownership, validation and refund mechanisms remain intact. There is no remaining Native compiled-mechanism blocker for this narrow R candidate.

Safety execution, JIT execution/profile coverage, allocation gates and any subsequent timing screen must retain their own evidence and acceptance requirements. This report neither executes NativeAOT nor turns a 40-byte-copy reduction into a projected RPC performance gain.
