#!/usr/bin/env python3
"""Verify the response-schema manifest against the real, CI-captured OpenAPI document.

Usage: verify-openapi-fixture.py <real-openapi-document.json> <response-schema-manifest.json>

Part of openapi-response-schemas (Platform/ADR-0035). The manifest
(openspec/changes/openapi-response-schemas/fixtures/response-schema-manifest.v1.json) is the
golden, cross-repo contract: per consumer group, the EMITTED components/schemas name plus the
verbatim field names Platform surfaces for every wire shape the Platform.Web typed-client consumes.
This check is the CI guard that the emitted document still carries every named schema with the exact
field names the manifest (and the downstream Web children) cite — replacing the earlier one-off,
single-`CsatResponseDto` eyeball comparison (design D4). It generalises the original check: the
`csat` group's `CsatResponseDto` entry subsumes the previous hard-coded fragment.

For every group in the manifest and every `SchemaName: [field, ...]` under it, asserts:
  - `components.schemas.<SchemaName>` exists in the real document, and
  - its property NAMES equal the manifest's field list EXACTLY (a name in one but not the other
    fails — the csat-runner incident, Web PR#159, was a hand-transcribed field-name drift).

Plus a document-wide numeric-truth assertion (openapi-numeric-schema-truth, Platform/ADR-0036):
NO schema in the captured document may declare a numeric+`string` union type
(`["integer","string"]` / `["number","string"]`, in any order). Before ADR-0036 the .NET 10
`JsonSchemaExporter` emitted these unions for every numeric and this check merely TOLERATED them
(field names only). The `NumericSchemaTruthTransformer` now strips the spurious `string` arm at the
source, so the tolerance is upgraded to an enforced invariant: any reappearing union (e.g. a
transformer regression or a hand-added `["number","string"]`) fails CI here. Nullable numerics
(OpenAPI 3.1 `["null","integer"]` / `["null","number"]`) are LEGITIMATE and pass — only the `string`
arm alongside a numeric is the artifact.

Exit 0 on full match, 1 on any missing schema, field-name mismatch, or surviving numeric+string
union; 2 on usage/IO error.
"""
import json
import sys

# openapi-numeric-schema-truth (Platform/ADR-0036): a numeric type array is the artifact IFF it
# pairs a numeric member with "string". Nullable numerics ("null" + numeric) are legitimate.
_NUMERIC_MEMBERS = frozenset({"integer", "number"})


def _load(path: str):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _find_numeric_string_unions(node, path: str, out: list[str]) -> None:
    """Recursively collect JSON-pointer-ish paths of every schema whose `type` is a
    numeric+string union (a numeric member together with "string")."""
    if isinstance(node, dict):
        type_val = node.get("type")
        if isinstance(type_val, list):
            members = set(type_val)
            if "string" in members and members & _NUMERIC_MEMBERS:
                out.append(f"{path or '<root>'}: type={type_val}")
        for key, value in node.items():
            _find_numeric_string_unions(value, f"{path}.{key}" if path else key, out)
    elif isinstance(node, list):
        for i, value in enumerate(node):
            _find_numeric_string_unions(value, f"{path}[{i}]", out)


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <real-openapi-document.json> <response-schema-manifest.json>",
              file=sys.stderr)
        return 2

    real_path, manifest_path = sys.argv[1], sys.argv[2]
    try:
        real_doc = _load(real_path)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::error::Failed to read/parse real document {real_path}: {exc}", file=sys.stderr)
        return 2
    try:
        manifest = _load(manifest_path)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::error::Failed to read/parse manifest {manifest_path}: {exc}", file=sys.stderr)
        return 2

    real_schemas = real_doc.get("components", {}).get("schemas", {})
    groups = manifest.get("groups", {})
    if not groups:
        print(f"::error::manifest {manifest_path} has no 'groups' — malformed.", file=sys.stderr)
        return 1

    errors: list[str] = []
    checked_schemas = 0
    checked_fields = 0

    for group_name, group in groups.items():
        status = group.get("status")
        schemas = group.get("schemas", {})
        if status == "TO-COMPLETE-BY-HOST":
            errors.append(f"group '{group_name}' is still TO-COMPLETE-BY-HOST — manifest incomplete")
            continue
        if not schemas:
            errors.append(f"group '{group_name}' has no schemas (status={status!r})")
            continue
        for schema_name, fields in schemas.items():
            checked_schemas += 1
            real_schema = real_schemas.get(schema_name)
            if real_schema is None:
                errors.append(f"[{group_name}] schema '{schema_name}' MISSING from the captured "
                              f"document (endpoint removed/renamed or response type changed)")
                continue
            real_props = set(real_schema.get("properties", {}).keys())
            manifest_fields = set(fields)
            checked_fields += len(manifest_fields)
            for missing in sorted(manifest_fields - real_props):
                errors.append(f"[{group_name}] '{schema_name}.{missing}' in manifest but MISSING "
                              f"from the real document")
            for extra in sorted(real_props - manifest_fields):
                errors.append(f"[{group_name}] '{schema_name}.{extra}' in the real document but "
                              f"NOT in the manifest (manifest is stale)")

    # openapi-numeric-schema-truth (Platform/ADR-0036): assert no numeric+string union survives
    # anywhere in the document. The NumericSchemaTruthTransformer must have stripped every one.
    unions: list[str] = []
    _find_numeric_string_unions(real_doc.get("components", {}).get("schemas", {}), "components.schemas", unions)
    for u in unions:
        errors.append(f"[numeric-truth] surviving numeric+string union (ADR-0036) — {u}")

    if errors:
        print("::error::response-schema manifest verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    print(f"OK: response-schema manifest matches the captured document "
          f"({checked_schemas} schemas / {checked_fields} field names across {len(groups)} groups); "
          f"no numeric+string union survives (ADR-0036 numeric-truth invariant holds).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
