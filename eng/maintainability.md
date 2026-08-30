# Maintainability report

Generate the local source/test maintainability inventory from the repository root:

```bash
bash eng/report-maintainability.sh
```

The command writes deterministic outputs to `artifacts/maintainability/report.json` and `artifacts/maintainability/report.md`. The generated files are local artifacts and are ignored by Git.

The report scans C# files under `src/` and `test/` separately and does not follow symbolic links or other reparse-point entries. It records physical file LOC, method-like executable-body count, maximum executable-body LOC, maximum cyclomatic-complexity estimate, and distinct non-global `using` targets as a lightweight coupling proxy. Methods, local functions, lambdas, and anonymous methods are measured as independent executable bodies: decision points inside a nested executable body are excluded from the parent and attributed to that nested body instead. Anonymous executable bodies use stable source-position names such as `<lambda>@line:column`. The machine-readable JSON contains every executable body at or above 80 LOC and every executable body at or above complexity 15; the Markdown report shows the top 25 entries for each hotspot list. The JSON schema and Markdown tables use stable ordering, contain no timestamps, and write LF line endings on every platform.

The checked-in `eng/maintainability/dev-baseline.md` file is the initial human-readable evidence snapshot for issue #350. Its source ref is the `dev` commit recorded at the top of the report. The complete machine-readable snapshot remains reproducible as `report.json`; it is intentionally not checked in so the inventory change stays independently reviewable.

## Debt gate

Run the same maintainability gate used by CI from the repository root:

```bash
bash eng/check-maintainability.sh
```

The gate first generates the normal inventory, then checks file LOC against `eng/maintainability/baseline.json`. Production and test code intentionally use separate limits:

- `source`: files at or below 800 LOC pass without an allowance.
- `test`: files at or below 1000 LOC pass without an allowance. Tests have a looser limit because integration scenarios, fixtures, and evidence runners commonly aggregate more setup and assertions than production units.

A file is oversized only when its LOC is strictly greater than its domain limit. Oversized files already present when issue #351 enforcement is introduced are listed explicitly in `baseline.json` with a `maxLoc` allowance. The allowance is a hard ceiling, not an extra margin: a baselined file may stay the same size or shrink, but it may not grow beyond its recorded `maxLoc`. A new oversized file has no allowance and fails the gate. If a baselined file disappears, or shrinks to at or below the normal domain threshold, the gate asks for the obsolete allowance to be removed so the checked-in debt list remains accurate.

There are two distinct snapshots involved in the baseline history:

- `00e2f18c6384c785d232bd59902102d3af7ad3da` is the original issue #350 evidence snapshot recorded in `eng/maintainability/dev-baseline.md`.
- `d23304028cb133ebcede9323e5859cc88bde1901` is the current `dev` snapshot used by this PR for the issue #351 enforcement baseline in `eng/maintainability/baseline.json`.

`src/` and `test/` changed between those commits, so the enforcement baseline does not claim that the issue #350 snapshot remained unchanged until enforcement was added. Hotspots whose recorded ceiling is unchanged from the issue #350 evidence use a reason such as `Existing dev debt captured by issue #350.` Hotspots that grew before issue #351 enforcement was introduced instead record that distinction explicitly with a reason such as `Existing dev debt present when issue #351 enforcement was introduced.` In both cases, the checked-in `maxLoc` is the no-headroom ceiling enforced from the issue #351 baseline onward.

### Reviewing baseline changes

Treat `eng/maintainability/baseline.json` as reviewed debt policy, not generated output. A baseline change should be intentional and visible in the same PR that needs it.

- Prefer reducing or splitting a file instead of adding or increasing an allowance.
- New or increased allowances must include a non-empty `reason` explaining why the exception is necessary.
- Do not add headroom. Set `maxLoc` to the reviewed current size that must be tolerated.
- Remove allowances once the file is deleted or reaches the normal threshold.
- Threshold changes affect the entire domain and therefore need an explicit policy rationale in review.

Failure output identifies the domain, file, threshold or recorded allowance, and the remediation path. The verifier returns exit code 1 for debt violations and exit code 2 for malformed baseline/report configuration.

File LOC is the blocking metric in this first debt baseline. Method-size and cyclomatic-complexity hotspots remain visible in the inventory but are informational; changing their enforcement policy should be a separate reviewable change rather than silently expanding this gate.

## Named snapshots

To reproduce a named snapshot in another output directory:

```bash
SHARPLINK_MAINTAINABILITY_SOURCE_REF=<commit-sha> bash eng/report-maintainability.sh <output-directory>
```

When `SHARPLINK_MAINTAINABILITY_SOURCE_REF` is set, the entry wrapper must itself match committed `HEAD`; a dirty wrapper is rejected before it can select a tool revision. The command then pins the maintainability wrapper/analyzer and its repository-level build inputs to their latest committed tool revision, re-runs the wrapper from a detached tool worktree, verifies that the executing worktree `HEAD` and actual tool inputs match that pinned revision, materializes the requested source commit in a second detached worktree, and scans that isolated source tree. The report records both `sourceRef` and `toolRef`. Uncommitted `src/`/`test/`, analyzer, or tracked build-input changes therefore cannot alter a named snapshot, while uncommitted wrapper changes cause the command to fail instead of selecting a different tool revision. A later committed tool change intentionally produces a different `toolRef`. Without an explicit source ref, the command scans the current checkout and labels both refs `working-tree`.
