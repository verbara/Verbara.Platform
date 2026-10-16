#!/usr/bin/env python3
"""Release provenance gate: refuse an image whose OCI provenance labels are not verified.

Usage:
  check-image-provenance.py inputs --revision R --version V --created C --ref-name T
  check-image-provenance.py gate --image IMG --revision R --version V --created C [--docker BIN]

Called by .github/workflows/release.yml on every matrix leg (fail-release-on-missing-image-provenance):

  inputs  Validates the values the `Resolve release tag` step resolved, before any build:
          the revision is a full 40-hex commit SHA, the version equals the tag ref name, and
          the created date is an RFC 3339 date-time with an explicit offset. None of them may
          be empty or the `unknown` placeholder the Dockerfiles default to.

  gate    Reads `BIN image inspect --format '{{json .Config.Labels}}' IMG` from the local
          daemon (the image was built with load: true, not yet pushed) and passes only when
          org.opencontainers.image.revision, .version and .created EQUAL the expected values.
          The expected values are validated the same way first, so a placeholder on both
          sides can never compare equal.

Fail closed. Exactly one path exits 0. Exit codes:
  0  pass
  1  unstamped     a label is absent, empty, `unknown`, not a string, or different; or the
                   image carries no labels at all. The cause is in the code or workflow at the
                   tag, which a re-run replays unchanged: fix it on main and cut a new tag.
  2  usage error   (argparse)
  3  unverifiable  the labels could not be read: inspect failed, the binary is missing, or
                   its output is empty, not JSON, or not a JSON object. Re-run the failed leg.
  4  invalid inputs  an expected/resolved value is itself not a real value.

Every failure is printed as a GitHub `::error` annotation naming the image, the label, the
value found, the value expected and the class. Dependency-free (stdlib only).
"""
import argparse
import datetime
import json
import re
import subprocess
import sys

EXIT_OK = 0
EXIT_UNSTAMPED = 1
EXIT_UNVERIFIABLE = 3
EXIT_INVALID_INPUTS = 4

PLACEHOLDER = "unknown"
LABEL_PREFIX = "org.opencontainers.image."
INSPECT_FORMAT = "{{json .Config.Labels}}"

_SHA_RE = re.compile(r"[0-9a-f]{40}")
# RFC 3339 date-time (section 5.6) with an explicit offset; uppercase T/Z only.
_RFC3339_RE = re.compile(
    r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})")

_REMEDY = {
    "unstamped": (
        "class unstamped: the cause is in the code or workflow at this tag; a re-run "
        "replays it unchanged. Fix it on main and cut a new tag."),
    "unverifiable": (
        "class unverifiable: the labels could not be read (daemon or tooling failure); "
        "re-run the failed leg."),
}


def _escape(value):
    """Escape workflow-command data so a value cannot end the annotation or forge one."""
    return str(value).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def _error(title, message):
    print(f"::error title={_escape(title)}::{_escape(message)}")


def _is_rfc3339(value):
    if not _RFC3339_RE.fullmatch(value):
        return False
    try:
        datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return False
    return True


def _invalid_values(revision, version, created):
    """Return (name, value, reason) for every expected value that is not a real value."""
    problems = []
    if not _SHA_RE.fullmatch(revision):
        problems.append(("revision", revision, "not a 40-hex lowercase commit SHA"))
    if version == "" or version == PLACEHOLDER:
        problems.append(("version", version, "empty or the `unknown` placeholder"))
    if not _is_rfc3339(created):
        problems.append(("created", created, "not an RFC 3339 date-time with an offset"))
    return problems


def _report_invalid(problems):
    for name, value, reason in problems:
        _error("Image provenance inputs",
               f"{name}={value!r} is invalid: {reason}. Refusing to build an image whose "
               f"provenance cannot be stamped.")
    return EXIT_INVALID_INPUTS


def cmd_inputs(args):
    problems = _invalid_values(args.revision, args.version, args.created)
    if not any(name == "version" for name, _, _ in problems) and args.version != args.ref_name:
        problems.append((
            "version", args.version, f"does not equal the tag ref name {args.ref_name!r}"))
    if problems:
        return _report_invalid(problems)
    print(f"provenance inputs valid: revision={args.revision} version={args.version} "
          f"created={args.created}")
    return EXIT_OK


def _unverifiable(image, reason):
    _error("Image provenance gate (unverifiable)",
           f"image={image} label={LABEL_PREFIX}{{revision,version,created}} "
           f"found=(unreadable: {reason}) expected=(readable labels) class=unverifiable")
    print(_REMEDY["unverifiable"])
    return EXIT_UNVERIFIABLE


def _read_labels(docker, image):
    """Return (labels_or_None, error_reason). labels None + no reason = image has no labels."""
    try:
        result = subprocess.run(
            [docker, "image", "inspect", "--format", INSPECT_FORMAT, image],
            capture_output=True, text=True, check=False)
    except OSError as exc:
        return None, f"cannot run {docker!r}: {exc}"
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip() or "no output"
        return None, f"inspect exited {result.returncode}: {detail}"
    raw = result.stdout.strip()
    if not raw:
        return None, "inspect printed nothing"
    try:
        labels = json.loads(raw)
    except json.JSONDecodeError as exc:
        return None, f"inspect output is not JSON ({exc}): {raw[:200]}"
    if labels is None:
        return {}, None
    if not isinstance(labels, dict):
        return None, f"labels are not a JSON object: {raw[:200]}"
    return labels, None


def _describe(found):
    if found is _MISSING:
        return "(absent)"
    if isinstance(found, str):
        return found if found else "(empty)"
    return json.dumps(found)


_MISSING = object()


def cmd_gate(args):
    problems = _invalid_values(args.revision, args.version, args.created)
    if problems:
        return _report_invalid(problems)

    labels, reason = _read_labels(args.docker, args.image)
    if reason is not None:
        return _unverifiable(args.image, reason)

    expected = {
        "revision": args.revision,
        "version": args.version,
        "created": args.created,
    }
    failures = 0
    for name, want in expected.items():
        label = LABEL_PREFIX + name
        found = labels.get(label, _MISSING)
        if isinstance(found, str) and found != "" and found != PLACEHOLDER and found == want:
            continue
        failures += 1
        _error("Image provenance gate (unstamped)",
               f"image={args.image} label={label} found={_describe(found)} "
               f"expected={want} class=unstamped")
    if failures:
        print(_REMEDY["unstamped"])
        return EXIT_UNSTAMPED

    print(f"provenance verified on {args.image}: revision={args.revision} "
          f"version={args.version} created={args.created}")
    return EXIT_OK


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)

    for name in ("inputs", "gate"):
        p = sub.add_parser(name)
        p.add_argument("--revision", required=True)
        p.add_argument("--version", required=True)
        p.add_argument("--created", required=True)
        if name == "inputs":
            p.add_argument("--ref-name", required=True)
        else:
            p.add_argument("--image", required=True)
            p.add_argument("--docker", default="docker")

    args = parser.parse_args(argv)
    if args.command == "inputs":
        return cmd_inputs(args)
    return cmd_gate(args)


if __name__ == "__main__":
    sys.exit(main())
