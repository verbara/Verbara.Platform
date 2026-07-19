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

Field names only: the manifest records the verbatim camelCase names Web types against. JSON types
/ numeric-format literalism are intentionally NOT compared (the .NET 10 OpenAPI generator emits
`["integer","string"]`-style unions for some numerics; comparing them would re-fail this check on
unrelated servicing updates — the documented tolerance carried over from the original script).

Exit 0 on full match, 1 on any missing schema or field-name mismatch, 2 on usage/IO error.
"""
import json
import sys


def _load(path: str):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


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

    if errors:
        print("::error::response-schema manifest verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    print(f"OK: response-schema manifest matches the captured document "
          f"({checked_schemas} schemas / {checked_fields} field names across {len(groups)} groups).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
