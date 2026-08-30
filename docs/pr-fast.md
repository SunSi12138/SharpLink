# PR Fast gate

`PR Fast` is the bounded pull-request validation tier defined by #374 and implemented by #375. It runs for every pull request target and can also be started manually with `workflow_dispatch`.

The pull-request trigger explicitly covers `opened`, `synchronize`, `reopened`, and `edited`. `edited` ensures that a base-branch retarget (including stacked PRs later retargeted to `dev`) produces a fresh `fast` result for the new PR merge commit before the status is required. GitHub does not expose a trigger filter limited only to base edits here, so title/body edits can also cause an extra Fast run.

The stable blocking-shaped job/status is `fast`. The job has a 5-minute hard timeout and targets successful active duration of 3 minutes or less under normal GitHub-hosted runner conditions.

## Fast checks

The Fast job intentionally contains only the high-signal bounded checks assigned to Fast by #374, including the verifier regression coverage that protects the maintainability baseline gate:

1. Restore.
2. Whitespace formatting verification.
3. Maintainability verifier regression tests.
4. Maintainability debt baseline gate.
5. Release build.
6. Generated-assembly Runtime dependency guard.
7. Unit tests.
8. Generator tests.
9. Load-test component unit tests.

Debug build, integration tests, NativeAOT smoke, packaging checks, package smoke, demo/load smoke, chaos validation, and codec compatibility remain in their existing workflows until #376 moves them deliberately.

## Local equivalent

Run these commands from the repository root with the .NET 10 SDK available:

```bash
dotnet restore Sharplink.slnx
dotnet format whitespace Sharplink.slnx --no-restore --verify-no-changes --verbosity minimal
python3 eng/test-verify-maintainability.py
bash eng/check-maintainability.sh
dotnet build Sharplink.slnx --no-restore -c Release -v minimal
./eng/verify-generated-assembly-dependencies.sh
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release --no-build
dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj -c Release --no-build
dotnet test --project test/SharpLink.LoadTest.Tests/SharpLink.LoadTest.Tests.csproj -c Release --no-build
```

## Duration contract and comparison

#374 measured representative fresh `PR Quick` runs at 6m 47s to 7m 06s, with a 7m 03s median. The bounded Fast-member steps from the sampled Quick job accounted for roughly two minutes before normal setup and cleanup overhead, which is the basis for the <=3-minute Fast target.

Every `PR Fast / fast` run records `Fast validation steps elapsed` in the GitHub Actions job summary. This timer starts in the first workflow step and stops immediately after the final validation step, so it intentionally excludes GitHub runner `Set up job` time and action post-cleanup. It must not be described as the complete job duration.

For a like-for-like comparison with the #374 `PR Quick` active-duration baseline, use the completed GitHub Actions job `started_at` and `completed_at` timestamps. The first live `PR Fast / fast` run on #437 completed successfully in 2m 18s by that full-job measure, versus the 7m 03s previous `PR Quick` median. The job summary keeps the <=3-minute full-job target, 5-minute hard timeout, and previous baseline visible while clearly identifying the narrower in-job timer scope.

The Fast tier must not expand its timeout to absorb checks that belong to Extended or Nightly. If normal successful full-job durations exceed the 3-minute target, Fast membership should be re-evaluated.
