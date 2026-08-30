# PR Fast gate

`PR Fast` is the bounded pull-request validation tier defined by #374 and implemented by #375. It runs for every pull request target and can also be started manually with `workflow_dispatch`.

The stable blocking-shaped job/status is `fast`. The job has a 5-minute hard timeout and targets successful active duration of 3 minutes or less under normal GitHub-hosted runner conditions.

## Fast checks

The Fast job intentionally contains only the high-signal bounded checks assigned to Fast by #374:

1. Restore.
2. Whitespace formatting verification.
3. Maintainability debt baseline gate.
4. Release build.
5. Generated-assembly Runtime dependency guard.
6. Unit tests.
7. Generator tests.
8. Load-test component unit tests.

Debug build, integration tests, NativeAOT smoke, packaging checks, package smoke, demo/load smoke, chaos validation, and codec compatibility remain in their existing workflows until #376 moves them deliberately.

## Local equivalent

Run these commands from the repository root with the .NET 10 SDK available:

```bash
dotnet restore Sharplink.slnx
dotnet format whitespace Sharplink.slnx --no-restore --verify-no-changes --verbosity minimal
bash eng/check-maintainability.sh
dotnet build Sharplink.slnx --no-restore -c Release -v minimal
./eng/verify-generated-assembly-dependencies.sh
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release --no-build
dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj -c Release --no-build
dotnet test --project test/SharpLink.LoadTest.Tests/SharpLink.LoadTest.Tests.csproj -c Release --no-build
```

## Duration contract and comparison

#374 measured representative fresh `PR Quick` runs at 6m 47s to 7m 06s, with a 7m 03s median. The bounded Fast-member steps from the sampled Quick job accounted for roughly two minutes before normal setup and cleanup overhead, which is the basis for the <=3-minute Fast target.

Every `PR Fast / fast` run records its own elapsed job duration in the GitHub Actions job summary and displays it next to the <=3-minute target, 5-minute hard timeout, and previous 7m 03s `PR Quick` median. This keeps the comparison visible on each run instead of relying on a stale one-off measurement.

The Fast tier must not expand its timeout to absorb checks that belong to Extended or Nightly. If normal successful runs exceed the 3-minute target, Fast membership should be re-evaluated.
