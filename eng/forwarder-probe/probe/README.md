# Explicit packaged SDK forwarding probe

This isolated .NET 10 console program has no NuGet dependencies. It does not modify or reference the production checkout. Its 20 fixed names match both `SdkTypeForwardingTests.cs` and `eng/public-api/3.0.0/SharpLink.Sdk.forwarders.txt` at source commit `8a13b12e799bb70a989eacac0c1b4cea22ab80ae`.

## Build and run

Use the independent .NET SDK 10.0.400, not the SDK being qualified by a hosted build. Set `DOTNET_CLI_HOME` and `NUGET_PACKAGES` to writable, isolated directories. `NuGet.Config` clears all feeds. Build with `--disable-build-servers /p:UseSharedCompilation=false /nodeReuse:false`, and set `DOTNET_CLI_USE_MSBUILD_SERVER=0` and `MSBUILDDISABLENODEREUSE=1`.

```text
dotnet build ForwarderProbe.csproj --configuration Release --disable-build-servers /p:UseSharedCompilation=false /nodeReuse:false
dotnet bin/Release/net10.0/ForwarderProbe.dll --self-test
dotnet bin/Release/net10.0/ForwarderProbe.dll --sdk /absolute/extracted/SharpLink.Sdk.dll --sdk-sha256 <64-hex-SHA256> --abstractions /absolute/extracted/SharpLink.Abstractions.dll --abstractions-sha256 <64-hex-SHA256>
```

The production form accepts exactly four unique named options with values, in any order. All paths must be absolute DLL paths, both file hashes are mandatory, and extra, unknown, duplicate or missing arguments fail. The self-test form accepts only the single `--self-test` argument. Exit 0 means the entire selected mode passed; exit 1 means failure. Standard output is one fixed-schema JSON object, without raw exception strings, file paths or captured logs.

## What a production pass establishes

1. Both specified files have the caller-pinned SHA-256, are bounded-size IL-only managed assemblies with one module, and have exactly the expected unsigned, neutral 3.0.0.0 assembly identities. Their metadata contains the expected `3.0.0+8a13b12e799bb70a989eacac0c1b4cea22ab80ae` informational version. Modules with global methods, including module initializers, are rejected. Attribute constructors are never invoked.
2. The SDK has exactly 20 ExportedType rows, with no unknown or duplicate names. Each is an AssemblyRef-backed type forwarder to the full expected Abstractions identity, including version, culture and public-key token. No local SDK TypeDef has one of those names. Each corresponding destination is a public Abstractions TypeDef.
3. A new collectible AssemblyLoadContext explicitly loads the two supplied DLL paths. The context permits only these exact SharpLink identities and exact full identities from the running .NET runtime's own trusted platform directory. Other application directories, default-loaded SharpLink assemblies, unknown dependencies, and native-library resolution are not allowed fallback paths.
4. All 20 types returned by SDK `Assembly.GetType` and SDK `Assembly.GetForwardedTypes` are reference-identical to the corresponding direct Abstractions `Assembly.GetType` results. An SDK-assembly-qualified `Type.GetType` entry point also returns those same objects using controlled assembly and type resolvers. The latter resolver intentionally delegates to SDK `Assembly.GetType`; these are three entry points, not three independent CLR binder implementations.
5. The loaded assemblies belong to the fresh context, have the exact full identities, input locations, and file metadata MVIDs. Every resolved contract belongs to the exact loaded Abstractions object and MVID. The context holds exactly the two SharpLink assemblies. Both input path contents are SHA-256 checked again before success.

## Scope limits

- This is an explicit SDK-qualified forwarding-hop check on supplied package-extracted DLLs. It is not an ordinary PackageReference consumer test: a current compiler can bind source types directly to Abstractions.
- It is not historical 2.0.3 consumer ABI proof. No historical binary, member invocation, RPC, service/object activation, serializer, transport or production SharpLink method is exercised.
- The caller must establish package/source provenance and pin hashes from validated package contents. An informational-version string and a supplied hash are not independent proof that a file came from a repository or a published release.
- The filesystem is assumed not to be maliciously raced. Before/after hashes, held read handles, locations and MVIDs detect changes but are not an adversarial filesystem immutability proof.
- A collectible context is created and `Unload` is requested in a `finally`-equivalent disposal path. Garbage-collection completion and actual unloading are not asserted.
- Runtime fallback trusts the active .NET runtime installation. This program is a bounded reflection check, not a general sandbox for untrusted assemblies. The caller should apply an overall process timeout and keep build/run logs local.

## Synthetic self-tests

`--self-test` manufactures minimal metadata-only assemblies with the pinned expected identity/version. It runs the same production validation pipeline for the positive fixture and metadata/input negative cases. These interfaces are purpose-built test shapes, not actual SharpLink code and not a historical consumer. Files are created in a unique temporary directory and removed afterward.

Coverage includes exact-20 success at all three reflection entry points, repeated success in another fresh context, both file-hash mismatches, missing/extra/duplicate/unknown forwarders, a local same-shape impostor, full destination identity retargets, a non-forwarder ExportedType, wrong SDK/source versions, missing/non-public destination types, missing input DLLs, strict CLI acceptance/rejection, and direct ALC boundary tests rejecting the default-loaded probe assembly, an unknown dependency, and a mismatched Abstractions identity.
