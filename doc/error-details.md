# Structured error details

SharpLink errors have two machine-readable identifiers and one diagnostic message:

- `SharpLinkException.Code` is the coarse, stable `SharpLinkErrorCode` classification.
- `SharpLinkException.DetailCode` is a stable `ushort` whose namespace is scoped by `Code`.
- `SharpLinkException.Message` is human-readable diagnostic text only. It can be truncated or reworded and must not be parsed to make program decisions.

`DetailCode == 0` means that no finer-grained classification was supplied. Unknown non-zero detail values are preserved by the protocol and exposed to callers. Code that does not recognize a detail value should treat it as an unknown detail within the corresponding top-level `Code`; it must not reinterpret it from `Message`.

For example:

```csharp
catch (SharpLinkException exception) when (
    exception.Code == SharpLinkErrorCode.ResourceExhausted &&
    exception.DetailCode == SharpLinkErrorDetails.ResourceExhausted.AdmissionQueue)
{
    // Admission queue capacity was exhausted.
}
```

## ResourceExhausted detail codes

The `SharpLinkErrorCode.ResourceExhausted` namespace is:

| Detail | Constant | Meaning |
| ---: | --- | --- |
| 0 | `Unspecified` | No finer-grained reason was supplied. |
| 1 | `ServerCallCapacity` | Server-wide concurrent-call capacity. |
| 2 | `PerConnectionCallCapacity` | Per-connection concurrent-call capacity. |
| 3 | `AdmissionConcurrency` | Admission concurrency limiter. |
| 4 | `AdmissionQueue` | Admission queue capacity. |
| 5 | `AdmissionRate` | Admission rate limiter. |
| 6 | `AdmissionPartitionCapacity` | Admission partition capacity. |
| 7 | `AdmissionOther` | Another bounded admission resource. |
| 8 | `PendingRequestCapacity` | Client pending-request capacity. |
| 9 | `SendQueueCapacity` | Session send-queue capacity. |
| 10 | `ServerDecodeConcurrency` | Server concurrent decode budget. |
| 11 | `ServerRetainedCompressedBytes` | Server retained-compressed-bytes budget. |
| 12 | `ServerDecodedBytes` | Server decoded-bytes budget. |
| 13 | `ServerDecodeQueue` | Server decode queue capacity. |
| 14 | `ServerPreAdmissionStreamBytes` | Server pre-admission stream-byte budget. |
| 15 | `PreparedBindingCapacity` | `PrepareAsync` could not allocate a prepared-binding slot on this physical connection. Existing bindings are unaffected. |

The public constants live under `SharpLinkErrorDetails.ResourceExhausted`.

## Wire format and compatibility

SharpLink 3.0 uses Protocol v2 minor 6, with a minimum compatible minor of 6. It retains the binary error payload layout introduced in minor 4:

```text
SharpLinkErrorCode : uint16 little-endian
DetailCode         : uint16 little-endian
MessageLength      : canonical varuint32
Message            : MessageLength bytes of UTF-8
```

Message truncation only changes the UTF-8 message and the frame's `Truncated` flag; it never removes or changes `DetailCode`.

The published 1.1.1/minor-3 layout carried only `(Code, Message)`; SharpLink 2.0 introduced `DetailCode` in minor 4. A 3.0 peer rejects both older minors during handshake, even though minor 4 uses the same error layout. See [Protocol v2](protocol-v2.md) for negotiation rules.
