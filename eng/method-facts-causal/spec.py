"""Fixed schedules; the missing profile keeps the original local manifest contract."""
CELLS = ["I/T", "P/T", "I/U", "P/U"]
SAMPLES = 5
CALLS = 2000
WARMUP = 1000
LOCAL = "local-m0-m1-v1"
CONTROLLED = "controlled-three-arm-v1"
PROFILES = {
    LOCAL: {
        "pairs": ["AA", "AB", "BB", "BA", "BA", "BB", "AB", "AA"],
        "comparisons": {"B/A": ("A", "B")},
        "title": "M0→M1 fixed causal campaign calculations",
    },
    CONTROLLED: {
        "pairs": ["OO", "OA", "AB", "BB", "BO", "AA", "BA", "AO", "OB",
                  "BO", "OA", "AB", "AA", "OB", "BB", "BA", "AO", "OO"],
        "comparisons": {"B/O": ("O", "B"), "B/A": ("A", "B"), "A/O": ("O", "A")},
        "title": "Controlled pre-M / M0 / M1 campaign calculations",
    },
}


def profile_for(manifest):
    name = manifest.get("profile", LOCAL)
    if name not in PROFILES:
        raise ValueError(f"Unknown fixed campaign profile: {name}")
    return name, PROFILES[name]


def validate_schedule(manifest):
    name, profile = profile_for(manifest)
    if manifest["pairs"] != profile["pairs"]:
        raise ValueError("Pair schedule differs from the predeclared profile")
    expected = list("".join(profile["pairs"]))
    if [p["arm"] for p in manifest["processes"]] != expected:
        raise ValueError("Process arms/order differ from the predeclared profile")
    return name, profile
