#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
client_source="$repository_root/src/SharpLink.Client/SharpLinkClient.cs"
server_source="$repository_root/src/SharpLink.Server/SharpLinkServer.cs"
client_project="$repository_root/src/SharpLink.Client"
server_project="$repository_root/src/SharpLink.Server"
client_builder="$repository_root/src/SharpLink.Client/SharpClientBuilder.cs"
server_builder="$repository_root/src/SharpLink.Server/SharpLinkServerBuilder.cs"

require_single_constructor() {
    local source_root="$1"
    local signature="$2"
    local runtime_name="$3"
    local count
    local total
    local primary
    count="$( (rg -n --glob '*.cs' "$signature" "$source_root" || true) | wc -l | tr -d ' ')"
    total="$( (rg -n --glob '*.cs' "^[[:space:]]*((public|private|protected|internal)([[:space:]]+(internal|protected))?[[:space:]]+)?${runtime_name}[[:space:]]*\\(" "$source_root" || true) | wc -l | tr -d ' ')"
    primary="$( (rg -n --glob '*.cs' "class[[:space:]]+${runtime_name}[[:space:]]*\\(" "$source_root" || true) | wc -l | tr -d ' ')"
    if [[ "$count" != "1" ]]; then
        echo "expected exactly one construction-boundary constructor under $source_root, found $count" >&2
        exit 1
    fi
    if [[ "$total" != "1" ]]; then
        echo "expected no legacy constructor overloads under $source_root, found $total constructor declarations" >&2
        exit 1
    fi
    if [[ "$primary" != "0" ]]; then
        echo "runtime partial declarations must not add a primary constructor under $source_root" >&2
        exit 1
    fi
}

require_no_constructor_resource_creation() {
    local source="$1"
    local start="$2"
    local end="$3"
    local resources="$4"
    if sed -n "/$start/,/$end/p" "$source" | rg -n "new ($resources)\\("; then
        echo "runtime constructor must not create owned resources: $source" >&2
        exit 1
    fi
}

require_constructor_topology_binding() {
    local constructor_body
    constructor_body="$(sed -n \
        '/^    internal SharpLinkClient(ClientRuntimeComposition composition)/,/^    public IRpcRuntimeContext RuntimeContext/p' \
        "$client_source")"
    for expression in \
        '_fixedEndpoint = fixedTopology.Endpoint' \
        '_cluster = new StaticClusterRuntime(this, staticTopology)' \
        '_cluster = new DynamicClusterRuntime(this, dynamicTopology)'; do
        if ! rg -q -F "$expression" <<<"$constructor_body"; then
            echo "Client constructor must bind every tagged topology before returning: missing $expression" >&2
            exit 1
        fi
    done
}

require_no_post_construction_topology_binding() {
    if rg -n --glob '*.cs' \
        'AttachClientBoundTopology|AttachFixedTopology|AttachStaticTopology|AttachDynamicTopology|_topologyAttached|_clientBoundTopologyAttached' \
        "$client_project"; then
        echo "Client topology must not depend on post-construction attachment" >&2
        exit 1
    fi
}

require_expected_concrete_creation_site() {
    local expression="$1"
    local expected="$2"
    local actual
    actual="$(rg -l "$expression" --glob '*.cs' "$repository_root" || true)"
    if [[ "$actual" != "$expected" ]]; then
        echo "unexpected concrete runtime creation sites for $expression:" >&2
        printf '%s\n' "$actual" >&2
        exit 1
    fi
}

require_single_constructor "$client_project" '^    internal SharpLinkClient\(ClientRuntimeComposition composition\)$' 'SharpLinkClient'
require_single_constructor "$server_project" '^    internal SharpLinkServer\(ServerRuntimeComposition composition\)$' 'SharpLinkServer'
require_no_constructor_resource_creation \
    "$client_source" \
    '^    internal SharpLinkClient(ClientRuntimeComposition composition)' \
    '^    internal static FrameworkTaskSupervisor CreateFrameworkTaskSupervisor' \
    'FrameworkTaskSupervisor'
require_no_constructor_resource_creation \
    "$server_source" \
    '^    internal SharpLinkServer(ServerRuntimeComposition composition)' \
    '^    public SharpLinkHealthStatus HealthStatus' \
    'FrameworkTaskSupervisor'
require_constructor_topology_binding
require_no_post_construction_topology_binding
require_expected_concrete_creation_site 'new SharpLinkClient\(' "$client_builder"
require_expected_concrete_creation_site 'new SharpLinkServer\(' "$server_builder"

echo "Runtime construction boundary verified."
