# Phase evidence publication — PASS

- Published only `codex/3-release-evidence`, using expected SHA `f55d291f5fa1ee4c5807e4b448718e60405fd960`, `force:false`.
- Remote commit `f90e9adf565cb3e96adbaa9d150d7f03712a9a80`; sole parent f55d291; exact independently reviewed tree `0fc4cbe0271446137590917733283427f37c4ee8`, identical to local e61a42c. Remote metadata changes commit SHA, not source/evidence bytes.
- 36 uploaded blob return identities match reviewed Git objects; independent JS base64 decode/Git SHA1 checks also match all 36 before tree/ref. Those JS checks were post-upload, not pre-upload, because the batch was already in flight when that additional requirement arrived.
- API commit/ref readback and public Git fetch PASS. Public fetch emitted `error: Could not read 06fabb3574942a83651a624d14bfee6a11ccfbd4` during negotiation but original operation ultimately exited 0 and fetched exact new SHA; no retry.
- Remote full tree diff empty; all 13 earlier archive index entries and 24 stored archive/part paths unchanged.
- All 30 fetched parts pass exact length/SHA256/order; reassembled 93,087,850 bytes, SHA256 `8c1cd1ff0f165eba1d0912d5fbe75604030c8ce895cf359f15b38b260dab6454`.
- Earlier first part upload failed identity: exec output truncated a 4,194,304-character base64 string to 1,048,607 with an omitted-bytes marker. API created isolated blob c404352a3841005e68926bd881c752367bbdc991. No tree/ref used it; it was not deleted. Corrective transport reads immutable Git objects in 393,216-byte chunks, validates each exact base64 length/grammar, and checks returned full Git SHA. No original archive changed. Full record in transport-failure.txt and scripts.
- No performance run, source branch, PR, merge, release or tag changed.

Permanent links:
- https://github.com/SunSi12138/SharpLink/blob/f90e9adf565cb3e96adbaa9d150d7f03712a9a80/README.md#phase-rewrite607-actual-local--hosted-measurements-2026-10-28
- https://github.com/SunSi12138/SharpLink/blob/f90e9adf565cb3e96adbaa9d150d7f03712a9a80/archives/phase-rewrite-performance-20261028.parts/manifest.json
- https://github.com/SunSi12138/SharpLink/blob/f90e9adf565cb3e96adbaa9d150d7f03712a9a80/phase-rewrite-performance-20261028-files.json

Local receipts: api-publication-receipt.json, uploaded.json, js-transport-verification.json, remote-verification.json, verify_remote.py, transport-functions.js. Original failed transport record retained. Published evidence integrity is not performance Go; phase candidate remains not promoted.
