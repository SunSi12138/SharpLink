# Generated ABI package-mixing fixtures

These projects are intentionally excluded from `Sharplink.slnx`. They model unsupported package
graphs and must not produce a loadable assembly:

- `new-generator-old-abstractions`: the current SharpLink SDK and Generator with Abstractions 1.1.1.
- `old-generator-new-abstractions`: SharpLink SDK and Generator 1.1.1 with the current Abstractions.

On the release runner, run `eng/verify-generated-abi-mixing.sh` after packing the current
`VersionPrefix` into `artifacts/nuget`. Each fixture must compile with a same-version control.
The mixed graph must fail with a SharpLink package-resolution error or a generated-source ABI
diagnostic, and must produce no target assembly. Runtime rejection of a previously generated
assembly, including one with the same API number but a different identity, is a separate check.
