# SharpLink 0.8.11 Performance Validation

Chinese: [`../performance-0.8.11.md`](../performance-0.8.11.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8 used one launch, five warmups, and fifteen measurements, repeated in reverse order. Normal Client registration/unregistration averaged 6.535 µs on 0.8.10 and 6.518 µs on 0.8.11, with allocation decreasing from 30.50 to 30.44 KB. Server averaged 6.407 µs on both revisions; the reverse-order run allocated 29.52 KB on both, while the first cross-process run varied between 29.46 and 29.58 KB without a stable regression signal. New aggregation objects exist only on exceptional rollback paths. Raw reports are under `artifacts/performance/0.8.11-dynamic-registration-ab/`.
