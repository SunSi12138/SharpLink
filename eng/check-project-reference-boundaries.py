#!/usr/bin/env python3
"""Validate SharpLink production ProjectReference boundaries."""
from __future__ import annotations

import argparse
import copy
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


@dataclass(frozen=True)
class SourceDoc:
    path: Path
    xml: ET.Element
    conditioned: bool


@dataclass(frozen=True)
class PropertyPath:
    value: str
    source: Path
    conditioned: bool


@dataclass(frozen=True)
class SdkLayout:
    kind: str
    props_index: int | None = None
    targets_index: int | None = None


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


def _attribute(element: ET.Element, name: str) -> str | None:
    target = name.lower()
    for key, value in element.attrib.items():
        if key.lower() == target:
            return value
    return None


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


def _parents(xml: ET.Element) -> dict[ET.Element, ET.Element]:
    return {child: parent for parent in xml.iter() for child in parent}


def _under(element: ET.Element, parents: dict[ET.Element, ET.Element], tag: str) -> bool:
    target = tag.lower()
    current = parents.get(element)
    while current is not None:
        if _local(current.tag).lower() == target:
            return True
        current = parents.get(current)
    return False


def _node_conditioned(element: ET.Element, parents: dict[ET.Element, ET.Element], inherited: bool = False) -> bool:
    if inherited:
        return True
    current: ET.Element | None = element
    while current is not None:
        if _attribute(current, "Condition") is not None:
            return True
        if _local(current.tag).lower() in {"when", "otherwise"}:
            return True
        current = parents.get(current)
    return False


def _metadata(element: ET.Element, name: str) -> tuple[bool, str | None, bool]:
    values: list[tuple[str, bool]] = []
    for key, value in element.attrib.items():
        if key.lower() == name.lower():
            values.append((value.strip(), False))
    for child in element:
        if _local(child.tag).lower() == name.lower():
            values.append(((child.text or "").strip(), _attribute(child, "Condition") is not None))
    if not values:
        return False, None, False
    unique = {value for value, _ in values}
    return True, next(iter(unique)) if len(unique) == 1 else None, any(c for _, c in values)


def _mode_names(element: ET.Element) -> list[str]:
    return [name for name in MODE_METADATA if _metadata(element, name)[0]]


def _audit_mode_filters(element: ET.Element, label: str, violations: list[str]) -> None:
    remove = _attribute(element, "RemoveMetadata")
    if remove is not None:
        if any(marker in remove for marker in DYNAMIC_MARKERS):
            violations.append(f"{label}: dynamic RemoveMetadata is denied because it may remove ProjectReference mode metadata")
        else:
            removed = {part.strip().lower() for part in remove.split(";") if part.strip()}
            affected = [name for name in MODE_METADATA if name.lower() in removed]
            if affected:
                violations.append(
                    f"{label}: RemoveMetadata must not remove ProjectReference mode metadata {', '.join(affected)}"
                )

    keep = _attribute(element, "KeepMetadata")
    if keep is not None:
        if any(marker in keep for marker in DYNAMIC_MARKERS):
            violations.append(f"{label}: dynamic KeepMetadata is denied because it cannot prove ProjectReference mode metadata is preserved")
        else:
            kept = {part.strip().lower() for part in keep.split(";") if part.strip()}
            missing = [name for name in MODE_METADATA if name.lower() not in kept]
            if missing:
                violations.append(
                    f"{label}: KeepMetadata must preserve ProjectReference mode metadata {', '.join(missing)}"
                )


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
    result: list[Path] = []
    for part in value.split(";"):
        raw = part.strip()
        if not raw:
            continue
        replacements = {
            "MSBuildThisFileDirectory": str(source.parent.resolve()) + os.sep,
            "MSBuildThisFileFullPath": str(source.resolve()),
            "MSBuildProjectDirectory": str(project.parent.resolve()),
            "MSBuildProjectFullPath": str(project.resolve()),
        }
        unknown = [name for name in PROPERTY_PATTERN.findall(raw) if name not in replacements]
        if unknown:
            violations.append(f"{_display(source, root)}: repository import is not statically traversable: {raw!r}")
            continue
        expanded = raw
        for name, replacement in replacements.items():
            expanded = expanded.replace(f"$({name})", replacement)
        if any(x in expanded for x in ("@(", "%(", "->")):
            violations.append(f"{_display(source, root)}: repository import is not statically traversable: {raw!r}")
            continue
        candidate = Path(_native(expanded))
        if not candidate.is_absolute():
            candidate = source.parent / candidate
        paths = [Path(p) for p in glob.glob(str(candidate), recursive=True)] if any(x in str(candidate) for x in "*?[]") else [candidate]
        for path in paths:
            resolved = path.resolve()
            if _repo_path(resolved, root) is not None and resolved.is_file():
                result.append(resolved)
    return result


def _read_doc(path: Path, root: Path, conditioned: bool) -> SourceDoc:
    try:
        xml = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        raise GuardConfigurationError(f"cannot parse MSBuild XML {_display(path, root)}: {exc}") from exc
    return SourceDoc(path.resolve(), xml, conditioned)


def _import_seeds(doc: SourceDoc, project: Path, root: Path, violations: list[str]) -> list[tuple[Path, bool]]:
    seeds: list[tuple[Path, bool]] = []
    parents = _parents(doc.xml)
    for element in doc.xml.iter():
        if _local(element.tag).lower() != "import":
            continue
        if _under(element, parents, "target"):
            continue
        if _attribute(element, "Sdk") is not None:
            continue
        import_path = _attribute(element, "Project")
        if import_path is None:
            continue
        conditioned = _node_conditioned(element, parents, doc.conditioned)
        for target in _expand_import(import_path, doc.path, project, root, violations):
            seeds.append((target, conditioned))
    return seeds


def _parse_sources(
    initial: Iterable[tuple[Path, bool] | Path | SourceDoc],
    project: Path,
    root: Path,
    violations: list[str],
) -> list[SourceDoc]:
    queue: list[SourceDoc | tuple[Path, bool]] = []
    for item in initial:
        if isinstance(item, SourceDoc):
            queue.append(item)
        elif isinstance(item, Path):
            queue.append((item.resolve(), False))
        else:
            queue.append((item[0].resolve(), item[1]))
    visited: set[tuple[Path, bool]] = set()
    result: list[SourceDoc] = []
    while queue:
        item = queue.pop(0)
        doc = item if isinstance(item, SourceDoc) else _read_doc(item[0], root, item[1])
        state = (doc.path.resolve(), doc.conditioned)
        if state in visited:
            continue
        visited.add(state)
        result.append(doc)
        queue.extend(_import_seeds(doc, project, root, violations))
    return result


def _sdk_import_kind(element: ET.Element) -> str | None:
    if _local(element.tag).lower() != "import" or _attribute(element, "Sdk") is None:
        return None
    raw = (_attribute(element, "Project") or "").strip().replace("\\", "/").lower()
    name = raw.rsplit("/", 1)[-1]
    if name == "sdk.props":
        return "props"
    if name == "sdk.targets":
        return "targets"
    return None


def _sdk_layout(xml: ET.Element, source: Path, root: Path, violations: list[str]) -> SdkLayout:
    if _attribute(xml, "Sdk") is not None or any(_local(child.tag).lower() == "sdk" for child in xml):
        return SdkLayout("implicit")
    children = list(xml)
    props = [i for i, child in enumerate(children) if _sdk_import_kind(child) == "props"]
    targets = [i for i, child in enumerate(children) if _sdk_import_kind(child) == "targets"]
    if not props and not targets:
        return SdkLayout("none")
    if len(props) == 1 and len(targets) == 1 and props[0] < targets[0]:
        return SdkLayout("explicit", props[0], targets[0])
    violations.append(f"{_display(source, root)}: explicit SDK import order is not statically modelable")
    return SdkLayout("ambiguous")


def _slice_sources(
    project: Path,
    xml: ET.Element,
    start: int,
    end: int,
    root: Path,
    violations: list[str],
) -> list[SourceDoc]:
    sliced = ET.Element(xml.tag, dict(xml.attrib))
    for child in list(xml)[start:end]:
        sliced.append(copy.deepcopy(child))
    return _parse_sources([SourceDoc(project.resolve(), sliced, False)], project, root, violations)


def _property_paths(sources: Iterable[SourceDoc], name: str) -> list[PropertyPath]:
    target = name.lower()
    values: list[PropertyPath] = []
    for doc in sources:
        parents = _parents(doc.xml)
        for element in doc.xml.iter():
            if _local(element.tag).lower() != target:
                continue
            parent = parents.get(element)
            if parent is None or _local(parent.tag).lower() != "propertygroup":
                continue
            if _under(element, parents, "target"):
                continue
            values.append(
                PropertyPath(
                    (element.text or "").strip(),
                    doc.path,
                    _node_conditioned(element, parents, doc.conditioned),
                )
            )
    return values


def _expand_property_paths(
    values: Iterable[PropertyPath],
    property_name: str,
    project: Path,
    root: Path,
    violations: list[str],
) -> list[tuple[Path, bool]]:
    result: list[tuple[Path, bool]] = []
    self_ref = f"$({property_name})".lower()
    for item in values:
        for part in item.value.split(";"):
            raw = part.strip()
            if not raw or raw.lower() == self_ref:
                continue
            for path in _expand_import(raw, item.source, project, root, violations):
                result.append((path, item.conditioned))
    return result


def _automatic_or_overrides(
    sources: list[SourceDoc],
    property_name: str,
    default_name: str,
    project: Path,
    root: Path,
    violations: list[str],
) -> list[tuple[Path, bool]]:
    values = _property_paths(sources, property_name)
    result = _expand_property_paths(values, property_name, project, root, violations)
    proven_unconditional_nonempty = bool(values) and all((not value.conditioned) and bool(value.value.strip()) for value in values)
    if not proven_unconditional_nonempty:
        default = _automatic(project, root, default_name)
        if default is not None:
            result.append((default.resolve(), False))
    return result


def _merge_sources(*groups: Iterable[SourceDoc]) -> list[SourceDoc]:
    result: list[SourceDoc] = []
    seen: set[tuple[Path, bool]] = set()
    for group in groups:
        for doc in group:
            state = (doc.path.resolve(), doc.conditioned)
            if state not in seen:
                seen.add(state)
                result.append(doc)
    return result


def _closure(project: Path, root: Path, violations: list[str]) -> list[SourceDoc]:
    project = project.resolve()
    project_doc = _read_doc(project, root, False)
    layout = _sdk_layout(project_doc.xml, project, root, violations)

    if layout.kind == "explicit":
        assert layout.props_index is not None and layout.targets_index is not None
        pre_props_project = _slice_sources(project, project_doc.xml, 0, layout.props_index, root, violations)
        pre_targets_project = _slice_sources(project, project_doc.xml, 0, layout.targets_index, root, violations)
    elif layout.kind == "implicit":
        pre_props_project = []
        pre_targets_project = _parse_sources([project], project, root, violations)
    else:
        pre_props_project = []
        pre_targets_project = _parse_sources([project], project, root, violations)

    before_props_seeds = _expand_property_paths(
        _property_paths(pre_props_project, "CustomBeforeDirectoryBuildProps"),
        "CustomBeforeDirectoryBuildProps",
        project,
        root,
        violations,
    )
    before_props = _parse_sources(before_props_seeds, project, root, violations)
    props_select_context = _merge_sources(pre_props_project, before_props)
    build_props_seeds = _automatic_or_overrides(
        props_select_context,
        "DirectoryBuildPropsPath",
        "Directory.Build.props",
        project,
        root,
        violations,
    )
    build_props = _parse_sources(build_props_seeds, project, root, violations)

    after_props_context = _merge_sources(props_select_context, build_props)
    after_props_seeds = _expand_property_paths(
        _property_paths(after_props_context, "CustomAfterDirectoryBuildProps"),
        "CustomAfterDirectoryBuildProps",
        project,
        root,
        violations,
    )
    after_props = _parse_sources(after_props_seeds, project, root, violations)
    automatic_props = _merge_sources(before_props, build_props, after_props)

    package_context = _merge_sources(pre_props_project, automatic_props)
    package_seeds = _automatic_or_overrides(
        package_context,
        "DirectoryPackagesPropsPath",
        "Directory.Packages.props",
        project,
        root,
        violations,
    )
    packages = _parse_sources(package_seeds, project, root, violations)

    project_phase = _parse_sources([project], project, root, violations)
    target_context = _merge_sources(automatic_props, packages, pre_targets_project)

    before_target_seeds = _expand_property_paths(
        _property_paths(target_context, "CustomBeforeDirectoryBuildTargets"),
        "CustomBeforeDirectoryBuildTargets",
        project,
        root,
        violations,
    )
    before_targets = _parse_sources(before_target_seeds, project, root, violations)
    target_select_context = _merge_sources(target_context, before_targets)
    target_seeds = _automatic_or_overrides(
        target_select_context,
        "DirectoryBuildTargetsPath",
        "Directory.Build.targets",
        project,
        root,
        violations,
    )
    targets = _parse_sources(target_seeds, project, root, violations)

    after_target_context = _merge_sources(target_select_context, targets)
    after_target_seeds = _expand_property_paths(
        _property_paths(after_target_context, "CustomAfterDirectoryBuildTargets"),
        "CustomAfterDirectoryBuildTargets",
        project,
        root,
        violations,
    )
    after_targets = _parse_sources(after_target_seeds, project, root, violations)

    return _merge_sources(project_phase, automatic_props, packages, before_targets, targets, after_targets)


def _references(xml: ET.Element) -> Iterable[tuple[ET.Element, bool, bool]]:
    parents = _parents(xml)
    for element in xml.iter():
        if _local(element.tag).lower() != "projectreference":
            continue
        parent = parents.get(element)
        in_definition = in_target = False
        while parent is not None:
            name = _local(parent.tag).lower()
            in_definition |= name == "itemdefinitiongroup"
            in_target |= name == "target"
            parent = parents.get(parent)
        yield element, in_definition, in_target


def _target(include: str, project: Path) -> Path:
    path = Path(_native(include))
    return (project.parent / path).resolve() if not path.is_absolute() else path.resolve()


def _audit_declarations(project_id: str, project: Path, root: Path, policy: Policy, by_path: dict[str, str], violations: list[str]) -> int:
    count = 0
    closure = _closure(project, root, violations)
    by_source: dict[Path, tuple[ET.Element, bool]] = {}
    for doc in closure:
        existing = by_source.get(doc.path)
        if existing is None:
            by_source[doc.path] = (doc.xml, doc.conditioned)
        else:
            by_source[doc.path] = (existing[0], existing[1] and doc.conditioned)

    for source, (xml, source_conditioned) in by_source.items():
        source_name = _display(source, root)
        imported = source.resolve() != project.resolve()
        parents = _parents(xml)
        for ref, in_definition, in_target in _references(xml):
            mode_names = _mode_names(ref)
            if in_definition:
                if mode_names:
                    violations.append(f"{source_name}: ItemDefinitionGroup must not supply production ProjectReference mode metadata: {', '.join(mode_names)}")
                continue
            update = _attribute(ref, "Update")
            if update is not None:
                update_label = f"{source_name}: ProjectReference Update {update!r}"
                _audit_mode_filters(ref, update_label, violations)
                if mode_names:
                    violations.append(f"{source_name}: ProjectReference Update must not supply/override mode metadata {', '.join(mode_names)} for {update!r}")
                continue
            include = _attribute(ref, "Include")
            if include is None:
                if in_target and _attribute(ref, "Remove") is None:
                    _audit_mode_filters(ref, f"{source_name}: ProjectReference target mutation", violations)
                    if mode_names:
                        violations.append(f"{source_name}: ProjectReference target mutation must not supply/override mode metadata {', '.join(mode_names)}")
                continue

            count += 1
            label = f"{project_id} ProjectReference {include!r} in {source_name}"
            if _dynamic(include):
                violations.append(f"{label}: dynamic/unresolvable production ProjectReference Include is denied; use a literal project path")
                continue
            target_path = _target(include, project)
            target_rel = _repo_path(target_path, root)
            target_id = by_path.get(target_rel or "")
            hidden = " condition-hidden" if _node_conditioned(ref, parents, source_conditioned) else ""
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
    normalized = "true" if roa is None else roa.strip().lower()
    if normalized not in {"true", "false"}:
        violations.append(f"{label}: active ReferenceOutputAssembly is not boolean: {roa!r}")
    elif edge.mode == "assembly" and (normalized != "true" or (oit is not None and oit.strip().lower() == "analyzer")):
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
