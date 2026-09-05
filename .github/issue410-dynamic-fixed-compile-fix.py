from pathlib import Path

path = Path("src/SharpLink.Server/Admission/AdmissionLimiterState.cs")
text = path.read_text(encoding="utf-8")
old = "    internal RateLimiter LimiterForAdmission => _dynamicFixedWindow ?? this;\n"
new = "    internal RateLimiter LimiterForAdmission\n        => _dynamicFixedWindow is null ? this : _dynamicFixedWindow;\n"
if text.count(old) != 1:
    raise RuntimeError(f"expected one LimiterForAdmission expression, found {text.count(old)}")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
