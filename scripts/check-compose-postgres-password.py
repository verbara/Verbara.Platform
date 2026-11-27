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
A service that takes PGPASSWORD from an `env_file:` (docker-compose.production.yml: .env.production) needs the stack's
`postgres` service to read POSTGRES_PASSWORD from that same file, and the file (or its `.example`) to set the two
equal: compose fills `${POSTGRES_PASSWORD}` from the shell and the `.env` next to the compose file, never from an
env_file, so a postgres service with `POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-...}` is initialised with the default
while the services send the operator's password. An env_file that is that `.env` is read by both and passes when the
two values agree.
A service that runs the realtime image (built from Dockerfile.realtime, or an image named `.../realtime:<tag>`) must
also have `ConnectionStrings__Cluster` or `ConnectionStrings__Postgres`, in `environment:` or in an `env_file:` (the
file, or its `.example` copy when the operator's own file is not in the tree; `environment:` wins): the realtime
service's leader election reads Cluster, falling back to Postgres only when Cluster is absent (an empty Cluster is
not absent), and stops at startup without one. The one it reads must be an Npgsql connection string with no
variables set: not blank, not a variable with no default, not a non-Postgres value; a `${VAR:?}` compose refuses to
start without is left to the operator. A
service with neither `build:` nor `image:` is an overlay and is left to the file it is layered on.
In the Helm charts under infra/ (templates, helpers and values, read as text) a Password that is a `$(VAR)` or
`{{ }}` expansion fails the same way, wherever it sits in the string or on its line.
An env file under docker/ or at the root (`.env*`, the files `env_file:` hands a service as given, such as
.env.production.example) fails when a `ConnectionStrings__*` Npgsql string in it holds any Password, since an env
file pastes the value as given too, or when it has such a string without Password and sets no `PGPASSWORD`.
Each failure line names the file, the service, the setting and what to change.

Exit codes:
  0  pass
  1  a stack pastes the password into a connection string, a service lacks or mismatches PGPASSWORD, a
     realtime service has no Postgres connection string, or postgres does not read the env file the services
     take PGPASSWORD from
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


_REALTIME_KEYS = ("connectionstrings__cluster", "connectionstrings__postgres")
_REALTIME_IMAGE = re.compile(r"(?:^|/)realtime(?:[:@]|$)")


def _is_realtime(service):
    build = service.get("build")
    if isinstance(build, dict) and os.path.basename(str(build.get("dockerfile") or "")) == "Dockerfile.realtime":
        return True
    return bool(_REALTIME_IMAGE.search(str(service.get("image") or "")))


def _env_file_paths(service):
    entries = service.get("env_file") or []
    if not isinstance(entries, list):
        entries = [entries]
    for entry in entries:
        yield str(entry.get("path", "")) if isinstance(entry, dict) else str(entry)


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


def _realtime_value(value):
    """Returns why a realtime connection string cannot open Postgres, or None when it can (or compose refuses it)."""
    try:
        text = interpolate(value, {})
    except Refused:
        return None  # compose will not start without it, so the operator has to supply one
    if not text.strip():
        return "blank" if not value.strip() else f"'{value.strip()}', which is blank when no variable is set"
    if not _is_npgsql(text):
        return f"'{value.strip()}', which is not an Npgsql connection string (it names no Host)"
    return None


def check_realtime(rel, name, service, compose_dir, root):
    """Returns the failure lines for one realtime service whose Postgres connection string cannot work."""
    if not _is_realtime(service):
        return []
    # What the container sees: the env_file entries in order, then `environment:` over them (compose precedence).
    # A key with no value passes the host's own variable through, which compose leaves unset, so it is skipped.
    seen = {}
    missing = []
    for entry in _env_file_paths(service):
        path = os.path.normpath(os.path.join(compose_dir, entry))
        found = next((p for p in (path, path + ".example") if os.path.isfile(p)), None)
        if found is None:
            missing.append(os.path.relpath(path, root))
            continue
        for k, v in _read_env_file(found).items():
            if k.lower() in _REALTIME_KEYS:
                seen[k.lower()] = (k, v)
    for k, v in _env(service).items():
        if str(k).lower() in _REALTIME_KEYS and v is not None:
            seen[str(k).lower()] = (str(k), str(v))
    # Program.cs reads GetConnectionString("Cluster") ?? GetConnectionString("Postgres"): only an absent Cluster
    # falls back to Postgres; an empty one is not null and stops the boot.
    picked = next((seen[k] for k in _REALTIME_KEYS if k in seen), None)
    if picked is None:
        where = f" (env_file {', '.join(missing)} is not in the tree, nor its .example)" if missing else ""
        return [f"{rel}: {name} runs the realtime image and has no ConnectionStrings__Cluster or "
                f"ConnectionStrings__Postgres{where}, so its leader election stops the boot; give {name} the stack's "
                f"Postgres connection string without Password= and PGPASSWORD from POSTGRES_PASSWORD"]
    key, value = picked
    why = _realtime_value(value)
    if why is None:
        return []
    return [f"{rel}: {name} {key} is {why}, and {name} runs the realtime image, whose leader election reads it "
            f"(Cluster first; an empty Cluster does not fall back to Postgres) and stops the boot; give {name} the "
            f"stack's Postgres connection string without Password= and PGPASSWORD from POSTGRES_PASSWORD"]


def check_env_file_postgres(rel, services, compose_dir, root):
    """Returns the failure lines for services whose PGPASSWORD comes from an env_file their stack's Postgres never reads.

    Compose fills `${POSTGRES_PASSWORD}` from the shell and the `.env` file next to the compose file, never from an
    `env_file:` entry. A service that takes PGPASSWORD from `.env.production` therefore uses the operator's password
    while a postgres service with `POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-...}` is initialised with the default.
    """
    postgres = services.get("postgres")
    if not isinstance(postgres, dict):
        return []
    pg_env = _env(postgres)
    pg_raw = pg_env.get("POSTGRES_PASSWORD")
    pg_files = {os.path.normpath(os.path.join(compose_dir, e)) for e in _env_file_paths(postgres)}
    users = {}
    for name, service in services.items():
        if name == "postgres" or not isinstance(service, dict) or "PGPASSWORD" in _env(service):
            continue
        for entry in _env_file_paths(service):
            path = os.path.normpath(os.path.join(compose_dir, entry))
            found = next((p for p in (path, path + ".example") if os.path.isfile(p)), None)
            if found is not None and "PGPASSWORD" in _read_env_file(found):
                users.setdefault((path, found, entry), []).append(name)
    failures = []
    for (path, found, entry), names in sorted(users.items()):
        keys = _read_env_file(found)
        shown = os.path.relpath(found, root)
        who = ", ".join(names)
        interpolated = os.path.basename(path) == ".env" and os.path.dirname(path) == os.path.normpath(compose_dir)
        if pg_raw is not None and not interpolated:
            failures.append(
                f"{rel}: {who} take PGPASSWORD from {entry}, and postgres takes POSTGRES_PASSWORD from '{pg_raw}', "
                f"which compose fills from the shell or .env, never from {entry}, so Postgres is initialised with "
                f"another password and they cannot connect; give postgres env_file: {entry} and drop "
                f"POSTGRES_PASSWORD from its environment:")
            continue
        if pg_raw is None and path not in pg_files:
            failures.append(f"{rel}: {who} take PGPASSWORD from {entry}, and postgres does not read {entry}; give "
                            f"postgres env_file: {entry}")
            continue
        try:
            want = interpolate(pg_raw, keys) if pg_raw is not None else keys.get("POSTGRES_PASSWORD")
        except Refused:
            continue
        if want != keys["PGPASSWORD"]:
            failures.append(f"{shown}: PGPASSWORD is '{keys['PGPASSWORD']}', and the POSTGRES_PASSWORD postgres "
                            f"gets from it is '{want}'; keep the two equal")
    return failures


def check_file(rel, services, compose_dir=None, root=None):
    """Returns (connection strings checked, failure lines) for one compose file."""
    failures = []
    checked = 0
    postgres = services.get("postgres")
    pg_pass_raw = _env(postgres).get("POSTGRES_PASSWORD") if isinstance(postgres, dict) else None
    if compose_dir is not None:
        failures += check_env_file_postgres(rel, services, compose_dir, root)
    for name, service in services.items():
        if not isinstance(service, dict):
            continue
        if compose_dir is not None:
            failures += check_realtime(rel, name, service, compose_dir, root)
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
            count, found = check_file(rel, load(path), os.path.dirname(path), root)
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
