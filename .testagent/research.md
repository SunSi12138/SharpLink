# 0.8.2 regression-test research

- Fixed-client initialization used the first public caller token, unlike the already-correct static/dynamic/multi-cluster ownership model.
- Static and dynamic endpoint dials duplicated only part of fixed-client handshake cancellation classification and leaked timeout cancellation as an unstructured inner exception.
- BCL DNS lookup failure is represented by `SocketException`; a catch-all after last-good also concealed bugs in a custom/internal query implementation and prevented the supervised outer loop from observing them.
- The writer always emits shortest-form VarUInt32, but the reader accepted a zero terminal group after continuation bytes.
- Error text used `Encoding.UTF8`, while metadata already used the strict `UTF8Encoding(false, true)` instance.
- The protocol parser performance gate uses an unchanged no-metadata request as a host-frequency control and keeps the affected path at 0 B/op.
