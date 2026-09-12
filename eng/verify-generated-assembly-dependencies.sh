#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${SHARPLINK_CONFIGURATION:-Release}"
TARGET_FRAMEWORK="${SHARPLINK_TARGET_FRAMEWORK:-net10.0}"
SCANNER="$ROOT/test/SharpLink.GeneratedAssemblyScanner/SharpLink.GeneratedAssemblyScanner.csproj"

assemblies=(
  "$ROOT/demo/MultiCluster.Orders.Contracts/bin/$CONFIGURATION/$TARGET_FRAMEWORK/MultiCluster.Orders.Contracts.dll"
  "$ROOT/demo/MultiCluster.Payments.Contracts/bin/$CONFIGURATION/$TARGET_FRAMEWORK/MultiCluster.Payments.Contracts.dll"
  "$ROOT/demo/SeparatedContracts/bin/$CONFIGURATION/$TARGET_FRAMEWORK/SeparatedContracts.dll"
  "$ROOT/test/SharpLink.AotContracts/bin/$CONFIGURATION/$TARGET_FRAMEWORK/SharpLink.AotContracts.dll"
  "$ROOT/test/SharpLink.AotServices/bin/$CONFIGURATION/$TARGET_FRAMEWORK/SharpLink.AotServices.dll"
  "$ROOT/test/SharpLink.DynamicContracts/bin/$CONFIGURATION/$TARGET_FRAMEWORK/SharpLink.DynamicPlugin.Contracts.dll"
  "$ROOT/test/SharpLink.DynamicServices/bin/$CONFIGURATION/$TARGET_FRAMEWORK/SharpLink.DynamicPlugin.Services.dll"
)

for assembly in "${assemblies[@]}"; do
  if [[ ! -f "$assembly" ]]; then
    echo "Generated assembly dependency gate could not find '$assembly'." >&2
    echo "Build Sharplink.slnx in $CONFIGURATION before running this gate." >&2
    exit 2
  fi
done

dotnet run \
  --project "$SCANNER" \
  -c "$CONFIGURATION" \
  --no-build \
  --no-restore \
  -- \
  --verify-clean \
  "${assemblies[@]}"
