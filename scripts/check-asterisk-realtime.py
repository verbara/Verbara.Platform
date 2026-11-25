#!/usr/bin/env python3
"""Asterisk realtime database guard: every shipped stack's Asterisk reads its configuration from that stack's Postgres.

Usage:
  check-asterisk-realtime.py [--root DIR]

Asterisk loads its PJSIP endpoints, queues and trunks through res_config_pgsql, from the res_pgsql.conf that
docker/entrypoint-asterisk.sh renders on every boot: it copies res_pgsql.conf.template from the directory mounted on
/etc/asterisk, then replaces dbhost, dbport, dbname, dbuser and dbpass with PG_REALTIME_HOST, _PORT, _DB, _USER and
_PASSWORD when each is set and non-empty. A stack that leaves them unset keeps the template's values, and when those
name another database, user or password, Asterisk starts with no endpoints ("No objects found").

The script finds every compose file under docker/ with a service built from Dockerfile.asterisk (so a new stack is
checked without being listed here) and a `postgres` service, and works out what each side ends up with:
  - Asterisk: the PG_REALTIME_* value when it is non-empty after interpolation, else the template's;
  - Postgres: POSTGRES_USER (image default `postgres`), POSTGRES_DB (image default: the user), POSTGRES_PASSWORD.
It does so twice: with no variables set (every `${VAR:-default}` takes its default) and with POSTGRES_DB,
POSTGRES_USER and POSTGRES_PASSWORD set to probe values (an operator's own .env). It requires, in each:
  - dbname == the Postgres database, dbuser == the Postgres user, dbpass == the Postgres password;
  - dbhost == `postgres` and dbport == 5432 unless Asterisk runs with `network_mode: host` (then the host and
    port are the operator's, as in the reference SMB stack).
A side whose value needs a variable compose would refuse to start without (`${VAR:?}`) skips that environment.
Each failure line names the file, the environment and the setting.

The interpolation is compose's: ${VAR}, ${VAR:-d}, ${VAR-d}, ${VAR:?m}, ${VAR?m}, $VAR and $$.

Exit codes:
  0  pass
  1  a stack's Asterisk would read another database, user, password or host than its Postgres
  2  usage error, a compose file or template that cannot be read, or no stack found to check
"""
import argparse
import glob
import os
import re
import sys

try:
    import yaml
except ImportError:  # pragma: no cover - CI installs PyYAML before this step
    print("check-asterisk-realtime: PyYAML is required (pip install pyyaml)", file=sys.stderr)
    sys.exit(2)

_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))

_EXIT_FAIL = 1
_EXIT_INPUT = 2

ENVIRONMENTS = [
    ("no variables set", {}),
    ("POSTGRES_* set in .env", {
        "POSTGRES_DB": "probe_db",
        "POSTGRES_USER": "probe_user",
        "POSTGRES_PASSWORD": "probe_password",
    }),
]

# (template key, PG_REALTIME_* variable)
SETTINGS = [
    ("dbhost", "PG_REALTIME_HOST"),
    ("dbport", "PG_REALTIME_PORT"),
    ("dbname", "PG_REALTIME_DB"),
    ("dbuser", "PG_REALTIME_USER"),
    ("dbpass", "PG_REALTIME_PASSWORD"),
]

_VAR = re.compile(r"\$(?:\$|\{([A-Za-z_][A-Za-z0-9_]*)(?:(:?[-?])([^}]*))?\}|([A-Za-z_][A-Za-z0-9_]*))")


class _ComposeLoader(yaml.SafeLoader):
    """SafeLoader that reads compose's merge tags (`!reset`, `!override`) as plain values."""


def _compose_tag(loader, _suffix, node):
    if isinstance(node, yaml.MappingNode):
        return loader.construct_mapping(node)
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node)
    return loader.construct_scalar(node)


_ComposeLoader.add_multi_constructor("!", _compose_tag)


class InputError(Exception):
    pass


class Refused(Exception):
    """compose would refuse to start: a `${VAR:?}` / `${VAR?}` variable is missing."""


def interpolate(value, env):
    if value is None:
        return None
    text = str(value)

    def sub(m):
        if m.group(0) == "$$":
            return "$"
        name = m.group(1) or m.group(4)
        op, arg = m.group(2), m.group(3) or ""
        present = name in env
        val = env.get(name, "")
        if op == ":-":
            return val if val else arg
        if op == "-":
            return val if present else arg
        if op == ":?":
            if not val:
                raise Refused(name)
            return val
        if op == "?":
            if not present:
                raise Refused(name)
            return val
        return val

    return _VAR.sub(sub, text)


def _env(service):
    env = (service or {}).get("environment") or {}
    if isinstance(env, list):
        env = dict(item.split("=", 1) if "=" in item else (item, None) for item in env)
    return env


def _is_asterisk(service):
    build = service.get("build") if isinstance(service, dict) else None
    return isinstance(build, dict) and os.path.basename(str(build.get("dockerfile") or "")) == "Dockerfile.asterisk"


def _config_dir(service, compose_dir):
    for vol in service.get("volumes") or []:
        if isinstance(vol, str):
            parts = vol.split(":")
            if len(parts) >= 2 and parts[1].rstrip("/") == "/etc/asterisk":
                return os.path.normpath(os.path.join(compose_dir, parts[0]))
        elif isinstance(vol, dict) and str(vol.get("target", "")).rstrip("/") == "/etc/asterisk":
            return os.path.normpath(os.path.join(compose_dir, str(vol.get("source", ""))))
    return None


def read_template(path):
    try:
        with open(path, encoding="utf-8") as fh:
            lines = fh.read().splitlines()
    except OSError as e:
        raise InputError(f"cannot read {path}: {e}") from e
    values = {}
    for line in lines:
        m = re.match(r"^(\w+) = (.*)$", line)  # the entrypoint's sed matches `^key = `
        if m:
            values[m.group(1)] = m.group(2).strip()
    return values


def load(path):
    try:
        with open(path, encoding="utf-8") as fh:
            model = yaml.load(fh, Loader=_ComposeLoader)  # noqa: S506 - a SafeLoader subclass
    except (OSError, yaml.YAMLError) as e:
        raise InputError(f"cannot read {path}: {e}") from e
    services = model.get("services") if isinstance(model, dict) else None
    return services if isinstance(services, dict) else {}


def check_stack(rel, services, compose_dir):
    failures = []
    postgres = services["postgres"]
    pg_env = _env(postgres)
    for ast_name, ast in services.items():
        if not _is_asterisk(ast):
            continue
        cfg = _config_dir(ast, compose_dir)
        if cfg is None:
            failures.append(f"{rel}: {ast_name} mounts nothing on /etc/asterisk, so it has no res_pgsql.conf.template")
            continue
        template = read_template(os.path.join(cfg, "res_pgsql.conf.template"))
        ast_env = _env(ast)
        host_network = str(ast.get("network_mode") or "") == "host"
        for env_name, env in ENVIRONMENTS:
            try:
                pg_user = interpolate(pg_env.get("POSTGRES_USER"), env) or "postgres"
                pg_db = interpolate(pg_env.get("POSTGRES_DB"), env) or pg_user
                pg_pass = interpolate(pg_env.get("POSTGRES_PASSWORD"), env) or ""
                effective = {}
                for key, var in SETTINGS:
                    v = interpolate(ast_env.get(var), env)
                    effective[key] = v if v else template.get(key)
            except Refused:
                continue
            expected = {"dbname": pg_db, "dbuser": pg_user, "dbpass": pg_pass}
            if not host_network:
                expected["dbhost"] = "postgres"
                expected["dbport"] = "5432"
            for key, var in SETTINGS:
                if key in expected and effective.get(key) != expected[key]:
                    failures.append(
                        f"{rel} ({env_name}): {ast_name} reads realtime {key} '{effective.get(key)}', "
                        f"the stack's Postgres has '{expected[key]}'; set {var} on the {ast_name} service")
    return failures


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=_ROOT, help="repository root (tests)")
    args = ap.parse_args(argv)
    root = os.path.abspath(args.root)
    failures = []
    checked = []
    try:
        files = sorted(glob.glob(os.path.join(root, "docker", "**", "*.yml"), recursive=True))
        for path in files:
            services = load(path)
            rel = os.path.relpath(path, root)
            if not isinstance(services.get("postgres"), dict):
                continue
            if not any(_is_asterisk(s) for s in services.values()):
                continue
            checked.append(rel)
            failures += check_stack(rel, services, os.path.dirname(path))
    except InputError as e:
        print(f"check-asterisk-realtime: {e}", file=sys.stderr)
        return _EXIT_INPUT
    if not checked:
        print("check-asterisk-realtime: no compose file under docker/ builds Asterisk next to a postgres service",
              file=sys.stderr)
        return _EXIT_INPUT
    for line in failures:
        print(line)
    if failures:
        return _EXIT_FAIL
    print(f"check-asterisk-realtime: {len(checked)} stack(s) point Asterisk at their own Postgres: "
          + ", ".join(checked))
    return 0


if __name__ == "__main__":
    sys.exit(main())
