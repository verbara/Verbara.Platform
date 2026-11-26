#!/usr/bin/env python3
"""Postgres password guard: no shipped stack pastes the Postgres password into an Npgsql connection string.

Usage:
  check-compose-postgres-password.py [--root DIR]

Compose interpolation pastes a variable's value into the text around it and cannot escape it. In
`Password=${POSTGRES_PASSWORD}` a `;` in the password ends the value early, and a quote or a leading or trailing
space is read differently by Npgsql, so a password Postgres and Asterisk accept stops the Api, the realtime service
and the mail service from connecting. The stacks therefore keep the password out of the connection string and give
it to Npgsql in the `PGPASSWORD` environment variable, which Npgsql reads when the connection string has no
Password and which compose sets as given, whatever characters it holds.

The script finds every compose file under docker/ and at the repository root (so a new stack or overlay is checked
without being listed here), and in each service every `ConnectionStrings__*` entry that is an Npgsql connection
string (it names a `Host` or `Server`). Only `ConnectionStrings__*` keys are read because the Platform, Sdk and Pro
code open every Postgres connection from `GetConnectionString(...)`; a new setting read another way needs a rule here.
It requires:
  - no Password (`Password`, `Pwd`, `Psw`) built from a variable; a fixed literal is left alone;
  - a connection string with no Password comes with `PGPASSWORD` on the same service;
  - that `PGPASSWORD` is the stack's Postgres password: with no variables set and with POSTGRES_PASSWORD set to a
    probe value (an operator's own .env), it equals the `postgres` service's POSTGRES_PASSWORD in the same file,
    or, in an overlay without a `postgres` service, it follows POSTGRES_PASSWORD.
A connection string that is a single variable (`${PG_CONNECTION_STRING}`) is the operator's own and is skipped.
In the Helm charts under infra/ (templates, helpers and values, read as text) a Password that is a `$(VAR)` or
`{{ }}` expansion fails the same way, wherever it sits in the string or on its line.
An env file under docker/ or at the root (`.env*`, the files `env_file:` hands a service as given, such as
.env.production.example) fails when a `ConnectionStrings__*` Npgsql string in it holds any Password, since an env
file pastes the value as given too, or when it has such a string without Password and sets no `PGPASSWORD`.
Each failure line names the file, the service, the setting and what to change.

Exit codes:
  0  pass
  1  a stack pastes the password into a connection string, or a service lacks or mismatches PGPASSWORD
  2  usage error, a file that cannot be read, or no Npgsql connection string found to check
"""
import argparse
import glob
import importlib.util
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, os.pardir))

# Compose loading and interpolation are shared with the Asterisk realtime guard, so both read a stack the same way.
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

_PROBE = "probe;pass'word\" x"
ENVIRONMENTS = [
    ("no variables set", {}),
    ("POSTGRES_PASSWORD set in .env", {"POSTGRES_PASSWORD": _PROBE}),
]

_PASSWORD_KEYS = {"password", "pwd", "psw"}
_HOST_KEYS = {"host", "server"}
_WHOLE_VARIABLE = re.compile(r"^\s*\$\{?[A-Za-z_][A-Za-z0-9_]*(?:[:?-][^}]*)?\}?\s*$")
_HELM_PASSWORD = re.compile(r"\b(?:Password|Pwd|Psw)\s*=\s*(?:\$\(|\{\{)", re.IGNORECASE)
_ENV_LINE = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")


def _pairs(conn):
    pairs = {}
    for part in conn.split(";"):
        if "=" in part:
            key, value = part.split("=", 1)
            pairs[key.strip().lower()] = value
    return pairs


def _is_npgsql(value):
    return bool(_HOST_KEYS & set(_pairs(value)))


def check_file(rel, services):
    """Returns (connection strings checked, failure lines) for one compose file."""
    failures = []
    checked = 0
    postgres = services.get("postgres")
    pg_pass_raw = _env(postgres).get("POSTGRES_PASSWORD") if isinstance(postgres, dict) else None
    for name, service in services.items():
        if not isinstance(service, dict):
            continue
        env = _env(service)
        needs_pgpassword = []
        for key, raw in env.items():
            if raw is None or not str(key).lower().startswith("connectionstrings__"):
                continue
            value = str(raw)
            if _WHOLE_VARIABLE.match(value) or not _is_npgsql(value):
                continue
            checked += 1
            password = next((v for k, v in _pairs(value).items() if k in _PASSWORD_KEYS), None)
            if password is None:
                needs_pgpassword.append(key)
            elif "$" in password:
                failures.append(
                    f"{rel}: {name} {key} pastes '{password.strip()}' into the connection string's Password, where "
                    f"a ; ' \" or space in it breaks the string; drop Password= and set PGPASSWORD on {name}")
        if not needs_pgpassword:
            continue
        keys = ", ".join(needs_pgpassword)
        if "PGPASSWORD" not in env or env.get("PGPASSWORD") is None:
            failures.append(f"{rel}: {name} {keys} has no Password and {name} sets no PGPASSWORD, so Npgsql "
                            f"connects without one; set PGPASSWORD on {name} to the stack's POSTGRES_PASSWORD")
            continue
        for env_name, values in ENVIRONMENTS:
            try:
                got = interpolate(env["PGPASSWORD"], values)
                if pg_pass_raw is not None:
                    want = interpolate(pg_pass_raw, values) or ""
                elif env_name == "no variables set":
                    continue  # an overlay's default belongs to the stack it is layered on
                else:
                    want = _PROBE
            except Refused:
                continue
            if got != want:
                where = "the stack's Postgres has" if pg_pass_raw is not None else "POSTGRES_PASSWORD is"
                failures.append(f"{rel} ({env_name}): {name} PGPASSWORD is '{got}', {where} '{want}'; "
                                f"set PGPASSWORD on {name} from POSTGRES_PASSWORD")
    return checked, failures


def check_env_files(root):
    """Returns (connection strings checked, files with one, failure lines) for the env files under docker/ and root."""
    failures = []
    checked = 0
    files = []
    paths = sorted(set(glob.glob(os.path.join(root, "docker", "**", ".env*"), recursive=True)
                       + glob.glob(os.path.join(root, ".env*"))))
    for path in paths:
        if not os.path.isfile(path):
            continue
        rel = os.path.relpath(path, root)
        try:
            with open(path, encoding="utf-8") as fh:
                lines = fh.read().splitlines()
        except OSError as e:
            raise InputError(f"cannot read {path}: {e}") from e
        keys = {}
        needs_pgpassword = []
        count = 0
        for number, line in enumerate(lines, 1):
            m = _ENV_LINE.match(line)
            if not m:
                continue
            key, value = m.group(1), m.group(2).strip()
            keys[key] = value
            if not key.lower().startswith("connectionstrings__") or not _is_npgsql(value):
                continue
            count += 1
            if any(k in _PASSWORD_KEYS for k in _pairs(value)):
                failures.append(f"{rel}:{number}: {key} holds a Password, which the env file hands Npgsql as given, "
                                f"so a ; ' \" or space in it breaks the string; drop Password= and set PGPASSWORD")
            else:
                needs_pgpassword.append(key)
        if needs_pgpassword and not keys.get("PGPASSWORD"):
            failures.append(f"{rel}: {', '.join(needs_pgpassword)} has no Password and the file sets no PGPASSWORD; "
                            f"set PGPASSWORD to the stack's POSTGRES_PASSWORD")
        if count:
            checked += count
            files.append(rel)
    return checked, files, failures


def check_helm(root):
    failures = []
    paths = set()
    for pattern in ("*.yaml", "*.yml", "*.tpl"):
        paths.update(glob.glob(os.path.join(root, "infra", "**", pattern), recursive=True))
    for path in sorted(paths):
        rel = os.path.relpath(path, root)
        try:
            with open(path, encoding="utf-8") as fh:
                lines = fh.read().splitlines()
        except OSError as e:
            raise InputError(f"cannot read {path}: {e}") from e
        for number, line in enumerate(lines, 1):
            if _HELM_PASSWORD.search(line):
                failures.append(f"{rel}:{number}: the connection string expands the password into its Password, "
                                f"where a ; ' \" or space in it breaks the string; drop Password= and name the "
                                f"secret's variable PGPASSWORD")
    return failures


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
            count, found = check_file(rel, load(path))
            if count:
                checked.append(rel)
                total += count
            failures += found
        count, env_files, found = check_env_files(root)
        checked += env_files
        total += count
        failures += found
        failures += check_helm(root)
    except InputError as e:
        print(f"check-compose-postgres-password: {e}", file=sys.stderr)
        return _EXIT_INPUT
    if not checked:
        print("check-compose-postgres-password: no compose file under docker/ holds an Npgsql connection string",
              file=sys.stderr)
        return _EXIT_INPUT
    for line in failures:
        print(line)
    if failures:
        return _EXIT_FAIL
    print(f"check-compose-postgres-password: {total} connection string(s) in {len(checked)} file(s) carry no pasted "
          "password: " + ", ".join(checked))
    return 0


if __name__ == "__main__":
    sys.exit(main())
