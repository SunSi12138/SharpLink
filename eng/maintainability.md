# Maintainability report

Generate the local source/test maintainability inventory from the repository root:

```bash
bash eng/report-maintainability.sh
```

The command writes deterministic outputs to `artifacts/maintainability/report.json` and `artifacts/maintainability/report.md`. The generated files are local artifacts and are ignored by Git.

The report scans C# files under `src/` and `test/` separately and does not follow symbolic links or other reparse-point entries. It records physical file LOC, method-like executable-body count, maximum executable-body LOC, maximum cyclomatic-complexity estimate, and distinct non-global `using` targets as a lightweight coupling proxy. Methods, local functions, lambdas, and anonymous methods are measured as independent executable bodies: decision points inside a nested executable body are excluded from the parent and attributed to that nested body instead. Anonymous executable bodies use stable source-position names such as `<lambda>@line:column`. The machine-readable JSON contains every executable body at or above 80 LOC and every executable body at or above complexity 15; the Markdown report shows the top 25 entries for each hotspot list. The JSON schema and Markdown tables use stable ordering, contain no timestamps, and write LF line endings on every platform.

The checked-in `eng/maintainability/dev-baseline.md` file is the initial human-readable evidence snapshot for issue #350. Its source ref is the `dev` commit recorded at the top of the report. The complete machine-readable snapshot remains reproducible as `report.json`; it is intentionally not checked in so this inventory PR stays independently reviewable.

To reproduce a named snapshot in another output directory:

```bash
SHARPLINK_MAINTAINABILITY_SOURCE_REF=<commit-sha> bash eng/report-maintainability.sh <output-directory>
```

When `SHARPLINK_MAINTAINABILITY_SOURCE_REF` is set, the entry wrapper must itself match committed `HEAD`; a dirty wrapper is rejected before it can select a tool revision. The command then pins the maintainability wrapper/analyzer and its repository-level build inputs to their latest committed tool revision, re-runs the wrapper from a detached tool worktree, verifies that the executing worktree `HEAD` and actual tool inputs match that pinned revision, materializes the requested source commit in a second detached worktree, and scans that isolated source tree. The report records both `sourceRef` and `toolRef`. Uncommitted `src/`/`test/`, analyzer, or tracked build-input changes therefore cannot alter a named snapshot, while uncommitted wrapper changes cause the command to fail instead of selecting a different tool revision. A later committed tool change intentionally produces a different `toolRef`. Without an explicit source ref, the command scans the current checkout and labels both refs `working-tree`.

This report is informational only. It does not fail CI or enforce a maintainability budget; regression enforcement belongs to the follow-up baseline issue.
