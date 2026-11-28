#!/usr/bin/env python3
"""Helm chart image pin guard: the platform chart's api and realtime pins follow the releases (issue #468).

Usage:
  check-helm-image-pins.py [--values PATH] [--chart-yaml PATH] [--changelog PATH] [--max-lag K] [--ledger PATH]

The chart `infra/k8s/helm/platform` is what a cluster runs when deployed as shipped, so its default images must be
a recent release. The defaults are the repo's chart and CHANGELOG.md. Offline and deterministic (run by CI);
needs PyYAML.

Rules (always):
  1. `api.image.tag` equals `realtime.image.tag` (one release, one pair), and each is a release tag `vX.Y.Z`.
  2. `api.image.digest` and `realtime.image.digest` each read `sha256:` plus 64 lowercase hex characters, and they
     differ (api and realtime are two images with two digests). An empty digest fails: the chart is the shipped
     default, not the dev path.
  3. Chart.yaml `appVersion` equals the tag without the `v`.
  4. CHANGELOG.md has a `## [X.Y.Z]` heading for the pin. `## [Unreleased]` never counts as a release.
  5. Lag. The released headings are sorted by version number (not by their order in the file). The pin's lag is
     the number of released headings newer than the pin, NOT counting the newest heading; the guard fails when
     the lag is above --max-lag K (default 2). The newest heading is set aside because on a release bump PR it is
     the release being cut, whose images and digests do not exist yet. So the pin must be one of the newest K + 2
     headings: with K = 2 and headings 2.41.3, 2.41.2, 2.41.1, 2.41.0, 2.40.0, a pin of v2.41.0 passes and v2.40.0
     fails; with K = 0 a bump PR that adds 2.41.4 passes while the chart is still at v2.41.3.

Ledger check (with --ledger; run with the sibling verbara-website checkout's data/authorized-digests.json
whenever the pins move):
  6. For api and realtime, an entry in `current` or `deprecated` has `image_ref` `<repository>:<tag>` and a
     `manifest_list_digest` equal to the chart's digest.

Exit codes:
  0  pass
  1  a rule above fails
  2  usage error, or an input that cannot be read or parsed, values with no api or realtime image, or a CHANGELOG
     with no released heading
"""
import argparse
import json
import os
import re
import sys

_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))
_CHART_DIR = os.path.join(_ROOT, "infra", "k8s", "helm", "platform")
_DEFAULT_VALUES = os.path.join(_CHART_DIR, "values.yaml")
_DEFAULT_CHART_YAML = os.path.join(_CHART_DIR, "Chart.yaml")
_DEFAULT_CHANGELOG = os.path.join(_ROOT, "CHANGELOG.md")
_DEFAULT_MAX_LAG = 2

_IMAGES = ("api", "realtime")
_TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
_DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
_HEADING_RE = re.compile(r"^##\s+\[([^\]]+)\]")
_VERSION_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")

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


def _load_yaml(p):
    try:
        import yaml
    except ImportError as e:
        raise InputError("PyYAML is not installed (pip install pyyaml)") from e
    try:
        return yaml.safe_load(_read(p))
    except yaml.YAMLError as e:
        raise InputError(f"cannot parse {p}: {e}") from e


def read_images(values_path):
    """Returns {'api': {'repository', 'tag', 'digest'}, 'realtime': {...}} as strings."""
    doc = _load_yaml(values_path)
    images = {}
    for name in _IMAGES:
        section = doc.get(name) if isinstance(doc, dict) else None
        image = section.get("image") if isinstance(section, dict) else None
        if not isinstance(image, dict):
            raise InputError(f"{values_path}: no {name}.image mapping")
        images[name] = {k: "" if image.get(k) is None else str(image.get(k))
                        for k in ("repository", "tag", "digest")}
    return images


def released_headings(changelog_path):
    """Released `## [X.Y.Z]` headings as version tuples, newest first by version number."""
    versions = []
    for line in _read(changelog_path).splitlines():
        m = _HEADING_RE.match(line)
        if not m:
            continue
        name = m.group(1).strip()
        if name.lower() == "unreleased":
            continue
        v = _VERSION_RE.match(name)
        if v:
            versions.append(tuple(int(g) for g in v.groups()))
    if not versions:
        raise InputError(f"{changelog_path}: no released '## [X.Y.Z]' heading")
    return sorted(set(versions), reverse=True)


def _fmt(v):
    return "{}.{}.{}".format(*v)


def check_rules(values_path, images, chart_yaml_path, changelog_path, max_lag):
    errors = []
    api_tag, rt_tag = images["api"]["tag"], images["realtime"]["tag"]
    if api_tag != rt_tag:
        errors.append(f"{values_path}: api.image.tag {api_tag} != realtime.image.tag {rt_tag} "
                      f"(one release, one pair)")
    valid_tags = []
    for name in _IMAGES:
        tag = images[name]["tag"]
        if not _TAG_RE.match(tag):
            errors.append(f"{values_path}: {name}.image.tag '{tag}' is not a release tag (vX.Y.Z)")
        elif tag not in valid_tags:
            valid_tags.append(tag)
    for name in _IMAGES:
        digest = images[name]["digest"]
        if not _DIGEST_RE.match(digest):
            errors.append(f"{values_path}: {name}.image.digest '{digest}' is not a manifest-list digest "
                          f"(sha256: plus 64 lowercase hex characters)")
    if images["api"]["digest"] and images["api"]["digest"] == images["realtime"]["digest"]:
        errors.append(f"{values_path}: api.image.digest and realtime.image.digest are both "
                      f"{images['api']['digest']} (two images, two digests)")

    chart = _load_yaml(chart_yaml_path)
    app_version = chart.get("appVersion") if isinstance(chart, dict) else None
    app_version = "" if app_version is None else str(app_version)
    expected = api_tag[1:] if api_tag.startswith("v") else api_tag
    if app_version != expected:
        errors.append(f"{chart_yaml_path}: appVersion '{app_version}' != tag {api_tag} without the 'v'")

    headings = released_headings(changelog_path)
    newest = headings[0]
    for tag in valid_tags:
        pin = tuple(int(g) for g in _TAG_RE.match(tag).groups())
        if pin not in headings:
            errors.append(f"{values_path}: pin {tag} has no '## [{_fmt(pin)}]' heading in {changelog_path} "
                          f"(not a release)")
            continue
        behind = headings.index(pin)
        lag = max(0, behind - 1)
        if lag > max_lag:
            errors.append(
                f"{values_path}: api/realtime image tag {tag} trails {changelog_path} by {behind} releases "
                f"(newest released heading ## [{_fmt(newest)}]); allowed: at most {max_lag + 1} behind "
                f"(--max-lag {max_lag}, plus the newest heading, which may be the release being cut). Move "
                f"api.image.tag, realtime.image.tag, both digests and Chart.yaml appVersion to a released version; "
                f"the digests are in verbara-website data/authorized-digests.json or the tag's GitHub Release notes.")
    return errors


def check_ledger(ledger_path, images):
    try:
        doc = json.loads(_read(ledger_path))
    except ValueError as e:
        raise InputError(f"cannot parse {ledger_path}: {e}") from e
    if not isinstance(doc, dict):
        raise InputError(f"{ledger_path}: not a JSON object")
    entries = []
    for key in ("current", "deprecated"):
        part = doc.get(key, [])
        if not isinstance(part, list):
            raise InputError(f"{ledger_path}: '{key}' is not a list")
        entries += [e for e in part if isinstance(e, dict)]
    errors = []
    for name in _IMAGES:
        ref = f"{images[name]['repository']}:{images[name]['tag']}"
        digest = images[name]["digest"]
        found = [e.get("manifest_list_digest") for e in entries if e.get("image_ref") == ref]
        if not found:
            errors.append(f"{ledger_path}: no entry for {ref} in current or deprecated")
        elif digest not in found:
            errors.append(f"{name}.image.digest {digest} != ledger entry for {ref} ({', '.join(map(str, found))})")
    return errors


def main(argv=None):
    ap = argparse.ArgumentParser(description="Helm chart image pin guard.")
    ap.add_argument("--values", default=_DEFAULT_VALUES)
    ap.add_argument("--chart-yaml", default=_DEFAULT_CHART_YAML)
    ap.add_argument("--changelog", default=_DEFAULT_CHANGELOG)
    ap.add_argument("--max-lag", type=int, default=_DEFAULT_MAX_LAG)
    ap.add_argument("--ledger", default=None)
    args = ap.parse_args(argv)
    if args.max_lag < 0:
        ap.error("--max-lag must be 0 or more")
    values, chart_yaml, changelog = (os.path.relpath(p) for p in (args.values, args.chart_yaml, args.changelog))
    try:
        images = read_images(values)
        errors = check_rules(values, images, chart_yaml, changelog, args.max_lag)
        if args.ledger is not None:
            errors += check_ledger(args.ledger, images)
    except InputError as e:
        print(f"check-helm-image-pins: ERROR: {e}", file=sys.stderr)
        return _EXIT_INPUT
    if errors:
        for e in errors:
            print(f"check-helm-image-pins: FAIL: {e}", file=sys.stderr)
        return _EXIT_FAIL
    checked = "rules + ledger" if args.ledger else "rules"
    print(f"check-helm-image-pins: OK ({checked}): {values} api/realtime {images['api']['tag']} "
          f"(--max-lag {args.max_lag})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
