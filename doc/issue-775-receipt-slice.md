# #775 receipt slice after the dynamic lifecycle gate

Start condition met: fc9d7b9c/run43387946751 passed Windows/Linux × TCP/SharedMemory,
each cell99 previous controls +67 lifecycle cases. No shipping merge or performance result.

## Criteria fixed before the first receipt Action

Keep both prior inventories and budgets unchanged. Add a separate **49-case receipt
inventory**, tracked in eng/775-slice/receipt-expected-cases.json, with a120s process watchdog.
All RPC, GC and lifecycle execution remains GitHub-hosted only. Local work is development,
basic build and source guards; no local functional or performance execution.

- A real control RPC replaces H1 with H2 and returns a scalar receipt. A distinct, already
  connected data Session still observes H1 because the asynchronous broadcast is held.
  Its initial mandatory handshake has verified server instance/registry epoch. A local H2
  factory prepares a real Bind and both generated/manual ordinary RPCs reach R2 without
  waiting for that broadcast. The ordinary no-receipt path still refuses stale compatibility.
- Full immutable receipt + requirement participate in site and in-flight/completed Bind keys.
  Identical demand coalesces; Compatible, MinimumVersion, ExactRegistration and distinct
  original receipts do not silently share a weaker constraint. Service and Prepare retain
  exactly the same site. Scalar receipts must contain no Type/Assembly/delegate reference.
- Wrong local contract/hash or missing/unknown strict requirement fails synchronously;
  wrong server/epoch fails explicit Prepare before a Bind is sent. Direct wire controls
  independently test server-side peer/epoch/RID validation. Refusal performs zero business
  activation and returns operation reservations. All errors use structured codes.
- MinimumVersion compares R's publication generation. A separate unrelated module advances
  the global generation but cannot satisfy a forged future minimum for R. Same-hash Replace
  never retargets an old token; Exact cannot downgrade, Minimum can obtain a new confirmation.
- Retire R before its held exact Ack arrives, and separately between Ack commit and delayed
  site publication. Neither boundary may resurrect readiness; old ALC must collect with
  live owners and the same ten GC cycles. Recovery needs a fresh legitimate Bind, not an
  automatic replay. Constraint/header truncation/unknown enum cases fail closed.
- Any unexpected assertion/build/protocol/GC failure stops that subject and blocks completion
  of this slice. Retain the failed run and change a measured cause before a new subject.
  Do not loosen inventories, deadlines or ALC checks to obtain green. No performance claim
  follows from functional watchdogs. No PR/final integration acceptance is authorized here.

## Private implementation boundary

MagicB9 / ABIsharplink-experimental-775-group-receipt-v3 isolate this profile. The mandatory
ContractManifest header adds32 bytes for server/epoch, bound immutably to each physical
Session. Bind is40 bytes without receipt or112 with receipt; the ordinary Request prefix
remains16 bytes. These are private experiment encodings, not allocated production grammar.

GetRegistrationReceipt(exact Assembly, contract) captures only the current owner under the
registry gate. It is a profile-only public cold capture API, not the final Register/Replace
return-result ABI; concurrent supersession fails rather than returning the wrong owner's
receipt. S/epoch eligibility never replaces server-side constraint checks or request admission.

The cold site publisher also rechecks confirmed scalar tokens under the same site gate as
invalidation. Merely committing an Ack in the Session state cannot revive a retired route
when its site continuation was delayed. The added deterministic control exercises that gap.

## Results and remaining obligations

Pending hosted validation. Keep #775/#805 open. Full five-family method-facts ABI and
streaming ownership, dynamic local generations, final canonical API/package/Trim/NativeAOT,
ordinary Windows Stop failure and valid performance acceptance remain separate obligations.
No paused investigation is reopened; no PR or merge has been made.
