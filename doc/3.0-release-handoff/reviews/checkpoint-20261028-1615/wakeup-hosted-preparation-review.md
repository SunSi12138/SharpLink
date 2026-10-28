# Independent hosted preparation review — run 40716132624

## Split verdict

**Linux x64: PASS for the successful, immutable preparation bundle and D/C receipts. Keep and reuse these prepared bytes; no Linux rebuild/replacement is needed.**

**ARM64: original receipt validation remains FAILED / not approved for collection. Read-only diagnosis confirms a deterministic official-archive overlay provenance-model error. Existing successful build bytes can be retained for a separately reviewed revalidation path; do not relabel the failed receipt as passed or silently rebuild.**

Current REQUEST and amendment01 still require both architecture receipts before collection. This review does not introduce a Linux-only schema, fabricate an ARM digest, or authorize any collect process.

## Binding and independent Linux checks

- Run 40716132624, attempt1; prepared remote tool commit 2b59e359bff99ec8f7d5b1de158179d6eb9fa4f0; reviewed local equivalent 48e6c2c6134387efa55a2867e31c848867c26186, tree614b0f3f6b4ab441bdd1ba223acd479270784e03.
- GitHub artifacts API independently read back both artifact IDs, exact sizes/digests, run and head SHA; both original local ZIPs independently rehashed and matched.
- Linux artifact12828492592: 66,379,790 bytes; SHA256 6a8426f4a468c41885d62d21d25bf7e59e0a6abaecc0def1c4a17d2ede70e522.
- Linux prepared.tar.gz SHA256: 38232d51c7ec14a661a91a0275f2795eda8ba6befa6b8bc51293ba52e4449d34.
- D receipt SHA256: 136aa42f2d66deb554092888a75ba055eb27ab00235af616de6419dc7f73a5e2.
- C receipt SHA256: a2b3d9ee0d3511e8892eab7ed2bebf997e32e3c66ced1eda5a8768bbd8bbfe36.
- D source f724c7aaabdeb5e02bec23f32f6b3c709860e97c; C source e5a35b9bf63d162c3077aec389d402e28bd2fb53.
- Native plan00abc7e4eec6ff0d4c197e83a01c9bef49e69d794136e11ee8b15ec5634de0e4; tool lock7610cc632c1def4ca8593b4cce9d199dc409cdb54438fb67087c620573104117.

Portable, read-only tar review checked all1,741 regular members, both strict receipt schemas, every1,091 receipt-bound file per arm, and required runtime/product/probe/generated/source closure. Each arm's507 tracked source/build-input files match exact Git object bytes; no extra non-bin/obj source files appeared. Native/hosted/workflow/copied probe/decoder files equal reviewed source. Product output copies match corresponding build outputs and contain true D/C source-commit informational versions. Key MVIDs were independently parsed from PE/CLI metadata (separate JSON).

The three official Linux archives were independently SHA512-verified using already-retained identical archives from local preparation; no archive downloads were made. All337 installed runtime files match both the complete official Core→ASP final union and the original validator's component assignment. Recorded4,467 installed SDK member identities match the official SDK archive. This checks the preparation record, not an independently repeated build or cryptographic source-to-binary attestation.

Retained host evidence is Ubuntu24.04/x64, runner image20261023.334.3; dotnet-info records SDK10.0.102 and host10.0.14. Both probe runtimeconfigs pin10.0.14/Disable. Three Release build logs and exits show0warnings/0errors. Normalized Python3.12.15 launch succeeded. This run actually records MULTILEVEL_LOOKUP0 and the sole approved Python LD path, with explicit removal; first-run unknown values are not backfilled.

## Independently established ARM diagnosis

- ARM artifact12828441906: 376,161,131 bytes; SHA256 b3648cb658d92509f2e74ff47dd34a4db3b4d81fdaac42504d069343eef108c5.
- Original failure remains: installed shared/Microsoft.NETCore.App/10.0.14/libcoreclr.dylib differs from standalone Core archive.
- Failure tar independently censused. Its three archive byte streams match the diagnostic copies and their frozen official SHA512 values.
- Archived executing driver bytes exactly match the reviewed driver: build SDK installation order SDK→Core→ASP; runtime-only installation order Core→ASP. This order was already in source before the run, not invented after seeing hashes.
- Complete set of336 installed runtime files equals the complete final Core→ASP member set and every installed hash matches its last official writer.13 shared paths differ between the two official archives; every installed conflict equals the final ASP member. Unknown/missing/extra installed members:0.
- CoreCLR installed/final-ASP SHA25687b532d37786da697c41b6af695949c1370169f82352ec8ce97cf4de27141792; standalone Core SHA256b48b82af5b338aa43d81521e77489241f7db2049b7eaa65fbbf77f0971f37705.
- SDK102 has the10.0.2 CoreCLR member, not the10.0.14 path; it cannot explain or authorize an arbitrary alternate14 member.
- ARM host logs record macOS14.8/arm64, SDK102/host14, normalized Python3.12.10 launch success, and all three builds0warnings/0errors. These are build results, not receipt/native-mechanism acceptance.

No claim is made that the differing dylibs differ only by signatures or that their executable code is identical; their actual bytes have exact official provenance under the already declared extraction order.

## Minimal repair acceptance conditions

1. Freeze ordered archive composition before validation: runtime root exactly [Core14, ASP14], with the existing exact official SHA512 lock. The final expected hash/owner for each path is determined by last writer, never by choosing whichever archive matches installed bytes.
2. Reject duplicate/ambiguous member names within an archive, invalid/traversing/link/special required entries, unknown installed files, missing required members, modified hashes, changed archive pins and undeclared order. Overlaps across the two declared archives are expected and resolved only by the fixed order.
3. Retain a deterministic final member provenance manifest (path, final component/archive digest, expected member digest); verify actual byte closure against it. Do not special-case13 mismatches or normalize/sign/modify installed native binaries.
4. Offline negatives must reject a conflict replaced with the earlier Core member even though that member is official, altered final bytes, undeclared/reversed order, missing final entries and duplicate/unknown entries. Existing Linux bundle and receipts must remain accepted unchanged.
5. Preserve original failed artifact/receipt, successful ARM product/probe/decoder binaries and original build/source stamps. Any repaired validation/tool bundle and new receipt must have distinct, explicit reviewed identities, retaining links to original artifacts. Do not silently overwrite old receipt or claim a new prepare run occurred. Reuse Linux unchanged. Collection stays behind the existing two-architecture approval/REQUEST gate.

Independent evidence: /workspace/shared/sharplink-hosted-40716132624-independent-audit.py and .json; /workspace/shared/sharplink-hosted-40716132624-independent-modules.json. Review ran no CLR, decoder, probe, CI, prepare or collect and did not modify original artifacts/tool sources.
