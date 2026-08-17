# Generated API 4 binary fixture

`SharpLink.Api4Fixture.dll.gz.b64` is a text-safe, gzip-compressed prebuilt managed assembly.
It is built from the source under `source/` with the repository's in-tree generator at the dev
commit recorded in `PROVENANCE.md` — i.e., the last generated surface that still declared
`ApiVersion = 4` (post-#167 codec architecture, before the API 5 bump).

The fixture contains one generated DTO codec (implementing the API 4 sized-codec surface) and all
five RPC call shapes: Unary, OneWay, ClientStreaming, ServerStreaming, and DuplexStreaming.

The API 5 Runtime rejects this binary at every registration boundary (direct loader, Client,
Server, multi-cluster registration and replacement) with an expected/actual version mismatch,
before manifest materialization, without publishing snapshots, and without retaining the
collectible load context. This proves the previous self-describing ABI is recognized and rejected
early rather than adapted.

The SHA-256 file records the checksum of the decompressed DLL. Regeneration must build against
the pre-bump repository commit recorded in `PROVENANCE.md` and must update the provenance file,
compressed base64 payload, and checksum together.
