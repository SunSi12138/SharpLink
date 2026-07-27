# SharpLink 0.8.2 Performance Validation

Chinese: [`../performance-0.8.2.md`](../performance-0.8.2.md)

BenchmarkDotNet compared 0.8.1 commit `5d30863` with the canonical VarUInt32 branch on Apple M4 / .NET 10.0.2. Each benchmark used three independent launches, three warmups, and ten measurement iterations. The unchanged contiguous Request parser served as the same-run host control.

The control moved from 39.32 ns to 40.23 ns, while the metadata parser moved from 42.67 ns to 39.60 ns; both remained 0 B/op. Baseline cross-launch variance was high, so this is recorded only as a no-regression result, not a 7.2% improvement claim. The normalized metadata/control ratio moved from 1.085 to 0.984. A duplicate-project setup attempt produced no measurements and was excluded.
