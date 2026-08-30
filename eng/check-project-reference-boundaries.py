#!/usr/bin/env python3
"""Validate SharpLink production ProjectReference boundaries."""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

MODE_METADATA = ("ReferenceOutputAssembly", "OutputItemType")
DYNAMIC_MARKERS = ("$(", "@(", "%(", "->")
PROPERTY_PATTERN = re.compile(r"\$\(([^)]+)\)")


class GuardConfigurationError(RuntimeError):
    pass


@dataclass(frozen=True)
class Edge:
    source: str
    target: str
    mode: str


@dataclass(frozen=True)
class Policy:
    production_root: str
    production_project_glob: str
    projects: dict[str, str]
    edges: dict[tuple[str, str], Edge]


@dataclass(frozen=True)
class GuardResult:
    project_count: int
    declaration_count: int
    active_count: int
    violations: tuple[str, ...]


def _scalar(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
        return value[1:-1]
    return value


def load_policy(path: Path) -> Policy:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise GuardConfigurationError(f"cannot read policy {path}: {exc}") from exc

    version = None
    section = None
    scope: dict[str, str] = {}
    projects: dict[str, str] = {}
    edge_rows: list[dict[str, str]] = []
    current: dict[str, str] | None = None

    for raw in lines:
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        line = raw.strip()
        if indent == 0:
            current = None
            if line.startswith("schema_version:"):
                try:
                    version = int(_scalar(line.split(":", 1)[1]))
                except ValueError as exc:
                    raise GuardConfigurationError("schema_version must be an integer") from exc
                section = None
            elif line.endswith(":"):
                section = line[:-1]
            else:
                section = None
            continue
        if section == "scope" and indent == 2 and ":" in line:
            key, value = line.split(":", 1)
            scope[key.strip()] = _scalar(value)
        elif section == "projects" and indent == 2 and ":" in line:
            key, value = line.split(":", 1)
            key, value = key.strip(), _scalar(value)
            if not key or not value or key in projects:
                raise GuardConfigurationError(f"invalid or duplicate policy project: {key!r}")
            projects[key] = value.replace("\\", "/")
        elif section in {"allowed_references", "temporary_exceptions"}:
            if indent == 2 and line.startswith("- "):
                current = {}
                edge_rows.append(current)
                rest = line[2:]
                if ":" in rest:
                    key, value = rest.split(":", 1)
                    current[key.strip()] = _scalar(value)
            elif indent >= 4 and current is not None and ":" in line:
                key, value = line.split(":", 1)
                current[key.strip()] = _scalar(value)

    if version != 1:
        raise GuardConfigurationError(f"unsupported policy schema_version: {version!r}; expected 1")
    production_root = scope.get("production_root")
    production_glob = scope.get("production_project_glob")
    if not production_root or not production_glob or not projects:
        raise GuardConfigurationError("policy must define production root/glob and projects")

    edges: dict[tuple[str, str], Edge] = {}
    for row in edge_rows:
        source, target, mode = row.get("from"), row.get("to"), row.get("mode")
        if source not in projects or target not in projects or mode not in {"assembly", "analyzer"}:
            raise GuardConfigurationError(f"invalid policy edge: {row!r}")
        key = (source, target)
        if key in edges:
            raise GuardConfigurationError(f"duplicate policy edge: {source} -> {target}")
        edges[key] = Edge(source, target, mode)
    return Policy(production_root, production_glob, projects, edges)


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _repo_path(path: Path, root: Path) -> str | None:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return None


def _display(path: Path, root: Path) -> str:
    return _repo_path(path, root) or str(path.resolve())


def _native(value: str) -> str:
    return value.replace("\\", os.sep).replace("/", os.sep)


def _dynamic(value: str) -> bool:
    return not value.strip() or any(x in value for x in DYNAMIC_MARKERS) or any(x in value for x in "*?[];")


def _metadata(element: ET.Element, name: str) -> tuple[bool, str | None, bool]:
    values: list[tuple[str, bool]] = []
    for key, value in element.attrib.items():
        if key.lower() == name.lower():
            values.append((value.strip(), False))
    for child in element:
        if _local(child.tag).lower() == name.lower():
            values.append(((child.text or "").strip(), any(k.lower() == "condition" for k in child.attrib)))
    if not values:
        return False, None, False
    unique = {value for value, _ in values}
    return True, next(iter(unique)) if len(unique) == 1 else None, any(c for _, c in values)


def _mode_names(element: ET.Element) -> list[str]:
    return [name for name in MODE_METADATA if _metadata(element, name)[0]]


def _validate_mode(edge: Edge, element: ET.Element, label: str, violations: list[str]) -> None:
    values: dict[str, str | None] = {}
    for name in MODE_METADATA:
        present, value, conditioned = _metadata(element, name)
        if not present:
            values[name] = None
            continue
        if value is None:
            violations.append(f"{label}: conflicting {name} metadata makes reference mode ambiguous")
            values[name] = None
            continue
        if conditioned:
            violations.append(f"{label}: {name} must not have a Condition")
        if not value or any(marker in value for marker in DYNAMIC_MARKERS):
            violations.append(f"{label}: {name} must be a literal value, got {value!r}")
        values[name] = value

    roa = values["ReferenceOutputAssembly"]
    oit = values["OutputItemType"]
    normalized = "true" if roa is None else roa.lower()
    if normalized not in {"true", "false"}:
        violations.append(f"{label}: ReferenceOutputAssembly must be true or false, got {roa!r}")
        normalized = None
    if edge.mode == "assembly":
        if normalized != "true":
            violations.append(f"{label}: reference-mode violation; assembly edge requires ReferenceOutputAssembly=true")
        if oit is not None and oit.lower() == "analyzer":
            violations.append(f"{label}: reference-mode violation; assembly edge must not use OutputItemType=Analyzer")
    else:
        if normalized != "false":
            violations.append(f"{label}: analyzer-reference metadata violation; ReferenceOutputAssembly=false is required")
        if oit is None or oit.lower() != "analyzer":
            violations.append(f"{label}: analyzer-reference metadata violation; OutputItemType=Analyzer is required")


def _automatic(project: Path, root: Path, name: str) -> Path | None:
    current, root = project.parent.resolve(), root.resolve()
    while True:
        candidate = current / name
        if candidate.is_file():
            return candidate
        if current == root or root not in current.parents:
            return None
        current = current.parent


def _expand_import(value: str, source: Path, project: Path, root: Path, violations: list[str]) -> list[Path]:
    raw = value.strip()
    if not raw:
        return []
    replacements = {
        "MSBuildThisFileDirectory": str(source.parent.resolve()) + os.sep,
        "MSBuildThisFileFullPath": str(source.resolve()),
        "MSBuildProjectDirectory": str(project.parent.resolve()),
        "MSBuildProjectFullPath": str(project.resolve()),
    }
    unknown = [name for name in PROPERTY_PATTERN.findall(raw) if name not in replacements]
    if unknown:
        violations.append(f"{_display(source, root)}: repository import is not statically traversable: {raw!r}")
        return []
    expanded = raw
    for name, replacement in replacements.items():
        expanded = expanded.replace(f"$({name})", replacement)
    if any(x in expanded for x in ("@(", "%(", "->", ";")):
        violations.append(f"{_display(source, root)}: repository import is not statically traversable: {raw!r}")
        return []
    candidate = Path(_native(expanded))
    if not candidate.is_absolute():
        candidate = source.parent / candidate
    paths = [Path(p) for p in glob.glob(str(candidate), recursive=True)] if any(x in str(candidate) for x in "*?[]") else [candidate]
    return [p.resolve() for p in paths if _repo_path(p, root) is not None and p.is_file()]


def _closure(project: Path, root: Path, violations: list[str]) -> list[tuple[Path, ET.Element]]:
    queue = [project.resolve()]
    for name in ("Directory.Build.props", "Directory.Build.targets"):
        path = _automatic(project, root, name)
        if path is not None:
            queue.append(path.resolve())
    seen: set[Path] = set()
    result: list[tuple[Path, ET.Element]] = []
    while queue:
        path = queue.pop(0).resolve()
        if path in seen:
            continue
        seen.add(path)
        try:
            xml = ET.parse(path).getroot()
        except (OSError, ET.ParseError) as exc:
            raise GuardConfigurationError(f"cannot parse MSBuild XML {_display(path, root)}: {exc}") from exc
        result.append((path, xml))
        for element in xml.iter():
            if _local(element.tag) == "Import" and element.attrib.get("Project") is not None:
                queue.extend(_expand_import(element.attrib["Project"], path, project, root, violations))
    return result


def _references(xml: ET.Element) -> Iterable[tuple[ET.Element, bool, bool]]:
    parents = {child: parent for parent in xml.iter() for child in parent}
    for element in xml.iter():
        if _local(element.tag) != "ProjectReference":
            continue
        parent = parents.get(element)
        in_definition = in_target = False
        while parent is not None:
            name = _local(parent.tag)
            in_definition |= name == "ItemDefinitionGroup"
            in_target |= name == "Target"
            parent = parents.get(parent)
        yield element, in_definition, in_target


def _target(include: str, project: Path) -> Path:
    path = Path(_native(include))
    return (project.parent / path).resolve() if not path.is_absolute() else path.resolve()


def _audit_declarations(project_id: str, project: Path, root: Path, policy: Policy, by_path: dict[str, str], violations: list[str]) -> int:
    count = 0
    for source, xml in _closure(project, root, violations):
        source_name = _display(source, root)
        imported = source.resolve() != project.resolve()
        for ref, in_definition, in_target in _references(xml):
            mode_names = _mode_names(ref)
            if in_definition:
                if mode_names:
                    violations.append(f"{source_name}: ItemDefinitionGroup must not supply production ProjectReference mode metadata: {', '.join(mode_names)}")
                continue
            update = ref.attrib.get("Update")
            if update is not None:
                if mode_names:
                    violations.append(f"{source_name}: ProjectReference Update must not supply/override mode metadata {', '.join(mode_names)} for {update!r}")
                continue
            include = ref.attrib.get("Include")
            if include is None:
                if in_target and ref.attrib.get("Remove") is None and mode_names:
                    violations.append(f"{source_name}: ProjectReference target mutation must not supply/override mode metadata {', '.join(mode_names)}")
                continue

            count += 1
            condition = ref.attrib.get("Condition", "").strip()
            label = f"{project_id} ProjectReference {include!r} in {source_name}"
            if _dynamic(include):
                violations.append(f"{label}: dynamic/unresolvable production ProjectReference Include is denied; use a literal project path")
                continue
            target_path = _target(include, project)
            target_rel = _repo_path(target_path, root)
            target_id = by_path.get(target_rel or "")
            hidden = " condition-hidden" if condition else ""
            if target_id is None:
                kind = "imported forbidden reference" if imported else "forbidden production edge"
                violations.append(f"{label}:{hidden} {kind}; target {target_rel or str(target_path)!r} is not a registered production project")
                continue
            edge = policy.edges.get((project_id, target_id))
            if edge is None:
                kind = "imported forbidden reference" if imported else "forbidden production edge"
                violations.append(f"{label}:{hidden} {kind} {project_id} -> {target_id} is not authorized by policy")
                continue
            _validate_mode(edge, ref, f"{label} ({project_id} -> {target_id}, mode={edge.mode})", violations)
    return count


def _json(stdout: str) -> dict:
    start, end = stdout.find("{"), stdout.rfind("}")
    if start < 0 or end < start:
        raise GuardConfigurationError("MSBuild -getItem output did not contain JSON")
    try:
        value = json.loads(stdout[start : end + 1])
    except json.JSONDecodeError as exc:
        raise GuardConfigurationError(f"cannot parse MSBuild -getItem JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise GuardConfigurationError("MSBuild -getItem output must be a JSON object")
    return value


def _ci(mapping: dict, key: str):
    return next((value for existing, value in mapping.items() if str(existing).lower() == key.lower()), None)


def _active_meta(item: dict, name: str) -> str | None:
    value = _ci(item, name)
    if value is None and isinstance(item.get("Metadata"), dict):
        value = _ci(item["Metadata"], name)
    return None if value is None else str(value)


def _validate_active(edge: Edge, item: dict, label: str, violations: list[str]) -> None:
    roa, oit = _active_meta(item, "ReferenceOutputAssembly"), _active_meta(item, "OutputItemType")
    normalized = "true" if roa is None or not roa.strip() else roa.strip().lower()
    if normalized not in {"true", "false"}:
        violations.append(f"{label}: active ReferenceOutputAssembly is not boolean: {roa!r}")
    elif edge.mode == "assembly" and (normalized != "true" or (oit and oit.strip().lower() == "analyzer")):
        violations.append(f"{label}: active MSBuild reference-mode violation; assembly requires ReferenceOutputAssembly=true and OutputItemType!=Analyzer")
    elif edge.mode == "analyzer" and (normalized != "false" or oit is None or oit.strip().lower() != "analyzer"):
        violations.append(f"{label}: active analyzer-reference metadata violation; OutputItemType=Analyzer and ReferenceOutputAssembly=false are required")


def _audit_active(project_id: str, project: Path, root: Path, policy: Policy, by_path: dict[str, str], dotnet: str, violations: list[str]) -> int:
    try:
        proc = subprocess.run([dotnet, "msbuild", str(project), "-nologo", "-verbosity:quiet", "-getItem:ProjectReference"], cwd=root, check=False, capture_output=True, text=True)
    except OSError as exc:
        raise GuardConfigurationError(f"cannot run {dotnet!r}: {exc}") from exc
    if proc.returncode:
        raise GuardConfigurationError(f"MSBuild evaluation failed for {_display(project, root)} (exit {proc.returncode}): {(proc.stderr or proc.stdout).strip()}")
    items = _json(proc.stdout).get("Items", {})
    refs = items.get("ProjectReference", []) if isinstance(items, dict) else []
    if refs is None:
        refs = []
    if not isinstance(refs, list):
        raise GuardConfigurationError("MSBuild ProjectReference result must be a list")
    for item in refs:
        if not isinstance(item, dict):
            raise GuardConfigurationError("MSBuild ProjectReference item must be an object")
        identity = _ci(item, "Identity") or _ci(item, "EvaluatedInclude")
        if not identity:
            raise GuardConfigurationError("MSBuild ProjectReference item has no Identity")
        target_rel = _repo_path(_target(str(identity), project), root)
        target_id = by_path.get(target_rel or "")
        label = f"{project_id} active ProjectReference {identity!r}"
        if target_id is None:
            violations.append(f"{label}: active MSBuild forbidden reference targets unregistered project {target_rel!r}")
            continue
        edge = policy.edges.get((project_id, target_id))
        if edge is None:
            violations.append(f"{label}: active MSBuild forbidden edge {project_id} -> {target_id}")
            continue
        _validate_active(edge, item, f"{label} ({project_id} -> {target_id}, mode={edge.mode})", violations)
    return len(refs)


def run_guard(root: Path, policy_path: Path, *, dotnet: str = "dotnet", evaluate_active: bool = True) -> GuardResult:
    root, policy_path = root.resolve(), policy_path.resolve()
    policy = load_policy(policy_path)
    production_root = (root / _native(policy.production_root)).resolve()
    if not production_root.is_dir():
        raise GuardConfigurationError(f"production root does not exist: {production_root}")

    registered: dict[str, str] = {}
    by_path: dict[str, str] = {}
    for project_id, raw_path in policy.projects.items():
        normalized = _repo_path(root / _native(raw_path), root)
        if normalized is None:
            raise GuardConfigurationError(f"registered project is outside repository: {raw_path}")
        registered[project_id] = normalized
        if normalized in by_path:
            raise GuardConfigurationError(f"policy registers the same project path more than once: {normalized}")
        by_path[normalized] = project_id

    discovered = {_display(path, root) for path in production_root.glob(policy.production_project_glob) if path.is_file()}
    expected = set(by_path)
    violations = [f"unregistered production project: {path}; add it to policy.projects or move it out of scope" for path in sorted(discovered - expected)]
    violations += [f"registered production project is missing: {path}" for path in sorted(expected - discovered)]

    declarations = active = 0
    for project_id, relative in registered.items():
        project = (root / _native(relative)).resolve()
        if not project.is_file():
            continue
        declarations += _audit_declarations(project_id, project, root, policy, by_path, violations)
        if evaluate_active:
            active += _audit_active(project_id, project, root, policy, by_path, dotnet, violations)
    return GuardResult(len(policy.projects), declarations, active, tuple(violations))


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate production ProjectReference architecture boundaries.")
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[1]), help="repository root")
    parser.add_argument("--policy", default="doc/project-reference-boundaries.yml", help="policy path")
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET_HOST_PATH", "dotnet"), help="dotnet host")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    root = Path(args.root).resolve()
    policy = Path(args.policy)
    if not policy.is_absolute():
        policy = root / policy
    try:
        result = run_guard(root, policy, dotnet=args.dotnet)
    except GuardConfigurationError as exc:
        print(f"Production project-reference guard configuration error: {exc}", file=sys.stderr)
        return 2
    if result.violations:
        print("Production project-reference boundary guard failed:", file=sys.stderr)
        for violation in result.violations:
            print(f"- {violation}", file=sys.stderr)
        return 1
    print(f"Production project-reference boundary guard passed ({result.project_count} projects, {result.declaration_count} declarations, {result.active_count} active references).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
