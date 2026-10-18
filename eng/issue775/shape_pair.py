#!/usr/bin/env python3
"""Reuse the unchanged paired runner with explicit frozen shape-experiment identities."""
import boundary_pair

# This is a previous-static comparison, never a replacement for original-dev acceptance.
COMMITS = {"old": "8810547756d1c16367bba8d81f6707018b9d7e5d",
           "candidate": "8f1a81e2c3bfc2c0cfc4734b658684631aa2954e"}


def configure():
    boundary_pair.COMMITS = COMMITS.copy()
    return boundary_pair


if __name__ == "__main__":
    configure().main()
