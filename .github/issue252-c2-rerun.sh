#!/usr/bin/env bash
set -euo pipefail

FIXED="$RUNNER_TEMP/issue252-c2-evidence-fixed.sh"
cp .github/issue252-c2-evidence.sh "$FIXED"
python3 - "$FIXED" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1])
t=p.read_text()
needle='if "_slots.Length" in text or "ref _slots[index]" in text:\n'
insert='many("ref _slots[index]", "ref slots[index]", 3)\n\n'
if t.count(needle) != 1:
    raise SystemExit('C2 harness validation marker mismatch')
t=t.replace(needle, insert + needle, 1)
p.write_text(t)
PY
bash "$FIXED"
