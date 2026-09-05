# Generated API 3 binary fixture

`SharpLink.Api3Fixture.dll.gz.b64` is a text-safe, gzip-compressed prebuilt managed assembly.
It is built from the source under `source/` with the published, exact `SharpLink.Sdk` 1.1.1
package—not with the repository's current generator.

The fixture contains one generated DTO codec and all five RPC call shapes: Unary, OneWay,
ClientStreaming, ServerStreaming, and DuplexStreaming. P3-00 proves that the current API 3 Runtime
can load it. P3-01 and P3-02 use the unchanged bytes to prove that Runtime 2.0 rejects API 3 before
resource materialization and without retaining the collectible load context.

The SHA-256 file records the checksum of the decompressed DLL. Regeneration must use an isolated
NuGet cache and must update the provenance file, compressed base64 payload, and checksum together.
