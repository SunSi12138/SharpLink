# Queued registration reproduction

This prerequisite keeps a request's selected registration, cancellation source and
stream-shape decisions coherent across an asynchronous admission continuation.
It is not a coordinated catalog switch or an ExactRegistration client API.

## Functional and maintenance guards

With the repository's .NET SDK installed:

```sh
python3 eng/tests/test_admitted_dispatch_parity.py
python3 eng/test-verify-maintainability.py
bash eng/check-maintainability.sh
dotnet build Sharplink.slnx -c Release
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release --no-build --treenode-filter '/*/*/ServiceRegistrationTests/*'
dotnet test --project test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj -c Release --no-build --treenode-filter '/*/*/RuntimeAssemblyIntegrationTests/*'
```

The paired source guards compare the entire admitted RPC/OneWay resource skeleton
with the immediate implementation, permitting only the explicitly described
selection, retired response and already-completed admission differences. The copy
helper guard prevents a divergent retained-buffer/permit cleanup path.

## Paired layout/native reproduction

Create a separate clean baseline checkout at
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`, then run:

```sh
python3 eng/queued-registration-probes/run.py \
  --baseline /absolute/path/to/baseline --candidate "$PWD" \
  --output /absolute/path/to/new-empty-output --default-repeats 4
```

To compare the same assemblies on a separately installed official runtime, add
`--runtime /absolute/path/to/runtime/dotnet`. Builds use the SDK's `dotnet` from
PATH; both arms execute using the selected runtime. The output directory must be
new or empty because the runtime appends native listings. The script captures
SDK/runtime versions, source cleanliness, assembly hashes, managed layouts and
FullOpts/non-PGO/default-tiered native listings. It does not infer throughput.

The checked-in numeric results retain all four default-PGO runs for each arm,
including the candidate's occasional extra ordinary cancellation-token spill.
They describe the combined-capture representation, not an acceptance threshold.
No environment variables, credentials or prior denied research archives are
included. The fresh CI artifact, when available, is generated directly from this
source and the pinned public baseline.
