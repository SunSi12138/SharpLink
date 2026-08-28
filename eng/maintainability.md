# Maintainability report

Generate the local source/test maintainability inventory from the repository root:

```bash
bash eng/report-maintainability.sh
```

The command writes deterministic outputs to `artifacts/maintainability/report.json` and `artifacts/maintainability/report.md`. The generated files are local artifacts and are ignored by Git.

The report scans C# files under `src/` and `test/` separately. It records physical file LOC, method count, maximum method LOC, maximum cyclomatic-complexity estimate, and distinct non-global `using` targets as a lightweight coupling proxy. The machine-readable JSON contains every method at or above 80 LOC and every method at or above complexity 15; the Markdown report shows the top 25 entries for each method hotspot list. The JSON schema and Markdown tables use stable ordering and contain no timestamps.

The checked-in `eng/maintainability/dev-baseline.md` file is the initial human-readable evidence snapshot for issue #350. Its source ref is the `dev` commit recorded at the top of the report. The complete machine-readable snapshot remains reproducible as `report.json`; it is intentionally not checked in so this inventory PR stays independently reviewable.

To reproduce a named snapshot in another output directory:

```bash
SHARPLINK_MAINTAINABILITY_SOURCE_REF=<commit-sha> bash eng/report-maintainability.sh <output-directory>
```

When `SHARPLINK_MAINTAINABILITY_SOURCE_REF` is set, the command resolves that commit and verifies that the current `src/` and `test/` contents, including untracked files, match it before generating the report. Without an explicit ref, clean `src/` and `test/` contents are attributed to `HEAD`; otherwise the report uses `working-tree` instead of incorrectly attributing modified content to a commit.

This report is informational only. It does not fail CI or enforce a maintainability budget; regression enforcement belongs to the follow-up baseline issue.
