# Independent #931 PGO review reproduction

The [review](INDEPENDENT-REVIEW.md) interprets completed run40646151477; these are analysis outputs, not a new benchmark or release acceptance. The reviewer did not implement the product or original experiment tools. Original unfavorable samples remain intact.

## Inputs and rerunning the independent calculation

Use evidence commit `5a6cf6c490b43c083fb772e2c6f2663fa94bbe16` from `codex/3-release-evidence`. Verify archive SHA-256 against that commit's `index.json` before extraction. `recompute.py` uses only the Python standard library and relative paths, with no historical desktop dependency.

```sh
# REVIEW_SOURCE is this committed review directory; EVIDENCE is the evidence checkout.
# Use a new scratch directory: recompute.py writes independent-recompute.json.
mkdir review-reproduction
cp "$REVIEW_SOURCE/recompute.py" review-reproduction/
tar -xzf "$EVIDENCE/archives/931-pgo-12.tar.gz" -C review-reproduction
tar -xzf "$EVIDENCE/archives/931-fullopts-12.tar.gz" -C review-reproduction
python3 review-reproduction/recompute.py > review-reproduction/recompute.log
cmp "$REVIEW_SOURCE/independent-recompute.json" review-reproduction/independent-recompute.json
```

The script verifies both ablation profiles' binary hashes, loaded identities, all host outcomes, per-call ticks/quantiles, paired ratios and deterministic descriptive bootstrap intervals. It does not execute RPCs, rebuild binaries, or modify original evidence. It asserts the frozen source identities rather than silently accepting another candidate.

`original-ratios-recomputed.json` records the reviewer's separate recomputation of the original 48-process result. `native-verification.json` and the two `*-intercepted-tier1.asm.gz` files (decompress with `gzip -dc`; `native-extracts.json` binds the exact uncompressed bytes) are source-bound native inspection outputs; the archived original native files remain authoritative. The above script's scope is the two ablation profiles, not an automated proof of every manual conclusion in the report.

No MSBuild binlogs, credentials, unrelated host process command lines or new raw measurement archive are added here. The published evidence mirror's existing exclusions/redactions remain as documented in its index.
