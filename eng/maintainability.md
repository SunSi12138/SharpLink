# Maintainability report

Generate the local source/test maintainability inventory from the repository root:

```bash
bash eng/report-maintainability.sh
```

The command writes deterministic outputs to `artifacts/maintainability/report.json` and `artifacts/maintainability/report.md`. The generated files are local artifacts and are ignored by Git.

The report scans C# files under `src/` and `test/` separately and does not follow symbolic links or other reparse-point entries. It records physical file LOC, method count, maximum method LOC, maximum cyclomatic-complexity estimate, and distinct non-global `using` targets as a lightweight coupling proxy. Method complexity excludes decision points inside nested local functions, lambdas, and anonymous methods. The machine-readable JSON contains every method at or above 80 LOC and every method at or above complexity 15; the Markdown report shows the top 25 entries for each method hotspot list. The JSON schema and Markdown tables use stable ordering, contain no timestamps, and write LF line endings on every platform.

The checked-in `eng/maintainability/dev-baseline.md` file is the initial human-readable evidence snapshot for issue #350. Its source ref is the `dev` commit recorded at the top of the report. The complete machine-readable snapshot remains reproducible as `report.json`; it is intentionally not checked in so this inventory PR stays independently reviewable.

To reproduce a named snapshot in another output directory:

```bash
SHARPLINK_MAINTAINABILITY_SOURCE_REF=<commit-sha> bash eng/report-maintainability.sh <output-directory>
```

When `SHARPLINK_MAINTAINABILITY_SOURCE_REF` is set, the command resolves that commit, materializes it in a temporary detached Git worktree, disables sparse checkout there, hard-resets it to the resolved commit, and scans that isolated tree. Current working-tree changes, untracked paths, path quoting, sparse checkout, and index skip bits therefore cannot alter a named snapshot. Without an explicit ref, the command scans the current checkout and labels the report `working-tree` rather than attributing it to a commit.

This report is informational only. It does not fail CI or enforce a maintainability budget; regression enforcement belongs to the follow-up baseline issue.
