# 0.8.3 regression-test research

- Top-level read-only collections do not make mutable elements immutable; endpoint Attributes remained writable after snapshot publication.
- `CancellationTokenSource.Cancel()` runs callbacks synchronously. Calling it before an async method's first incomplete await can make `StopAsync` itself block and can abort cleanup if a callback throws.
- `catch/finally` cleanup awaits replaced in-flight connect exceptions when cleanup faulted.
- Hosted startup catch blocks had the same masking pattern and Server cleanup could skip token disposal.
- Metadata decoding validates every entry before construction, so transferring the array to an internal factory is safe; public callers still require defensive copying.
- `params ReadOnlySpan<T>` was measured but retained no allocation benefit on .NET 10, so the public signature was preserved.
