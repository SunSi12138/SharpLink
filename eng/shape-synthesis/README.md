# #728 synthesis checks

These tools audit record structure and the selected source snapshot. They do not run SharpLink code or establish performance/behavior acceptance.

Run with Python 3, from this directory:

```sh
python3 test_validator.py
python3 test_source.py
python3 validator.py ledger.json
python3 check_source.py /absolute/path/to/clean-8a13-checkout
```

The final command requires exact HEAD 8a13b12e799bb70a989eacac0c1b4cea22ab80ae, matching src tree and a clean checkout. It reads seven pinned source files and checks exact hashes/line anchors. It does not execute C#, restore packages, mutate the checkout or fetch Git objects.

`ledger.json` deliberately has zero currentPass assertions. Eighteen observed axes are static source observations, not runtime test passes. Historical/partial benchmark and semantic evidence remains separately labeled. Source/candidate commit fields are declarations; the offline ledger validator does not verify external URLs or prose truth. candidateCommit=null means the row is not declaring one single currently proposed experimental candidate. It does not mean all historical candidate identities are unknown. Known historical research heads are recorded per evidence.sourceCommit; unknown or multi-variant identities remain documented rather than fabricated.

`VALIDATION.json` is a local test summary with file hashes. It is not hosted CI evidence. `PROVENANCE.json` records separately verified GitHub ancestry and production-tree facts; equal src trees do not make two full heads/CI runs identical. `REPORT.md` contains the human conclusions, limits and source links.

No old-version compatibility gate is introduced. New 3.0 API/generated ABI entry count and maintenance costs still belong in the design assessment. No issue is closed by a structural check.
