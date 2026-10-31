# Historical runtime facts classification control

The accompanying file is preserved byte-for-byte from source base `fb5344b6`.
SHA-256: `6b16c179338681f79ac05e1373a2f4808c615692c51c1ef2adaa709c4736526d`.

Selected cold-facts policy B changes the SPI timing: invalid facts for declared methods now fail registration preparation with InvalidManifest. It does not permit a successfully published registration to return new null/default/wrong-identity facts during invocation. The current executable-source counterpart therefore tests an undeclared ID against the prepared domain and the exact NotFound/UnknownBoundMethod wire response. Invalid declared candidates are covered by cold preparation tests. This historical runtime oracle is not silently rewritten or reported as passing under the new SPI. No tests were executed during this preservation.
