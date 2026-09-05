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

The initial `SharpLinkErrorCode.ResourceExhausted` namespace is:

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

The public constants live under `SharpLinkErrorDetails.ResourceExhausted`.

## Wire format and compatibility

Protocol v2 minor 6 encodes every binary error payload as:

```text
SharpLinkErrorCode : uint16 little-endian
DetailCode         : uint16 little-endian
MessageLength      : canonical varuint32
Message            : MessageLength bytes of UTF-8
```

Message truncation only changes the UTF-8 message and the frame's `Truncated` flag; it never removes or changes `DetailCode`.

This binary shape is not compatible with the previous minor-5 `(Code, Message)` layout. SharpLink therefore sets both `ProtocolV2Constants.MinorVersion` and `MinimumCompatibleMinorVersion` to 6. A minor-5 peer is rejected during handshake instead of allowing either side to misinterpret an error payload.
