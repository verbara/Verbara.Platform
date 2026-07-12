#!/usr/bin/env python3
"""Verify the golden OpenAPI fixture's CsatResponseDto fragment against the real document.

Usage: verify-openapi-fixture.py <real-openapi-document.json> <fixture.json>

Part of the openapi-typed-client change (Platform/ADR-0035): the fixture
(fixtures/openapi-document.v1.sample.json in the change directory) is a hand-curated golden
ENVELOPE sample whose CsatResponseDto schema field names must stay verbatim with the real,
CI-captured Platform OpenAPI document (openapi/openapi-export capability spec, requirement
"The golden fixture is verified against the real emitted document"). This is the repeatable
check that replaces the one-off eyeball comparison performed at propose-time — see
openspec/changes/openapi-typed-client/design.md D3.

Compares ONLY the `components.schemas.CsatResponseDto` fragment:
  - Field NAMES must match exactly (a name present in one schema and missing from the other
    fails the check) — this is the field the csat-runner incident (Web PR#159, v3.13.1-web) got
    wrong by hand-transcribing the DTO instead of generating from the real contract.
  - Numeric/date-time FORMATS are illustrative in the fixture per its own documented intent
    (impact.yaml) and are NOT compared — e.g. the fixture's `totalResponses` uses
    `{"type": "integer", "format": "int64"}` while .NET 10's OpenAPI generator currently emits a
    `{"type": ["integer", "string"], "format": "int32", "pattern": "..."}` union for the same
    C# `int` property; both describe the same field, so format-level literalism would make this
    check re-fail on every unrelated .NET/ASP.NET Core servicing update. TYPE families (string vs
    number vs integer, ignoring the int/string big-number union) are still compared.

Exit code 0 on match, 1 on any field-name mismatch or missing schema, 2 on usage/IO error.
"""
import json
import sys

REQUIRED_FIELDS = (
    "queueName",
    "channel",
    "totalResponses",
    "averageRating",
    "rangeStart",
    "rangeEnd",
)

# Base JSON Schema "type" families the union-typed 2020-12 schemas (.NET 10 emits
# ["integer", "string"] / ["number", "string"] for some numeric properties, see module
# docstring) may present as; the fixture uses the plain singular form.
_TYPE_FAMILY = {
    "integer": {"integer", "string"},
    "number": {"number", "string"},
    "string": {"string"},
}


def _type_set(schema_type) -> set[str]:
    if isinstance(schema_type, list):
        return set(schema_type)
    return {schema_type}


def _families_compatible(fixture_type: str, real_types: set[str]) -> bool:
    allowed = _TYPE_FAMILY.get(fixture_type, {fixture_type})
    return bool(allowed & real_types)


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <real-openapi-document.json> <fixture.json>", file=sys.stderr)
        return 2

    real_path, fixture_path = sys.argv[1], sys.argv[2]

    try:
        with open(real_path, encoding="utf-8") as f:
            real_doc = json.load(f)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::error::Failed to read/parse real document {real_path}: {exc}", file=sys.stderr)
        return 2

    try:
        with open(fixture_path, encoding="utf-8") as f:
            fixture_doc = json.load(f)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::error::Failed to read/parse fixture {fixture_path}: {exc}", file=sys.stderr)
        return 2

    real_schema = real_doc.get("components", {}).get("schemas", {}).get("CsatResponseDto")
    fixture_schema = fixture_doc.get("components", {}).get("schemas", {}).get("CsatResponseDto")

    if real_schema is None:
        print("::error::CsatResponseDto schema missing from the real captured document — "
              "the /api/v{version}/analytics/csat/queues/{queueId} endpoint may have been "
              "removed, renamed, or its response type changed.", file=sys.stderr)
        return 1
    if fixture_schema is None:
        print(f"::error::CsatResponseDto schema missing from fixture {fixture_path} — "
              "the fixture is malformed.", file=sys.stderr)
        return 1

    real_props = real_schema.get("properties", {})
    fixture_props = fixture_schema.get("properties", {})

    errors: list[str] = []

    for field in REQUIRED_FIELDS:
        if field not in real_props:
            errors.append(f"'{field}' present in fixture but MISSING from the real document")
        if field not in fixture_props:
            errors.append(f"'{field}' present in the real document but MISSING from the fixture")

    # Field-name completeness in both directions (catches additions/removals beyond the
    # documented 6, since the fixture's whole purpose is field-name fidelity).
    extra_in_real = set(real_props) - set(fixture_props)
    extra_in_fixture = set(fixture_props) - set(real_props)
    for field in sorted(extra_in_real):
        errors.append(f"'{field}' is in the real document but not in the fixture (fixture is stale)")
    for field in sorted(extra_in_fixture):
        errors.append(f"'{field}' is in the fixture but not in the real document (endpoint contract changed)")

    # Type-family check (not exact format — see module docstring).
    for field in REQUIRED_FIELDS:
        if field not in real_props or field not in fixture_props:
            continue  # already reported above
        fixture_type = fixture_props[field].get("type")
        real_types = _type_set(real_props[field].get("type"))
        if isinstance(fixture_type, str) and not _families_compatible(fixture_type, real_types):
            errors.append(
                f"'{field}' type family mismatch: fixture={fixture_type!r} real={sorted(real_types)!r}"
            )

    if errors:
        print("::error::CsatResponseDto fixture verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    print(f"OK: CsatResponseDto fixture matches the real document for all {len(REQUIRED_FIELDS)} fields "
          f"({', '.join(REQUIRED_FIELDS)}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
