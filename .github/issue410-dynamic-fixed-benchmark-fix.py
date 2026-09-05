from pathlib import Path

path = Path("test/SharpLink.Benchmarks/DynamicFixedWindowIntegratedBenchmarks.cs")
text = path.read_text(encoding="utf-8")
old = "using BenchmarkDotNet.Engines;\nusing SharpLink.Server;\n"
new = "using BenchmarkDotNet.Engines;\nusing SharpLink.Abstractions;\nusing SharpLink.Server;\n"
if text.count(old) != 1:
    raise RuntimeError(f"expected one benchmark using block, found {text.count(old)}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
