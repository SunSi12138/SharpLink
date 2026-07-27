# SharpLink 0.8.40 performance validation

On Apple M4 with .NET SDK 10.0.102, exact `8fffab7` and the final candidate alternated a real TCP unary harness. Every process warmed the plain and one-Client-plus-one-Server-interceptor paths for 2,000 calls, then measured nine samples of 20,000 calls.

Intercepted baseline process medians were 39.478/40.752/39.845 microseconds; candidate medians were 38.997/40.234/40.298 microseconds. Median-of-process medians changed from 39.845 to 40.234 microseconds (+0.98%) with overlapping ranges, while allocation fell from approximately 1,584.02-1,584.04 to 1,560.01-1,560.02 B/op. Plain-path medians changed from 38.640 to 38.454 microseconds (-0.48%) with allocation unchanged at approximately 320 B/op.

Packed flags reduced `RpcMethodDescriptor` from the exact baseline's 48 bytes to 40 bytes, preventing response-nullability metadata from enlarging interceptor contexts. Raw harness projects and process output are retained under `artifacts/performance/0.8.40-baseline/` and `artifacts/performance/0.8.40-interceptor-ab/`. The combined gate passed non-incremental Release with no warnings/errors, Generator 119/119, Unit 486/486, Integration 250/250, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory functional smoke.
