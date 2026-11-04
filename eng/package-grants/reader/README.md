# Assembly grants metadata inventory

Standalone, package-free .NET 10 tool for issue #805. It complements the public
API inventory, which intentionally excludes assembly attributes. It does not
change grants, impose a friend-assembly policy, or remove test friends.

## Build and run

Run outside repository-wide build properties, with a .NET 10 SDK:

```sh
dotnet build SharpLink.AssemblyGrants -c Release
dotnet SharpLink.AssemblyGrants/bin/Release/net10.0/SharpLink.AssemblyGrants.dll \
  exact/package/lib/net10.0/SharpLink.Abstractions.dll \
  exact/package/lib/net10.0/SharpLink.Runtime.dll > inventory.json
```

Pass the exact extracted package DLLs as separate arguments. There is no directory
search, dependency resolution, target assembly loading, reflection over target
types, or target-code execution. `PEReader` and `MetadataReader` inspect a byte
snapshot; SHA256 is computed over that same snapshot. The `AssemblyName` class is
used only to format identities from metadata, never to load an assembly.

The CLI writes one JSON object only after every requested input succeeds. Input
order and repeated inputs are preserved. The filename is a basename, not an
absolute path. Callers must bind each result to their ordered extraction manifest
using filename, identity and SHA256, especially when basenames repeat.

Exit codes: 0 success, 1 invalid/unreadable assembly or unsupported/malformed
recognized metadata, 2 missing inputs or unsupported CLI options. Errors go to
stderr; failed runs do not emit partial JSON on stdout. An existing redirected
output file is the caller's responsibility; the tool does not write files itself.

## JSON schema version 1

- `SchemaVersion`: 1.
- `Assemblies`: one object per exact input file, in argument order.
  - `FileName`, `Sha256`, `ModuleVersionId`, `InformationalVersion`.
  - `Identity`: `Name`, `Version`, `Culture`, `FullName`, `PublicKeyOrToken`,
    `HasFullPublicKey`, `Flags`.
  - `InternalsVisibleTo`, `IgnoresAccessChecksTo`: arrays of exact serialized
    strings in metadata order. No trimming, identity rewriting, sorting or
    deduplication. Full public-key qualification remains intact.
  - `IsReferenceAssembly`: recognized reference-assembly marker present.
  - `AssemblyReferences`: full identity objects with the fields listed above.
    `FullName` includes version, culture, public-key token and applicable
    retargetable/WindowsRuntime qualifiers; raw key bytes and flags are retained.
  - `TypeForwarders`: `{TypeName, Target}` objects, where `Target` is the full
    AssemblyRef identity. Nested exported types inherit the containing forwarder
    flag. Cyclic scopes and forwarders without an AssemblyRef target fail.
  - `RecognizedAttributes`: records containing `TypeName`, `ConstructorKind`,
    `Value`, `AllInternalsVisible`. The last field retains the optional IVT named
    boolean; null means it was not encoded, not a policy interpretation.

Recognized top-level attribute names are:

- `System.Runtime.CompilerServices.InternalsVisibleToAttribute`
- `System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute`
- `System.Runtime.CompilerServices.ReferenceAssemblyAttribute`
- `System.Reflection.AssemblyInformationalVersionAttribute`

Only assembly-level rows are inventoried. Both MemberRef and MethodDef
constructors are supported. Same short names in other namespaces and nested
imposters are excluded. This is name-based metadata inventory, not proof that an
attribute's defining assembly is trusted or that a runtime will honor a grant.

For recognized attributes the reader validates the constructor signature, prolog,
UTF-8 strings, named-argument count and complete payload consumption. IVT supports
the `AllInternalsVisible` boolean property. Unknown named arguments, malformed
payloads, null grant/version strings and duplicate informational-version
attributes fail closed. ReferenceAssembly supports both parameterless and string
constructors, including a null description. Unrelated attribute payloads are not
decoded; this is not a whole-image verifier or security audit.

## Tests

```sh
DOTNET_HOST_PATH=/path/to/dotnet bash test.sh > test-results.json
```

The harness compiles two fixtures and a package-free test executable. The grants
fixture contains a throwing module initializer, which must never execute. It
exercises actual compiled MemberRef IVT and MethodDef IgnoresAccessChecksTo
attributes, key qualification and optional named arguments. The no-grants fixture
uses same-short-name attributes in a different namespace. A generated reference
assembly verifies reference-marker detection.

Additional PE images are built using `MetadataBuilder`/`ManagedPEBuilder`, not
source-string simulations. They cover byte-identical duplicate CustomAttribute
rows, full AssemblyRef keys and flags, nested forwarding, MethodDef constructors,
non-assembly attributes, nested imposters and malformed payloads. Tests also
check CLI failures and all-or-nothing batch output.

**Multiplicity caveat:** SDK 10.0.400's C# compiler coalesces byte-identical
repeated assembly attributes. Source declarations do not prove emitted row
counts. The compiled IVT fixture uses distinct named arguments with the same
friend string to preserve repeated strings; the PE fixtures explicitly emit
identical duplicate rows. Inventory expectations must match actual package
metadata, without inventing duplicates or removing any that are present.

## Publish allowlist

Only publish the files listed in `publish-allowlist.txt`. Generated assemblies,
PDBs, runtime/deps files, `bin`, `obj`, `.cache`, `.test-output`, crash files and
raw build logs are local validation material and must not be published. The
compact `test-results.json` contains assertion names and a pass count only.
