"""Shared strict JSON/count/publication primitives only; no 1.73 testcase oracle."""
import json
from pathlib import Path
import re
COUNTER='Expected 0 matching unobserved failure(s), received 1.'  # rejected focused-case negative control


def require(value, label):
    if not value:
        raise ValueError(label)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON key')
        result[key] = value
    return result


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=unique_object,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Nonfinite JSON number')))


def summary(total, failed):
    return dict(total=total, passed=total-failed, failed=failed, skipped=0, cancelled=0, timedOut=0, flaky=0)


def exact_summary(actual, expected):
    require(isinstance(actual, dict) and set(actual) == set(expected), 'Wrong summary fields')
    require(all(type(v) is int for v in actual.values()) and actual == expected, 'Wrong summary counts')


def check_optional_filter(native, selector):
    if 'filter' in native:
        require(native['filter'] == selector, 'Present native filter differs from executed filter')


def check_publication(generation, seen):
    require(isinstance(generation, str) and re.fullmatch('[0-9a-f]{32}', generation) is not None, 'Invalid publication generation')
    require(generation not in seen, 'Repeated publication generation across isolated bodies')
