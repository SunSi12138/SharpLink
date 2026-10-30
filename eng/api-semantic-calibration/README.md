# Fixed-artifact semantic comparison: reviewed request-only validation

This route never publishes a historical fixture or a new evidence repository.
It retains only current-run inventory and review diagnostics as a three-day
GitHub Actions temporary test report artifact. Same-repository `actions: read`
and temporary release-validation test reports were explicitly authorized on
2026-10-30. This does not authorize the earlier fixture upload;
this route must not retry, rename, encode, split, or republish that upload.
Publish reviewed tools without a request, independently review their exact
remote commit/tree, and only then add the one-file request trigger.

## Identity and input contract

* Product: `a91ce4bf48523264282928231f35fce339fb1567`, tree
  `83fe67363af2c3c815a333e93806d8a5fc6136aa`; never follow a branch dynamically.
* Frozen ledger/validator checkout: `3d76a3a8b55a37e24c62a073fe588cff3d3867be`,
  tree `1c24da3af090acd4d6cdbfb005a9e3bd95304e3a`.
* Existing artifact: repository `SunSi12138/SharpLink` (ID `1155708874`),
  run `39986953281`, artifact `12515194592`, 7,477,905 ZIP bytes.
* Run PR head `87aeaf66cba5f804b8a33d312b9878143adbc9dd` differs from the
  actual inventory source `de0d871f7a13662010fdf47d140ee885d7ba04ea`.
  The latter is separately verified in provenance, with tree
  `ccc99fbab3daa767ef1bb340bd3a73c9472d798c` and historical SDK 10.0.113.
* ZIP SHA256, reconstructed complete fixture SHA256, and required-map SHA256
  are independently pinned in `baseline.py`. These are small commitments,
  not embedded or encoded copies of the historical inventory.
* Artifact expiration last observed: 2026-11-22 16:30:09 UTC. Missing, expired,
  deleted, inaccessible or changed input fails closed. No latest-artifact
  lookup, refresh, re-upload, regenerated baseline or counts-only fallback.

The tools commit is based directly on the remote product commit above, without
carrying the former pin-performance candidate or fixture-bearing history.
The product includes reviewed correctness and restricted-maintenance changes;
real API differences are expected and require independent review. Never push
an old fixture-bearing commit and then delete the file: that transfers history.
The exact resulting remote tools commit/tree requires independent review before
request creation.

## Processing and disclosure

1. Read only the fixed GitHub artifact API, with a step-scoped read token.
   Verify metadata and actual ZIP bytes. Authorization never goes to the
   storage redirect; require HTTPS and recognized storage hostname suffixes.
2. Preflight the entire ZIP. Reject traversal, duplicates, file/directory
   collisions, links/special members, encrypted/unknown compression and
   over-limit inputs. Limits: 8 MiB ZIP, 1024 entries, 64 MiB/member,
   128 MiB total expansion, 1024 UTF-8 path bytes. Extract only the 315 files
   below `api-inventory/pr/`; never execute any artifact content.
3. Verify all 314 output hashes and frozen source/tool/control identities.
   Run the original proposal validator from its separate pinned checkout.
   Reconstruct the same canonical mapping locally. Both frozen digest anchors
   must match before use; the expected mapping remains on the ephemeral runner.
4. Generate the complete current inventory using unchanged product helpers,
   SDK 10.0.102, Linux x86_64 Release, and all ten targets. Raw driver output
   and per-target logs are included in the scoped temporary current-run report.
5. Compare all required semantic fields except `declarations` and `locations`;
   retain array order/scalar distinctions, full incident-relation records and
   assembly IVT relations. Duplicate relations fail. This intentionally does
   not claim all non-required private implementation details are identical.
6. Emit bounded comparison diagnostics only: at most 32 records, 8 KiB each,
   64 KiB aggregate `API_DIFF` bytes. Overflow emits no partial records and
   returns INCOMPLETE. Fixed-size provenance/status metadata is additional.
   The same full computed difference records are saved separately as an explicitly
   `not-reviewed` temporary report. Even when that report contains every record,
   stdout overflow remains INCOMPLETE with exit 2; no approval or gate changes.
7. Always retain only this current inventory directory, comparison report and
   current inventory driver log in one three-day artifact. Never include the
   historical ZIP, reconstructed fixture, tokens, SDK, caches or checkouts.
   Failed/partial current inventory is retained for diagnosis, not acceptance.

Source and reviewed tools are separate clean checkouts. The dormant workflow
only responds to a one-file `request.json` addition on its fixed validation
branch, with one reviewed tools parent. This draft contains only an example,
not an executable request. A semantic difference returns a non-success status
and requires review; NO_SEMANTIC_DIFF is not ledger/API/release approval.
`proposal-not-approved` and `not-reviewed` remain unchanged.

## Pure local controls

```
PYTHONDONTWRITEBYTECODE=1 python3 -B -m unittest discover \
  -s eng/api-semantic-calibration -p 'test_*.py' -v
```

These tests use synthetic inputs and mocked HTTP; they do not download an
artifact, upload evidence, start .NET/MSBuildWorkspace, or invoke GitHub CI.
The full existing historical ZIP can additionally be checked using
`safe_extract()` plus `rebuild()` in a temporary directory; its generated bytes
must not be added to Git. End-to-end current inventory execution remains a
separate authorized runner validation, not a claim made by these controls.
