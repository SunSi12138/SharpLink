#!/usr/bin/env python3
"""Offline, structural-only validator for the #728 six-direction evidence ledger.

Schema v1 (unknown keys are errors):
  root: schemaVersion=1, targetSourceCommit=TARGET_SOURCE_COMMIT, directions=[...]
  row: issueId, decision, sourceCommit, candidateCommit (commit or null), axes
  axis: state, assessment (nonempty text), currentPass (bool), evidence=[...]
  evidence: url (absolute HTTPS), scope (current/historical/context),
            currentEvidence (bool), sourceCommit (optional full Git SHA-1)

Exactly issues 729..734 and all AXES are required. Source and candidate identities
are separate assertions, not verified Git ancestry. Current evidence must identify
the pinned target source. Historical/context evidence cannot be relabeled current.
A currentPass assertion requires an observed axis and eligible current evidence
on a row whose source is current. It is NOT derived from a go decision.

Success means structural completeness and provenance-label consistency only.
This tool neither reads URLs nor verifies claims, commits, performance, acceptance
thresholds, API/ABI compatibility, or the truth of a currentPass assertion.
In particular, no 2.0.3 compatibility gate or performance threshold is imposed.
New 3.0 API/ABI and code-size assessment is recorded, not silently waived.

CLI: python3 validator.py ledger.json
Exit 0 = structurally valid, 1 = invalid/input failure, 2 = usage error.
Stdout is a compact JSON object containing only whitelisted keys and error codes.
It never echoes a path, input value, URL, exception, or a product all-pass verdict.
"""

import json
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit


TARGET_SOURCE_COMMIT = "8a13b12e799bb70a989eacac0c1b4cea22ab80ae"
ISSUE_IDS = frozenset(range(729, 735))
AXES = (
    "characterization", "staticFacts", "runtimeRechecks", "jit", "nativeAot",
    "codeSize", "abiMaintenance", "semantics",
)
DECISIONS = frozenset({"qualified-go", "no-go", "open", "selected-local-go"})
STATES = frozenset({"observed", "not-run", "partial", "not-applicable"})
SCOPES = frozenset({"current", "historical", "context"})
ROOT_KEYS = frozenset({"schemaVersion", "targetSourceCommit", "directions"})
ROW_KEYS = frozenset({"issueId", "decision", "sourceCommit", "candidateCommit", "axes"})
AXIS_KEYS = frozenset({"state", "assessment", "currentPass", "evidence"})
EVIDENCE_REQUIRED = frozenset({"url", "scope", "currentEvidence"})
EVIDENCE_ALLOWED = EVIDENCE_REQUIRED | {"sourceCommit"}
MAX_INPUT_BYTES = 1024 * 1024
COMMIT_RE = re.compile(r"[0-9a-f]{40}\Z")


def is_commit(value):
    return type(value) is str and COMMIT_RE.fullmatch(value) is not None


def is_https_url(value):
    """Check syntax only. Never resolve DNS or fetch evidence."""
    if (type(value) is not str or not value or "\\" in value
            or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in value)):
        return False
    try:
        parsed = urlsplit(value)
        # Accessing port also rejects invalid/out-of-range port strings.
        port = parsed.port
        return bool(
            parsed.scheme == "https" and parsed.hostname
            and parsed.username is None and parsed.password is None
            and (port is None or 1 <= port <= 65535)
        )
    except ValueError:
        return False


def _enum(value, choices):
    return type(value) is str and value in choices


def validate(ledger):
    """Return stable, input-independent error codes; [] is NOT a truth verdict."""
    errors = set()
    if type(ledger) is not dict:
        return ["root_shape"]
    if ledger.keys() != ROOT_KEYS:
        errors.add("root_keys")
    if type(ledger.get("schemaVersion")) is not int or ledger["schemaVersion"] != 1:
        errors.add("schema_version")
    target = ledger.get("targetSourceCommit")
    if not is_commit(target):
        errors.add("target_commit")
    elif target != TARGET_SOURCE_COMMIT:
        errors.add("target_mismatch")

    rows = ledger.get("directions")
    if type(rows) is not list:
        errors.add("directions_shape")
        return sorted(errors)
    if len(rows) != 6:
        errors.add("direction_count")
    seen = set()
    for row in rows:
        if type(row) is not dict:
            errors.add("row_shape")
            continue
        if row.keys() != ROW_KEYS:
            errors.add("row_keys")
        issue_id = row.get("issueId")
        if type(issue_id) is not int or issue_id not in ISSUE_IDS:
            errors.add("issue_id")
        else:
            if issue_id in seen:
                errors.add("duplicate_issue")
            seen.add(issue_id)
        if not _enum(row.get("decision"), DECISIONS):
            errors.add("decision")
        source = row.get("sourceCommit")
        if not is_commit(source):
            errors.add("source_commit")
        candidate = row.get("candidateCommit")
        if candidate is not None and not is_commit(candidate):
            errors.add("candidate_commit")
        axes = row.get("axes")
        if type(axes) is not dict:
            errors.add("axes_shape")
            continue
        if axes.keys() != set(AXES):
            errors.add("axes_keys")
        for name in AXES:
            axis = axes.get(name)
            if type(axis) is not dict:
                errors.add("axis_shape")
                continue
            if axis.keys() != AXIS_KEYS:
                errors.add("axis_keys")
            state = axis.get("state")
            if not _enum(state, STATES):
                errors.add("assessment_state")
            assessment = axis.get("assessment")
            if type(assessment) is not str or not assessment.strip():
                errors.add("assessment_text")
            current_pass = axis.get("currentPass")
            if type(current_pass) is not bool:
                errors.add("current_pass_boolean")
            evidence = axis.get("evidence")
            if type(evidence) is not list:
                errors.add("evidence_shape")
                continue
            if _enum(state, {"observed", "partial"}) and not evidence:
                errors.add("observation_without_evidence")
            eligible_current = False
            for entry in evidence:
                if type(entry) is not dict:
                    errors.add("evidence_entry_shape")
                    continue
                if not EVIDENCE_REQUIRED <= entry.keys() <= EVIDENCE_ALLOWED:
                    errors.add("evidence_keys")
                url_ok = is_https_url(entry.get("url"))
                if not url_ok:
                    errors.add("evidence_url")
                scope = entry.get("scope")
                if not _enum(scope, SCOPES):
                    errors.add("evidence_scope")
                flag = entry.get("currentEvidence")
                if type(flag) is not bool:
                    errors.add("current_evidence_boolean")
                evidence_source = entry.get("sourceCommit")
                if "sourceCommit" in entry and not is_commit(evidence_source):
                    errors.add("evidence_commit")
                # The scope and explicit current flag must agree in both directions.
                if (scope == "current" and flag is not True) or (
                    flag is True and scope != "current"
                ):
                    errors.add("current_evidence_scope")
                if (scope == "current" or flag is True) and (
                    not is_commit(evidence_source) or evidence_source != target
                ):
                    errors.add("current_evidence_source")
                eligible_current |= (
                    scope == "current" and flag is True and url_ok
                    and is_commit(evidence_source) and evidence_source == target
                )
            if current_pass is True:
                if state != "observed":
                    errors.add("current_pass_state")
                if not eligible_current:
                    errors.add("current_pass_without_current_evidence")
                if source != target:
                    errors.add("current_pass_source")
    if seen != ISSUE_IDS:
        errors.add("issue_set")
    return sorted(errors)


class DuplicateKeyError(ValueError):
    pass


def _unique_object(pairs):
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise DuplicateKeyError()
        obj[key] = value
    return obj


def _reject_nonfinite(_value):
    raise ValueError()


def result(errors):
    """Only these fields may be printed, even for valid-but-fabricated claims."""
    return {
        "structurallyValid": not errors,
        "semanticTruthVerified": False,
        "evidenceContentsVerified": False,
        "errors": errors,
    }


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    status = 1
    if len(argv) != 1:
        errors = ["usage"]
        status = 2
    else:
        try:
            with Path(argv[0]).open("rb") as handle:
                raw = handle.read(MAX_INPUT_BYTES + 1)
            if len(raw) > MAX_INPUT_BYTES:
                errors = ["input_too_large"]
            else:
                ledger = json.loads(
                    raw.decode("utf-8"), object_pairs_hook=_unique_object,
                    parse_constant=_reject_nonfinite,
                )
                errors = validate(ledger)
                status = 1 if errors else 0
        except DuplicateKeyError:
            errors = ["duplicate_json_key"]
        except (UnicodeError, ValueError, RecursionError):
            errors = ["malformed_json"]
        except OSError:
            errors = ["input_unreadable"]
    print(json.dumps(result(errors), separators=(",", ":"), sort_keys=True))
    return status


if __name__ == "__main__":
    raise SystemExit(main())
