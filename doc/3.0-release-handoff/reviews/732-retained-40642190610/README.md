# Retained #732 evidence diagnosis

[Diagnosis](DIAGNOSIS.md) preserves M0 NoGo / M1 Hold; it is not a new timing run. Inputs remain the original privacy-scoped evidence branch at `5a6cf6c490b43c083fb772e2c6f2663fa94bbe16`.

For the independent calculation, verify `archives/732-hosted-three-arm-36.tar.gz` against that commit's index, then use a fresh scratch directory containing only this input archive and `analyze_retained.py`. The script locates the single extracted `completion.json` below its own directory and writes `retained-analysis.json` there.

```sh
mkdir review-732-reproduction
cp "$REVIEW_SOURCE/analyze_retained.py" review-732-reproduction/
tar -xzf "$EVIDENCE/archives/732-hosted-three-arm-36.tar.gz" -C review-732-reproduction
python3 review-732-reproduction/analyze_retained.py > review-732-reproduction/analysis.log
cmp "$REVIEW_SOURCE/retained-analysis.json" review-732-reproduction/retained-analysis.json
```

Python standard library only. This verifies retained sample counts/order and frozen paired formulas; it does not build or execute RPCs, identify a JIT tier, or attribute scheduling. A separate independently reviewed phase/runtime diagnostic is needed for those missing events.
