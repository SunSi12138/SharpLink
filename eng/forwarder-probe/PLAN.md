# Frozen package forwarding-hop validation

Source: 8a13b12e799bb70a989eacac0c1b4cea22ab80ae; production tree bfc211191d91b5a77c6a8f0085a0c00a459c5583.

Existing coverage is not empty: UnitTests/SdkTypeForwardingTests loads SDK and checks 20 forwarding names/destination simple name; public API extraction reflects over assemblies for declaration surfaces and separately compares ExportedType metadata; RpcIssue898RegressionTests tests compiler discovery. That regression explicitly demonstrates ordinary C# compilation through the forwarding facade emits Abstractions references rather than an SDK AssemblyRef. The previously passing full package consumer run was a7a11c85, not this exact source. No results are relabeled.

This new lane builds only Abstractions, Generator and SDK; packs Abstractions and SDK; validates package identity, source commit, unique shipping DLL layout with frozen helpers; extracts the two DLLs; then resolves every SDK-qualified forwarded type through a controlled isolated ALC and compares object identity with direct Abstractions types. Check actual file hashes, MVID, complete target identity, informational version and runtime identity. Probe has no SharpLink project/package references and does not execute RPC, instantiate SharpLink objects, or alter grants.

This supplements runtime forwarding-hop evidence for actual package bytes. It does not claim NuGet dependency closure, old 2.0.3 consumer binary ABI compatibility, all public member compatibility, default package smoke, Trim/AOT, security isolation or permission approval. An ALC is a loading mechanism, not a security sandbox.

Use the existing owned-process helper, fresh source checkout and clean exact tools checkout, independent temp probe build, SDK 10.0.114 pinned for both, no raw logs or artifacts uploaded. All commands bounded, fail closed on timeout/nonzero/cleanup errors. Whitelisted compact identities and counts only. No merge, release, deletion, grant modification, D2 or Cancel task. Submit source and synthetic tests for independent review before publishing a single workflow run.

## Audited coverage references (frozen source)
- test/SharpLink.UnitTests/SdkTypeForwardingTests.cs:31-49, project references in SharpLink.UnitTests.csproj:7-14.
- test/SharpLink.Generator.Tests/RpcIssue898RegressionTests.cs:11-129; emitted consumers have no SDK AssemblyRef at93-99.
- eng/SharpLink.PublicApi/Program.cs:31-102; eng/public-api/README-forwarders.md:13-16.
- eng/run-default-package-smoke.py:285-300 already checks consumed/runtime DLL identities and five markers. Do not duplicate or schedule that broader RPC lane in this task.
- Api3BinaryFixtureIntegrationTests.cs:16-90 rejects1.1.1/API3; Api4BinaryFixtureIntegrationTests.cs:18-50 rejects a frozen development collision sentinel. Neither is a supported2.0.3 consumer.

## Deliberate exclusions
This narrow lane does not invoke the nine-package aggregate verify-packages.sh, since it produces only two packages. It reuses each pair's unchanged verify-package-identities.py plus compare-published-api.extract_package for unique implementation DLL/layout/source provenance. It does not claim full package-set validation or analyzer execution. Generator is built only to let SDK pack include its existing analyzer. There are no production source changes.

The controlled Type.GetType assembly-qualified resolver explicitly chooses the SDK and delegates the type lookup to SDK.GetType. These are three exercised call entrances, not three independent CLR binder mechanisms. A fresh collectible ALC is requested and unloading is requested on disposal; collection/unload completion is not measured or claimed.
