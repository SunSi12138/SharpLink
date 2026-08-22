#!/usr/bin/env bash
set -euo pipefail

python3 .github/issue-287-patch.py
python3 .github/issue-287-generator.py
python3 .github/issue-287-tests.py
python3 .github/issue-287-review-tests.py
python3 .github/issue-287-followup.py
python3 .github/issue-287-generator-tests.py
python3 .github/issue-287-syntax-fix.py
python3 .github/issue-287-inherited-budget.py
python3 .github/issue-287-server-handshake-test.py
python3 .github/issue-287-docs.py
python3 .github/issue-287-review-round2.py
python3 .github/issue-287-review-round2-followup.py
python3 .github/issue-287-review-round2-compile-fixes.py
python3 - <<'PY'
from pathlib import Path
p = Path('src/SharpLink.Client/SharpLinkClient.RpcChannel.cs')
text = p.read_text().replace('DateTimeOffset? deadline = null,', 'RpcDeadline deadline = default,')
p.write_text(text)
p = Path('src/SharpLink.Client/SharpLinkClient.Interceptors.cs')
p.write_text(p.read_text().replace('context.Options', 'context.Metadata'))
p = Path('src/SharpLink.Generator/RpcGenerator.Analysis.cs')
p.write_text(p.read_text().replace('                m.Parameters.Count(IsCallOptionsParameter) > 1 ||\n', ''))
p = Path('src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs')
p.write_text(p.read_text().replace('if (IsCancellationTokenParameter(parameter) || IsCallOptionsParameter(parameter))', 'if (IsCancellationTokenParameter(parameter))'))
PY
cat \
  .github/issue-287-review-round3.part0 \
  .github/issue-287-review-round3.part1 \
  .github/issue-287-review-round3.part2 \
  .github/issue-287-review-round3.part3 \
  > /tmp/issue-287-review-round3.patch.xz
xz -t /tmp/issue-287-review-round3.patch.xz
xz -dc /tmp/issue-287-review-round3.patch.xz | git apply -
python3 .github/issue-287-review-round3-fix.py
python3 .github/issue-287-review-round3-integration-fix.py
python3 .github/issue-287-unit-fixes.py

git diff --check
! git grep -n -E 'SharpLinkCallOptions|IsCallOptions|HasCallOptions' -- src
! git grep -n 'WaitForReady' -- src/SharpLink.Client
! git grep -n -E 'TimeBudgetMinorVersion|ToUnixTimeMilliseconds|FromUnixTimeMilliseconds' -- src/SharpLink.Client src/SharpLink.Server src/SharpLink.Abstractions/ProtocolV2.cs
! git grep -n '1.1.1' -- eng/verify-protocol-v2-cross-version.sh
python3 - <<'PY'
from pathlib import Path
assert 'public const int Api = 4;' in Path('src/SharpLink.Abstractions/SharpLinkGeneratedAssemblyManifest.cs').read_text()
assert 'public int ApiVersion => 4;' in Path('src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs').read_text()
assert 'Get<TContract>(SharpLinkMetadata metadata)' not in Path('src/SharpLink.Abstractions/ISharpLinkClient.cs').read_text()
assert 'GetWithMetadata<TContract>(SharpLinkMetadata metadata)' in Path('src/SharpLink.Abstractions/ISharpLinkClient.cs').read_text()
assert 'RpcDeadline deadline)' not in Path('src/SharpLink.Runtime/OwnedFrame.cs').read_text()
PY
