"""Require requested compilation evidence, not native equivalence or a cost budget."""

REQUIRED_TIERS = {
    "fullopts": ({"FullOpts"},),
    "tier0": ({"Tier0"},),
    "tier1": ({"Tier1", "Tier1-OSR"},),
    "pgo": ({"Instrumented Tier0"}, {"Tier1", "Tier1-OSR"}),
}


def validate_native_evidence(mode, rows):
    """An absent/inlined individual method stays absent, never a zero-cost result."""
    if not rows or any(row["NativeBytes"] <= 0 for row in rows):
        raise AssertionError(f"Missing or empty native evidence for {mode}")
    observed = {tier for row in rows for tier in row["Tier"]}
    for alternatives in REQUIRED_TIERS[mode]:
        if not observed.intersection(alternatives):
            raise AssertionError(
                f"Missing requested tier for {mode}: expected one of "
                f"{sorted(alternatives)}, observed {sorted(observed)}"
            )
    return sorted(observed)
