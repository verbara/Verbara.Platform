#!/usr/bin/env python3
"""CSAT reply secret guard: in every shipped stack the Api and the mail service use the same CSAT reply secret.

Usage:
  check-compose-csat-reply-secret.py [--root DIR]

A CSAT email goes out with the reply address csat+<token>@<domain>, where the Api signs the token with
Csat:Email:TokenSigningSecret (Program.cs: falling back to Csat:WebChatTokenSecret, then to a value derived from
the licence path). With the reply mailbox on, the mail service checks each reply's token with
Imap:TokenSigningSecret. When the two differ, the mail service rejects the token of every reply; only a reply whose
mail client kept the In-Reply-To header still reaches its survey.

The script finds every compose file under docker/ and at the repository root (so a new stack or overlay is checked
without being listed here). In each file that runs the mail service (built from Dockerfile.mail, or an image named
`.../mail:<tag>`) with Imap__TokenSigningSecret set, every Api service (built from the repository's Dockerfile, or
an image named `.../api:<tag>` or `...-api:<tag>`) must set Csat__Email__TokenSigningSecret (or
Csat__WebChatTokenSecret, the Api's fallback) to the same value: with no variables set, and with each variable the
two settings name set to a probe value (an operator's own .env). A value comes from `environment:`, else from an
`env_file:` (the file, or its `.example` copy when the operator's own file is not in the tree); a `${VAR:?}` compose
refuses to start without is left to the operator in that case. A file with a mail service and no Api service is an
overlay and is left to the stack it is layered on.

Exit codes:
  0  pass
  1  an Api service sets no CSAT reply secret, or one that differs from the mail service's
  2  usage error, a file that cannot be read, or no stack with both services found to check
"""
import argparse
import glob
import importlib.util
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, os.pardir))

# Compose loading and interpolation are shared with the Asterisk realtime guard, so every guard reads a stack the same way.
_spec = importlib.util.spec_from_file_location("check_asterisk_realtime", os.path.join(_HERE, "check-asterisk-realtime.py"))
_realtime = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_realtime)
interpolate = _realtime.interpolate
Refused = _realtime.Refused
InputError = _realtime.InputError
load = _realtime.load
_env = _realtime._env

_EXIT_FAIL = 1
_EXIT_INPUT = 2

_MAIL_KEY = "Imap__TokenSigningSecret"
# Program.cs: Csat:Email:TokenSigningSecret ?? Csat:WebChatTokenSecret ?? a value derived from the licence path.
_API_KEYS = ("Csat__Email__TokenSigningSecret", "Csat__WebChatTokenSecret")
_PROBE = "probe-csat-reply-secret-0123456789abcdef"

_MAIL_IMAGE = re.compile(r"(?:^|/)mail(?:[:@]|$)")
_API_IMAGE = re.compile(r"(?:^|[/-])api(?:[:@]|$)")
_VAR_NAME = re.compile(r"\$\{?([A-Za-z_][A-Za-z0-9_]*)")
_ENV_LINE = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")


def _build(service):
    build = service.get("build")
    if isinstance(build, str):
        return build, "Dockerfile"
    if isinstance(build, dict):
        return str(build.get("context") or "."), str(build.get("dockerfile") or "Dockerfile")
    return None, None


def is_mail(service):
    _, dockerfile = _build(service)
    if dockerfile is not None and os.path.basename(dockerfile) == "Dockerfile.mail":
        return True
    return bool(_MAIL_IMAGE.search(str(service.get("image") or "")))


def is_api(service, compose_dir, root):
    context, dockerfile = _build(service)
    if dockerfile is not None:
        # The Api is the image built from the Dockerfile at the repository root (the console's Dockerfile has its own
        # context, the Platform.Web tree).
        return (os.path.normpath(os.path.join(compose_dir, context)) == os.path.normpath(root)
                and os.path.normpath(dockerfile) == "Dockerfile")
    return bool(_API_IMAGE.search(str(service.get("image") or "")))


def _read_env_file(path):
    keys = {}
    try:
        with open(path, encoding="utf-8") as fh:
            for line in fh.read().splitlines():
                m = _ENV_LINE.match(line)
                if m:
                    keys[m.group(1)] = m.group(2).strip()
    except OSError as e:
        raise InputError(f"cannot read {path}: {e}") from e
    return keys


def _env_file_paths(service):
    entries = service.get("env_file") or []
    if not isinstance(entries, list):
        entries = [entries]
    for entry in entries:
        yield str(entry.get("path", "")) if isinstance(entry, dict) else str(entry)


def setting(service, keys, compose_dir):
    """Returns (key, raw value, where) of the first of `keys` the container sees, or None.

    Compose precedence: the env_file entries in order, then `environment:` over them. A key listed with no value
    passes the host's own variable through, which compose leaves unset, so it is skipped.
    """
    seen = {}
    for entry in _env_file_paths(service):
        path = os.path.normpath(os.path.join(compose_dir, entry))
        found = next((p for p in (path, path + ".example") if os.path.isfile(p)), None)
        if found is None:
            continue
        for k, v in _read_env_file(found).items():
            if k in keys:
                seen[k] = (v, f"env_file {entry}", True)
    for k, v in _env(service).items():
        if str(k) in keys and v is not None:
            seen[str(k)] = (str(v), "environment", False)
    for k in keys:
        if k in seen:
            value, where, from_file = seen[k]
            return k, value, where, from_file
    return None


def _environments(*raws):
    names = sorted({m.group(1) for raw in raws for m in _VAR_NAME.finditer(raw)})
    yield "no variables set", {}
    for name in names:
        yield f"{name} set in .env", {name: _PROBE}


def _value(raw, from_file, env):
    # An env file hands the value to the container as given: compose does not interpolate it.
    return raw if from_file else interpolate(raw, env)


def check_file(rel, services, compose_dir, root):
    """Returns (pairs checked, failure lines) for one compose file."""
    mails = []
    apis = []
    for name, service in services.items():
        if not isinstance(service, dict):
            continue
        if is_mail(service):
            found = setting(service, (_MAIL_KEY,), compose_dir)
            if found is not None:
                mails.append((name, found))
        elif is_api(service, compose_dir, root):
            apis.append((name, service))
    failures = []
    checked = 0
    for mail_name, (_, mail_raw, mail_where, mail_from_file) in mails:
        for api_name, api in apis:
            checked += 1
            found = setting(api, _API_KEYS, compose_dir)
            if found is None:
                failures.append(
                    f"{rel}: {mail_name} checks CSAT replies with {_MAIL_KEY} ({mail_where}: '{mail_raw}'), and "
                    f"{api_name} sets no Csat__Email__TokenSigningSecret, so the Api signs the reply addresses with "
                    f"a value of its own and the mail service rejects every reply's token; set "
                    f"Csat__Email__TokenSigningSecret on {api_name} from the same variable")
                continue
            api_key, api_raw, api_where, api_from_file = found
            for env_name, env in _environments(mail_raw, api_raw):
                try:
                    want = _value(mail_raw, mail_from_file, env)
                    got = _value(api_raw, api_from_file, env)
                except Refused:
                    continue  # compose will not start without it, so the operator has to supply one
                if got != want:
                    failures.append(
                        f"{rel} ({env_name}): {api_name} {api_key} ({api_where}) is '{got}' and {mail_name} "
                        f"{_MAIL_KEY} ({mail_where}) is '{want}', so the mail service rejects the token of every "
                        f"CSAT reply; give both the same variable")
                    break
    return checked, failures


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=_ROOT, help="repository root (tests)")
    args = ap.parse_args(argv)
    root = os.path.abspath(args.root)
    failures = []
    checked = []
    total = 0
    try:
        files = sorted(set(
            glob.glob(os.path.join(root, "docker", "**", "*.yml"), recursive=True)
            + glob.glob(os.path.join(root, "docker", "**", "*.yaml"), recursive=True)
            + glob.glob(os.path.join(root, "docker-compose*.yml"))
            + glob.glob(os.path.join(root, "docker-compose*.yaml"))
            + glob.glob(os.path.join(root, "compose*.yml"))
            + glob.glob(os.path.join(root, "compose*.yaml"))))
        for path in files:
            rel = os.path.relpath(path, root)
            count, found = check_file(rel, load(path), os.path.dirname(path), root)
            if count:
                checked.append(rel)
                total += count
            failures += found
    except InputError as e:
        print(f"check-compose-csat-reply-secret: {e}", file=sys.stderr)
        return _EXIT_INPUT
    if not checked:
        print("check-compose-csat-reply-secret: no compose file under docker/ runs the mail service with "
              f"{_MAIL_KEY} next to an Api service", file=sys.stderr)
        return _EXIT_INPUT
    for line in failures:
        print(line)
    if failures:
        return _EXIT_FAIL
    print(f"check-compose-csat-reply-secret: {total} Api/mail pair(s) in {len(checked)} file(s) share one CSAT reply "
          "secret: " + ", ".join(checked))
    return 0


if __name__ == "__main__":
    sys.exit(main())
