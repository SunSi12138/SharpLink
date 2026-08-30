#!/usr/bin/env python3
"""Validate SharpLink production ProjectReference boundaries.

The guard performs two complementary checks:
1. A condition-insensitive declaration audit over each production project and its
   repository-owned MSBuild import closure.
2. An active ProjectReference audit using `dotnet msbuild -getItem:ProjectReference`.
"""

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
EXTERNAL_IMPORT_PROPERTIES = {
    "MSBuildToolsPath",
    "MSBuildToolsPath32",
    "MSBuildToolsPath64",
    "MSBuildExtensionsPath",
    "MSBuildExtensionsPath32",
    "MSBuildExtensionsPath64",
    "MSBuildSDKsPath",
    "NuGetPackageRoot",
    "VSToolsPath",
}
PROPERTY_PATTERN = re.compile(r"\$\(([^)]+)\)")


class GuardConfigurationError(RuntimeError):
    pass


@dataclass(frozen=True)
class Edge:
    source: str
    target: str
    mode: str
    temporary: bool = False


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


def _strip_yaml_scalar(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
        return value[1:-1]
    return value


def load_policy(path: Path) -> Policy:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise GuardConfigurationError(f"cannot read policy {path}: {exc}") from exc

    schema_version: int | None = None
    section: str | None = None
    scope: dict[str, str] = {}
    projects: dict[str, str] = {}
    allowed: list[dict[str, str]] = []
    temporary: list[dict[str, str]] = []
    current_edge: dict[str, str] | None = None

    for raw_line in lines:
        if not raw_line.strip() or raw_line.lstrip().startswith("#"):
            continue

        indent = len(raw_line) - len(raw_line.lstrip(" "))
        line = raw_line.strip()

        if indent == 0:
            current_edge = None
            if line.startswith("schema_version:"):
                try:
                    schema_version = int(_strip_yaml_scalar(line.split(":", 1)[1]))
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
            scope[key.strip()] = _strip_yaml_scalar(value)
            continue

        if section == "projects" and indent == 2 and ":" in line:
            key, value = line.split(":", 1)
            project_id = key.strip()
            project_path = _strip_yaml_scalar(value)
            if not project_id or not project_path:
                raise GuardConfigurationError("projects entries require non-empty id and path")
            if project_id in projects:
                raise GuardConfigurationError(f"duplicate project id in policy: {project_id}")
            projects[project_id] = project_path.replace("\\", "/")
            continue

        if section in {"allowed_references", "temporary_exceptions"}:
            destination = allowed if section == "allowed_references" else temporary
            if indent == 2 and line.startswith("- "):
                current_edge = {}
                destination.append(current_edge)
                remainder = line[2:]
                if ":" in remainder:
                    key, value = remainder.split(":", 1)
                    current_edge[key.strip()] = _strip_yaml_scalar(value)
                continue
            if indent >= 4 and current_edge is not None and ":" in line:
                key, value = line.split(":", 1)
                current_edge[key.strip()] = _strip_yaml_scalar(value)
                continue

    if schema_version != 1:
        raise GuardConfigurationError(f"unsupported policy schema_version: {schema_version!r}; expected 1")

    production_root = scope.get("production_root")
    production_glob = scope.get("production_project_glob")
    if not production_root or not production_glob:
        raise GuardConfigurationError("policy scope must define production_root and production_project_glob")
    if not projects:
        raise GuardConfigurationError("policy projects mapping must not be empty")

    edges: dict[tuple[str, str], Edge] = {}
    for entries, is_temporary in ((allowed, False), (temporary, True)):
        for raw in entries:
            source = raw.get("from")
            target = raw.get("to")
            mode = raw.get("mode")
            if not source or not target or mode not in {"assembly", "analyzer"}:
                raise GuardConfigurationError(
                    f"invalid policy edge: expected from/to plus mode assembly|analyzer, got {raw!r}"
                )
            if source not in projects or target not in projects:
                raise GuardConfigurationError(f"policy edge references unknown project: {source} -> {target}")
            key = (source, target)
            if key in edges:
                raise GuardConfigurationError(f"duplicate policy edge: {source} -> {target}")
            edges[key] = Edge(source, target, mode, temporary=is_temporary)

    return Policy(production_root, production_glob, projects, edges)


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _repo_relative(path: Path, repo_root: Path) -> str:
    try:
        return path.resolve().relative_to(repo_root.resolve()).as_posix()
    except ValueError:
        return str(path.resolve())


def _normalized_repo_path(path: Path, repo_root: Path) -> str | None:
    try:
        return path.resolve().relative_to(repo_root.resolve()).as_posix()
    except ValueError:
        return None


def _path_from_msbuild(value: str) -> str:
    return value.replace("\\", os.sep).replace("/", os.sep)


def _is_dynamic_project_reference(include: str) -> bool:
    return (
        not include.strip()
        or any(marker in include for marker in DYNAMIC_MARKERS)
        or any(ch in include for ch in "*?[]")
        or ";" in include
    )


def _metadata_value(element: ET.Element, name: str) -> tuple[bool, str | None, bool]:
    values: list[tuple[str, bool]] = []
    for attr_name, attr_value in element.attrib.items():
        if attr_name.lower() == name.lower():
            values.append((attr_value.strip(), False))
    for child in element:
        if _local_name(child.tag).lower() == name.lower():
            value = (child.text or "").strip()
            conditioned = any(attr.lower() == "condition" for attr in child.attrib)
            values.append((value, conditioned))

    if not values:
        return False, None, False
    unique_values = {value for value, _ in values}
    if len(unique_values) != 1:
        return True, None, any(conditioned for _, conditioned in values)
    return True, values[0][0], any(conditioned for _, conditioned in values)


def _validate_mode(
    edge: Edge,
    reference: ET.Element,
    source_label: str,
    violations: list[str],
) -> None:
    metadata: dict[str, str | None] = {}
    for name in MODE_METADATA:
        present, value, conditioned = _metadata_value(reference, name)
        if not present:
            metadata[name] = None
            continue
        if value is None:
            violations.append(f"{source_label}: conflicting {name} metadata makes reference mode ambiguous")
            metadata[name] = None
            continue
        if conditioned:
            violations.append(f"{source_label}: {name} must not have a Condition")
        if not value:
            violations.append(f"{source_label}: {name} must not be empty")
        if any(marker in value for marker in DYNAMIC_MARKERS):
            violations.append(f"{source_label}: {name} must be a literal value, got {value!r}")
        metadata[name] = value

    roa = metadata["ReferenceOutputAssembly"]
    oit = metadata["OutputItemType"]
    normalized_roa: str | None
    if roa is None:
        normalized_roa = "true"
    elif roa.lower() in {"true", "false"}:
        normalized_roa = roa.lower()
    else:
        normalized_roa = None
        violations.append(f"{source_label}: ReferenceOutputAssembly must be true or false, got {roa!r}")

    if edge.mode == "assembly":
        if normalized_roa != "true":
            violations.append(
                f"{source_label}: reference-mode violation; assembly edge requires ReferenceOutputAssembly=true"
            )
        if oit is not None and oit.lower() == "analyzer":
            violations.append(
                f"{source_label}: reference-mode violation; assembly edge must not use OutputItemType=Analyzer"
            )
    elif edge.mode == "analyzer":
        if normalized_roa != "false":
            violations.append(
                f"{source_label}: analyzer-reference metadata violation; ReferenceOutputAssembly=false is required"
            )
        if oit is None or oit.lower() != "analyzer":
            violations.append(
                f"{source_label}: analyzer-reference metadata violation; OutputItemType=Analyzer is required"
            )


def _mode_metadata_names(element: ET.Element) -> list[str]:
    found: list[str] = []
    for name in MODE_METADATA:
        present, _, _ = _metadata_value(element, name)
        if present:
            found.append(name)
    return found


def _automatic_build_file(project_file: Path, repo_root: Path, name: str) -> Path | None:
    current = project_file.parent.resolve()
    root = repo_root.resolve()
    while True:
        candidate = current / name
        if candidate.is_file():
            return candidate
        if current == root:
            return None
        if root not in current.parents:
            return None
        current = current.parent


def _expand_static_import(
    value: str,
    importing_file: Path,
    project_file: Path,
    repo_root: Path,
    violations: list[str],
) -> list[Path]:
    raw = value.strip()
    if not raw:
        return []

    replacements = {
        "MSBuildThisFileDirectory": str(importing_file.parent.resolve()) + os.sep,
        "MSBuildThisFileFullPath": str(importing_file.resolve()),
        "MSBuildProjectDirectory": str(project_file.parent.resolve()),
        "MSBuildProjectFullPath": str(project_file.resolve()),
    }

    property_names = PROPERTY_PATTERN.findall(raw)
    unknown = [name for name in property_names if name not in replacements]
    if unknown:
        if all(name in EXTERNAL_IMPORT_PROPERTIES for name in unknown):
            return []
        violations.append(
            f"{_repo_relative(importing_file, repo_root)}: repository import is not statically traversable: {raw!r}"
        )
        return []

    expanded = raw
    for name, replacement in replacements.items():
        expanded = expanded.replace(f"$({name})", replacement)
    if any(marker in expanded for marker in ("@(", "%(", "->")) or ";" in expanded:
        violations.append(
            f"{_repo_relative(importing_file, repo_root)}: repository import is not statically traversable: {raw!r}"
        )
        return []

    expanded = _path_from_msbuild(expanded)
    candidate = Path(expanded)
    if not candidate.is_absolute():
        candidate = importing_file.parent / candidate

    wildcard = any(ch in str(candidate) for ch in "*?[]")
    candidates = [Path(match) for match in glob.glob(str(candidate), recursive=True)] if wildcard else [candidate]
    result: list[Path] = []
    for item in candidates:
        resolved = item.resolve()
        if _normalized_repo_path(resolved, repo_root) is None:
            continue
        if resolved.is_file():
            result.append(resolved)
    return result


def _declaration_closure(
    project_file: Path,
    repo_root: Path,
    violations: list[str],
) -> list[tuple[Path, ET.Element]]:
    queue: list[Path] = [project_file.resolve()]
    for name in ("Directory.Build.props", "Directory.Build.targets"):
        automatic = _automatic_build_file(project_file, repo_root, name)
        if automatic is not None:
            queue.append(automatic.resolve())

    seen: set[Path] = set()
    closure: list[tuple[Path, ET.Element]] = []
    while queue:
        path = queue.pop(0).resolve()
        if path in seen:
            continue
        seen.add(path)
        try:
            root = ET.parse(path).getroot()
        except (OSError, ET.ParseError) as exc:
            raise GuardConfigurationError(f"cannot parse MSBuild XML {_repo_relative(path, repo_root)}: {exc}") from exc
        closure.append((path, root))

        for element in root.iter():
            if _local_name(element.tag) != "Import":
                continue
            project = element.attrib.get("Project")
            if project is None:
                continue
            queue.extend(_expand_static_import(project, path, project_file, repo_root, violations))

    return closure


def _iter_project_references(root: ET.Element) -> Iterable[tuple[ET.Element, bool]]:
    parent_map = {child: parent for parent in root.iter() for child in parent}
    for element in root.iter():
        if _local_name(element.tag) != "ProjectReference":
            continue
        current = parent_map.get(element)
        in_item_definition = False
        while current is not None:
            if _local_name(current.tag) == "ItemDefinitionGroup":
                in_item_definition = True
                break
            current = parent_map.get(current)
        yield element, in_item_definition


def _resolve_reference_target(include: str, project_file: Path) -> Path:
    candidate = Path(_path_from_msbuild(include))
    if not candidate.is_absolute():
        candidate = project_file.parent / candidate
    return candidate.resolve()


def _audit_declarations(
    project_id: str,
    project_file: Path,
    repo_root: Path,
    policy: Policy,
    project_by_path: dict[str, str],
    violations: list[str],
) -> int:
    count = 0
    for declaration_file, root in _declaration_closure(project_file, repo_root, violations):
        declaration_rel = _repo_relative(declaration_file, repo_root)
        imported = declaration_file.resolve() != project_file.resolve()

        for reference, in_item_definition in _iter_project_references(root):
            mode_metadata = _mode_metadata_names(reference)
            if in_item_definition:
                if mode_metadata:
                    violations.append(
                        f"{declaration_rel}: ItemDefinitionGroup must not supply production ProjectReference mode metadata: "
                        + ", ".join(mode_metadata)
                    )
                continue

            update = reference.attrib.get("Update")
            if update is not None:
                if mode_metadata:
                    violations.append(
                        f"{declaration_rel}: ProjectReference Update must not supply/override mode metadata "
                        + ", ".join(mode_metadata)
                        + f" for {update!r}"
                    )
                continue

            include = reference.attrib.get("Include")
            if include is None:
                continue
            count += 1
            condition = reference.attrib.get("Condition", "").strip()
            label = f"{project_id} ProjectReference {include!r} in {declaration_rel}"

            if _is_dynamic_project_reference(include):
                violations.append(
                    f"{label}: dynamic/unresolvable production ProjectReference Include is denied; use a literal project path"
                )
                continue

            target_path = _resolve_reference_target(include, project_file)
            target_rel = _normalized_repo_path(target_path, repo_root)
            target_id = project_by_path.get(target_rel or "")
            if target_id is None:
                kind = "imported forbidden reference" if imported else "forbidden production edge"
                hidden = " condition-hidden" if condition else ""
                target_display = target_rel or str(target_path)
                violations.append(
                    f"{label}:{hidden} {kind}; target {target_display!r} is not a registered production project"
                )
                continue

            edge = policy.edges.get((project_id, target_id))
            if edge is None:
                kind = "imported forbidden reference" if imported else "forbidden production edge"
                hidden = " condition-hidden" if condition else ""
                violations.append(f"{label}:{hidden} {kind} {project_id} -> {target_id} is not authorized by policy")
                continue

            _validate_mode(edge, reference, f"{label} ({project_id} -> {target_id}, mode={edge.mode})", violations)

    return count


def _extract_json(stdout: str) -> dict:
    start = stdout.find("{")
    end = stdout.rfind("}")
    if start < 0 or end < start:
        raise GuardConfigurationError("MSBuild -getItem output did not contain JSON")
    try:
        value = json.loads(stdout[start : end + 1])
    except json.JSONDecodeError as exc:
        raise GuardConfigurationError(f"cannot parse MSBuild -getItem JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise GuardConfigurationError("MSBuild -getItem output must be a JSON object")
    return value


def _ci_get(mapping: dict, key: str):
    for existing, value in mapping.items():
        if str(existing).lower() == key.lower():
            return value
    return None


def _active_metadata(item: dict, name: str) -> str | None:
    direct = _ci_get(item, name)
    if direct is not None:
        return str(direct)
    nested = item.get("Metadata")
    if isinstance(nested, dict):
        value = _ci_get(nested, name)
        if value is not None:
            return str(value)
    return None


def _validate_active_mode(edge: Edge, item: dict, label: str, violations: list[str]) -> None:
    roa = _active_metadata(item, "ReferenceOutputAssembly")
    oit = _active_metadata(item, "OutputItemType")
    normalized_roa = "true" if roa is None or not roa.strip() else roa.strip().lower()

    if normalized_roa not in {"true", "false"}:
        violations.append(f"{label}: active ReferenceOutputAssembly is not boolean: {roa!r}")
        return
    if edge.mode == "assembly":
        if normalized_roa != "true" or (oit is not None and oit.strip().lower() == "analyzer"):
            violations.append(
                f"{label}: active MSBuild reference-mode violation; assembly requires ReferenceOutputAssembly=true and OutputItemType!=Analyzer"
            )
    else:
        if normalized_roa != "false" or oit is None or oit.strip().lower() != "analyzer":
            violations.append(
                f"{label}: active analyzer-reference metadata violation; OutputItemType=Analyzer and ReferenceOutputAssembly=false are required"
            )


def _audit_active_items(
    project_id: str,
    project_file: Path,
    repo_root: Path,
    policy: Policy,
    project_by_path: dict[str, str],
    dotnet: str,
    violations: list[str],
) -> int:
    try:
        completed = subprocess.run(
            [dotnet, "msbuild", str(project_file), "-nologo", "-verbosity:quiet", "-getItem:ProjectReference"],
            cwd=repo_root,
            check=False,
            capture_output=True,
            text=True,
        )
    except OSError as exc:
        raise GuardConfigurationError(f"cannot run {dotnet!r}: {exc}") from exc

    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout).strip()
        raise GuardConfigurationError(
            f"MSBuild evaluation failed for {_repo_relative(project_file, repo_root)} (exit {completed.returncode}): {detail}"
        )

    payload = _extract_json(completed.stdout)
    items_container = payload.get("Items", {})
    raw_items = items_container.get("ProjectReference", []) if isinstance(items_container, dict) else []
    if raw_items is None:
        raw_items = []
    if not isinstance(raw_items, list):
        raise GuardConfigurationError("MSBuild ProjectReference result must be a list")

    count = 0
    for raw_item in raw_items:
        if not isinstance(raw_item, dict):
            raise GuardConfigurationError("MSBuild ProjectReference item must be an object")
        identity = _ci_get(raw_item, "Identity") or _ci_get(raw_item, "EvaluatedInclude")
        if not identity:
            raise GuardConfigurationError("MSBuild ProjectReference item has no Identity")
        count += 1
        target_path = _resolve_reference_target(str(identity), project_file)
        target_rel = _normalized_repo_path(target_path, repo_root)
        target_id = project_by_path.get(target_rel or "")
        label = f"{project_id} active ProjectReference {identity!r}"
        if target_id is None:
            violations.append(
                f"{label}: active MSBuild forbidden reference targets unregistered project {target_rel or str(target_path)!r}"
            )
            continue
        edge = policy.edges.get((project_id, target_id))
        if edge is None:
            violations.append(f"{label}: active MSBuild forbidden edge {project_id} -> {target_id}")
            continue
        _validate_active_mode(edge, raw_item, f"{label} ({project_id} -> {target_id}, mode={edge.mode})", violations)

    return count


def run_guard(
    repo_root: Path,
    policy_path: Path,
    *,
    dotnet: str = "dotnet",
    evaluate_active: bool = True,
) -> GuardResult:
    repo_root = repo_root.resolve()
    policy_path = policy_path.resolve()
    policy = load_policy(policy_path)

    production_root = (repo_root / _path_from_msbuild(policy.production_root)).resolve()
    if not production_root.is_dir():
        raise GuardConfigurationError(f"production root does not exist: {production_root}")

    registered_paths = {project_id: path.replace("\\", "/") for project_id, path in policy.projects.items()}
    project_by_path = {path: project_id for project_id, path in registered_paths.items()}
    if len(project_by_path) != len(registered_paths):
        raise GuardConfigurationError("policy registers the same project path more than once")

    discovered = {
        _repo_relative(path, repo_root)
        for path in production_root.glob(policy.production_project_glob)
        if path.is_file() and "bin" not in path.parts and "obj" not in path.parts
    }
    registered = set(project_by_path)
    violations: list[str] = []
    for path in sorted(discovered - registered):
        violations.append(f"unregistered production project: {path}; add it to policy.projects or move it out of scope")
    for path in sorted(registered - discovered):
        violations.append(f"registered production project is missing: {path}")

    declaration_count = 0
    active_count = 0
    for project_id, relative_path in policy.projects.items():
        project_file = (repo_root / _path_from_msbuild(relative_path)).resolve()
        if not project_file.is_file():
            continue
        declaration_count += _audit_declarations(
            project_id, project_file, repo_root, policy, project_by_path, violations
        )
        if evaluate_active:
            active_count += _audit_active_items(
                project_id, project_file, repo_root, policy, project_by_path, dotnet, violations
            )

    return GuardResult(len(policy.projects), declaration_count, active_count, tuple(violations))


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate production ProjectReference architecture boundaries.")
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[1]), help="repository root")
    parser.add_argument(
        "--policy",
        default="doc/project-reference-boundaries.yml",
        help="policy path, relative to --root by default",
    )
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET_HOST_PATH", "dotnet"), help="dotnet host")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    repo_root = Path(args.root).resolve()
    policy_path = Path(args.policy)
    if not policy_path.is_absolute():
        policy_path = repo_root / policy_path

    try:
        result = run_guard(repo_root, policy_path, dotnet=args.dotnet)
    except GuardConfigurationError as exc:
        print(f"Production project-reference guard configuration error: {exc}", file=sys.stderr)
        return 2

    if result.violations:
        print("Production project-reference boundary guard failed:", file=sys.stderr)
        for violation in result.violations:
            print(f"- {violation}", file=sys.stderr)
        return 1

    print(
        "Production project-reference boundary guard passed "
        f"({result.project_count} projects, {result.declaration_count} declarations, {result.active_count} active references)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
