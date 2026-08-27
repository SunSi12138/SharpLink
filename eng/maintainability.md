# Maintainability report

Generate the local source/test maintainability inventory from the repository root:

```bash
bash eng/report-maintainability.sh
```

The command writes deterministic outputs to `artifacts/maintainability/report.json` and `artifacts/maintainability/report.md`. The generated files are local artifacts and are ignored by Git.

The report scans C# files under `src/` and `test/` separately. It records physical file LOC, method count, maximum method LOC, maximum cyclomatic-complexity estimate, and distinct non-global `using` targets as a lightweight coupling proxy. It also lists all methods at or above 80 LOC and all methods at or above complexity 15. The JSON schema and Markdown tables use stable ordering and contain no timestamps.

The checked-in `dev-baseline.json` and `dev-baseline.md` files are the initial evidence snapshot for issue #350. They are generated from the `dev` commit recorded in their `sourceRef` field. To reproduce a named snapshot in another output directory:

```bash
SHARPLINK_MAINTAINABILITY_SOURCE_REF=<commit-sha> bash eng/report-maintainability.sh <output-directory>
```

This report is informational only. It does not fail CI or enforce a maintainability budget; regression enforcement belongs to the follow-up baseline issue.
