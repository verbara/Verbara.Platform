#!/usr/bin/env python3
"""Verify the three residual contract-shape fixtures against the CI-captured OpenAPI document.

Usage: verify-residual-shapes.py <real-openapi-document.json> <fixtures-dir>

Part of openapi-residual-contract-shapes (decision_ref Platform/ADR-0036). Sibling of
`verify-openapi-fixture.py` (openapi-response-schemas, ADR-0035): same "verbatim field names against
the CI-runtime-captured document" posture (design D4), applied to the three residual shapes the
`/xr:change` scouts reconciled (Platform + Platform.Web, 2026-07-25):

  1. `compliance-rule-summary.v1.json`  -> schema `ComplianceRuleSummaryDto`
       * top-level field names match verbatim, AND
       * `severity` is the CLOSED enum [Info, Warning, Critical] (the one genuine producer fix — a
         document-only `ComplianceSeverityEnumTransformer`, NOT a DTO type change).
  2. `topic-trends-response.v1.json`     -> schema `TopicTrendsResponse`
       * field names match verbatim (`trends`, `totalAnalyzed`) — NO `topics`/`from`/`to`
         (regression guard; the stale `topics` lived only in the Web shadow — no host change, D2).
  3. `paged-result-envelope.v1.json`     -> ANY `PagedResultOf<T>` concrete schema
       * envelope field names match verbatim. `openapi-typescript`'s `PagedResultOf<T>`
         monomorphization is by-design (no reusable generic in the emitted document — D3); this
         check verifies the envelope shape on whichever concrete envelope the document emits.

Each fixture is a JSON *instance* (an example object); its top-level keys are the verbatim field
names the named schema must declare. Exit 0 on full match, 1 on any missing schema, field-name
mismatch, or a severity enum that is not exactly [Info, Warning, Critical]; 2 on usage/IO error.
"""
import json
import os
import sys

SEVERITY_DOMAIN = ["Info", "Warning", "Critical"]


def _load(path: str):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _schema_props(schema: dict) -> set[str]:
    return set(schema.get("properties", {}).keys())


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <real-openapi-document.json> <fixtures-dir>", file=sys.stderr)
        return 2

    real_path, fixtures_dir = sys.argv[1], sys.argv[2]
    try:
        real_doc = _load(real_path)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::error::Failed to read/parse real document {real_path}: {exc}", file=sys.stderr)
        return 2

    schemas = real_doc.get("components", {}).get("schemas", {})
    errors: list[str] = []

    def fixture(name: str) -> dict | None:
        path = os.path.join(fixtures_dir, name)
        try:
            return _load(path)
        except (OSError, json.JSONDecodeError) as exc:
            errors.append(f"could not read fixture {path}: {exc}")
            return None

    # ── 1. ComplianceRuleSummaryDto — field names + closed severity enum ──────────────────────
    crs = fixture("compliance-rule-summary.v1.json")
    if crs is not None:
        schema = schemas.get("ComplianceRuleSummaryDto")
        if schema is None:
            errors.append("schema 'ComplianceRuleSummaryDto' MISSING from the captured document")
        else:
            expected = set(crs.keys())
            actual = _schema_props(schema)
            for missing in sorted(expected - actual):
                errors.append(f"[ComplianceRuleSummaryDto] fixture field '{missing}' MISSING from the document")
            for extra in sorted(actual - expected):
                errors.append(f"[ComplianceRuleSummaryDto] document field '{extra}' NOT in the fixture (fixture stale)")
            severity = schema.get("properties", {}).get("severity", {})
            enum = severity.get("enum")
            if enum != SEVERITY_DOMAIN:
                errors.append(
                    f"[ComplianceRuleSummaryDto.severity] expected closed enum {SEVERITY_DOMAIN} "
                    f"(document-only ComplianceSeverityEnumTransformer), got {enum!r}"
                )

    # ── 2. TopicTrendsResponse — verbatim field names, no topics/from/to ──────────────────────
    ttr = fixture("topic-trends-response.v1.json")
    if ttr is not None:
        schema = schemas.get("TopicTrendsResponse")
        if schema is None:
            errors.append("schema 'TopicTrendsResponse' MISSING from the captured document")
        else:
            expected = set(ttr.keys())
            actual = _schema_props(schema)
            if expected != actual:
                errors.append(
                    f"[TopicTrendsResponse] field-name mismatch — fixture {sorted(expected)} "
                    f"vs document {sorted(actual)} (the `topics` name lived only in the Web shadow)"
                )

    # ── 3. PagedResult envelope — any concrete PagedResultOf<T> (monomorphization by-design) ───
    pre = fixture("paged-result-envelope.v1.json")
    if pre is not None:
        concrete = {n: s for n, s in schemas.items() if n.startswith("PagedResultOf")}
        if not concrete:
            errors.append("no 'PagedResultOf<T>' concrete envelope schema found in the captured document")
        else:
            expected = set(pre.keys())
            # The envelope shape is identical across every monomorphized concrete; verify all present ones.
            for name, schema in sorted(concrete.items()):
                actual = _schema_props(schema)
                if expected != actual:
                    errors.append(
                        f"[{name}] envelope field-name mismatch — fixture {sorted(expected)} "
                        f"vs document {sorted(actual)}"
                    )

    if errors:
        print("::error::residual contract-shape verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    print("OK: residual contract shapes match their fixtures — "
          "ComplianceRuleSummaryDto.severity is the closed [Info,Warning,Critical] enum; "
          "TopicTrendsResponse emits trends/totalAnalyzed (no topics); "
          "PagedResultOf<T> envelope verified (monomorphization by-design).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
