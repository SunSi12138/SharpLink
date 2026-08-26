# Generated API 4 binary fixture

`SharpLink.Api4Fixture.dll.gz.b64` is a text-safe, gzip-compressed prebuilt managed assembly.
It is built from the source under `source/` with the repository's in-tree generator at the dev
commit recorded in `PROVENANCE.md` — i.e., the last generated surface that still declared
`ApiVersion = 4` (post-#167 codec architecture, before the API 5 bump).

The fixture contains one generated DTO codec (implementing the API 4 sized-codec surface) and all
five RPC call shapes: Unary, OneWay, ClientStreaming, ServerStreaming, and DuplexStreaming.

This fixture is not a supported compatibility boundary. It is retained as a discriminator-collision
sentinel: this development binary already stamped integer API 4 but uses the pre-#287
`IRpcChannel(... SharpLinkCallOptions ...)` shape. The 2.0 line also uses API 4 because version
numbering is anchored to the published 1.1.1/API3 baseline, so the current runtime additionally
requires an exact ABI identity in the self-describing locator. This frozen binary must therefore be
rejected before manifest materialization even though its integer API value is also 4.

The SHA-256 file records the checksum of the decompressed DLL. Regeneration must build against
the pre-bump repository commit recorded in `PROVENANCE.md` and must update the provenance file,
compressed base64 payload, and checksum together.
