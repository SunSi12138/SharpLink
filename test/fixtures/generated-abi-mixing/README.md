# Generated ABI package-mixing fixtures

These projects are intentionally excluded from `Sharplink.slnx`. They model unsupported package
graphs and must not produce a loadable assembly:

- `new-generator-old-abstractions`: SharpLink.Sdk/Generator 2.0.0 with Abstractions 1.1.1.
- `old-generator-new-abstractions`: SharpLink.Sdk/Generator 1.1.1 with Abstractions 2.0.0.

Run `eng/verify-generated-abi-mixing.sh` after packing 2.0.0 packages into `artifacts/nuget`.
The gate accepts a package-resolution rejection or an ABI compile rejection, but always requires
the target assembly to remain absent. This prevents an old Generator from producing an API 3
shape that could be mistaken for API 4 through consumer-side constants.
