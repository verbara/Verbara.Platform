#!/usr/bin/env python3
"""SMB reference stack pin guard (OpenSpec change bump-smb-reference-stack-pins, design Decision 3).

Usage:
  check-smb-pins.py [--compose PATH] [--template PATH] [--web-changelog PATH]

Agreement check (always; offline, deterministic — run by CI):
  Every `ghcr.io/verbara/platform/<image>:` reference in the SMB compose must read
  `${PLATFORM_WEB_TAG:-X}` (the `web` image) or `${PLATFORM_API_TAG:-Y}` (every other image) — a hard-coded tag
  or a variable without a default fails. All API defaults must be equal, and equal to the template's
  `PLATFORM_API_TAG`; the Web default must equal the template's `PLATFORM_WEB_TAG`. Each pin must be a release tag
  (`vX.Y.Z` for the API, `vX.Y.Z-web` for the Web console).

Floor check (with --web-changelog; run with the sibling Verbara.Platform.Web checkout whenever the pins move):
  Reads the Web CHANGELOG section for the Web pin (`## [X.Y.Z-web]`, or the pre-suffix `## [X.Y.Z]`) and the
  "requires Platform ≥ vA.B.C" (or `>=`) statements inside that section only; the highest one is the floor, and it
  must not exceed the API pin. A missing section, or a section with no such statement ("paired with Platform ...",
  or nothing at all), fails with "floor unknown": an unknown floor never counts as compatible and nothing is
  inherited from an older section. The fix for a missing floor is in the Web CHANGELOG, not here.

Exit codes:
  0  pass
  1  pin drift, a pin that is not a release tag, a Web floor above the API pin, or "floor unknown"
  2  usage error, or an input file that cannot be read or holds no pins
"""
import argparse
import os
import re
import sys

_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))
_DEFAULT_COMPOSE = os.path.join(_ROOT, "docker", "docker-compose.reference-smb.yml")
_DEFAULT_TEMPLATE = os.path.join(_ROOT, "docker", ".env.reference-smb.example")

_IMAGE_RE = re.compile(r"^\s*image:\s*[\"']?ghcr\.io/verbara/platform/([A-Za-z0-9_.-]+):(\S+?)[\"']?\s*(?:#.*)?$")
_VAR_DEFAULT_RE = re.compile(r"^\$\{(PLATFORM_API_TAG|PLATFORM_WEB_TAG):-([^}]*)\}$")
_API_TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
_WEB_TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)-web$")
_FLOOR_RE = re.compile(r"requires\s+Platform\s*(?:≥|>=)\s*v?(\d+)\.(\d+)\.(\d+)", re.IGNORECASE)
_SECTION_RE = re.compile(r"^##\s+\[([^\]]+)\]")

_EXIT_FAIL = 1
_EXIT_INPUT = 2


class InputError(Exception):
    pass


def _read(p):
    try:
        with open(p, encoding="utf-8") as fh:
            return fh.read()
    except OSError as e:
        raise InputError(f"cannot read {p}: {e}") from e


def parse_compose(text, where):
    """Returns ({'PLATFORM_API_TAG': [(image, default), ...], 'PLATFORM_WEB_TAG': [...]}, [errors])."""
    pins = {"PLATFORM_API_TAG": [], "PLATFORM_WEB_TAG": []}
    errors = []
    for n, line in enumerate(text.splitlines(), 1):
        m = _IMAGE_RE.match(line)
        if not m:
            continue
        image, tag = m.group(1), m.group(2)
        expected_var = "PLATFORM_WEB_TAG" if image == "web" else "PLATFORM_API_TAG"
        v = _VAR_DEFAULT_RE.match(tag)
        if not v or v.group(1) != expected_var or not v.group(2):
            errors.append(f"{where}:{n}: image '{image}' tag is '{tag}'; expected "
                          f"'${{{expected_var}:-<release tag>}}'")
            continue
        pins[expected_var].append((image, v.group(2)))
    if not pins["PLATFORM_API_TAG"] and not pins["PLATFORM_WEB_TAG"] and not errors:
        raise InputError(f"{where}: no ghcr.io/verbara/platform image found")
    return pins, errors


def parse_template(text, where):
    values = {}
    errors = []
    for n, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if not s or s.startswith("#"):
            continue
        m = re.match(r"^(?:export\s+)?(PLATFORM_API_TAG|PLATFORM_WEB_TAG)\s*=\s*(.*)$", s)
        if not m:
            continue
        val = re.sub(r"\s+#.*$", "", m.group(2)).strip().strip("\"'")
        if m.group(1) in values:
            errors.append(f"{where}:{n}: {m.group(1)} assigned more than once")
        values[m.group(1)] = val
    for var in ("PLATFORM_API_TAG", "PLATFORM_WEB_TAG"):
        if var not in values:
            errors.append(f"{where}: {var} is not set (an uncommented {var}=<tag> line is required)")
    return values, errors


def check_agreement(compose_path, template_path):
    """Returns (api_pin, web_pin, errors). Pins are the template's values (None when absent)."""
    pins, errors = parse_compose(_read(compose_path), compose_path)
    tmpl, terrs = parse_template(_read(template_path), template_path)
    errors += terrs
    for var, tag_re in (("PLATFORM_API_TAG", _API_TAG_RE), ("PLATFORM_WEB_TAG", _WEB_TAG_RE)):
        defaults = pins[var]
        if not defaults:
            errors.append(f"{compose_path}: no image uses ${{{var}:-...}}")
        distinct = sorted({d for _, d in defaults})
        if len(distinct) > 1:
            listing = ", ".join(f"{img}={d}" for img, d in defaults)
            errors.append(f"{compose_path}: {var} defaults disagree: {listing}")
        want = tmpl.get(var)
        if want is not None:
            for img, d in defaults:
                if d != want:
                    errors.append(f"{var}: compose default for '{img}' is {d} but the template "
                                  f"({template_path}) has {want}")
        for tag in sorted(set(distinct) | ({want} if want else set())):
            if not tag_re.match(tag):
                errors.append(f"{var}: '{tag}' is not a release tag "
                              f"({'vX.Y.Z-web' if var == 'PLATFORM_WEB_TAG' else 'vX.Y.Z'})")
    return tmpl.get("PLATFORM_API_TAG"), tmpl.get("PLATFORM_WEB_TAG"), errors


def find_section(text, web_pin):
    """Returns (heading_looked_for, section_body or None). Only the pinned section, never a neighbour."""
    version = web_pin[1:] if web_pin.startswith("v") else web_pin
    names = {version}
    if version.endswith("-web"):
        names.add(version[: -len("-web")])
    lines = text.splitlines()
    for i, line in enumerate(lines):
        m = _SECTION_RE.match(line)
        if m and m.group(1).strip() in names:
            body = []
            for nxt in lines[i + 1:]:
                if re.match(r"^##\s", nxt):
                    break
                body.append(nxt)
            return f"## [{version}]", "\n".join(body)
    return f"## [{version}]", None


def check_floor(changelog_path, api_pin, web_pin):
    text = _read(changelog_path)
    heading, body = find_section(text, web_pin)
    if body is None:
        return [f"floor unknown: no section '{heading}' for the Web pin {web_pin} in {changelog_path}"]
    floors = [tuple(int(g) for g in m.groups()) for m in _FLOOR_RE.finditer(body)]
    if not floors:
        return [f"floor unknown: section '{heading}' in {changelog_path} states no 'requires Platform ≥ vX.Y.Z' "
                f"(nothing is inherited from older sections; add the floor to the Web CHANGELOG)"]
    floor = max(floors)
    api = _API_TAG_RE.match(api_pin or "")
    if not api:
        return [f"cannot compare the Web floor: API pin '{api_pin}' is not a release tag"]
    if floor > tuple(int(g) for g in api.groups()):
        f = "v{}.{}.{}".format(*floor)
        return [f"Web {web_pin} requires Platform ≥ {f} ({changelog_path} '{heading}') but the API pin is {api_pin}"]
    return []


def main(argv=None):
    ap = argparse.ArgumentParser(description="SMB reference stack pin guard.")
    ap.add_argument("--compose", default=_DEFAULT_COMPOSE)
    ap.add_argument("--template", default=_DEFAULT_TEMPLATE)
    ap.add_argument("--web-changelog", default=None)
    args = ap.parse_args(argv)
    args.compose, args.template = os.path.relpath(args.compose), os.path.relpath(args.template)
    try:
        api_pin, web_pin, errors = check_agreement(args.compose, args.template)
        if args.web_changelog is not None:
            if web_pin is None:
                errors.append("floor unknown: the template names no PLATFORM_WEB_TAG")
            else:
                errors += check_floor(args.web_changelog, api_pin, web_pin)
    except InputError as e:
        print(f"check-smb-pins: ERROR: {e}", file=sys.stderr)
        return _EXIT_INPUT
    if errors:
        for e in errors:
            print(f"check-smb-pins: FAIL: {e}", file=sys.stderr)
        return _EXIT_FAIL
    checked = "agreement + Web floor" if args.web_changelog else "agreement"
    print(f"check-smb-pins: OK ({checked}): PLATFORM_API_TAG={api_pin} PLATFORM_WEB_TAG={web_pin}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
