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

It then renders the Kubernetes charts (`helm template infra/k8s/helm/asterisk` and `infra/k8s/helm/platform`, or
--rendered-asterisk / --rendered-platform) and requires, of the asterisk StatefulSet's `asterisk` container:
  - every PG_REALTIME_* variable that docker/entrypoint-asterisk.sh reads is set, to a non-empty value or from a
    Secret key;
  - its `command` (which replaces the image's entrypoint) renders /etc/asterisk/res_pgsql.conf from
    res_pgsql.conf.template and names each of those variables;
  - the asterisk-config ConfigMap is mounted at res_pgsql.conf.template (holding a `key = ` line for dbhost, dbport,
    dbname, dbuser and dbpass), extconfig.conf (ps_endpoints => pgsql) and sorcery.conf (endpoint = realtime,ps_endpoints,
    and each endpoint/auth/aor cache before every other mapping of its type: a cache after pjsip.conf's mapping hides
    the realtime endpoints from `pjsip show endpoints`);
  - host, port, database and user equal the platform chart's platform-api ConnectionStrings__Postgres, and the
    password Secret's value equals platform-api's PG_PASSWORD Secret's (compared when both charts render the Secret).
CHECK_ASTERISK_REALTIME_HELM overrides the helm executable (tests).

The interpolation is compose's: ${VAR}, ${VAR:-d}, ${VAR-d}, ${VAR:?m}, ${VAR?m}, $VAR and $$.

Exit codes:
  0  pass
  1  a stack's Asterisk would read another database, user, password or host than its Postgres, or the Kubernetes
     chart's Asterisk would not render its realtime settings from the platform's Postgres
  2  usage error, a compose file, template or entrypoint that cannot be read, no stack found to check, no helm,
     or a chart that does not render
"""
import argparse
import glob
import os
import re
import subprocess
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


# ── Kubernetes (Helm) ────────────────────────────────────────────────────────────────────────────────────────────────

_ENTRYPOINT = os.path.join("docker", "entrypoint-asterisk.sh")
_ASTERISK_CHART = os.path.join("infra", "k8s", "helm", "asterisk")
_PLATFORM_CHART = os.path.join("infra", "k8s", "helm", "platform")
_CONFIG_DIR = "/etc/asterisk/"
_REALTIME_READ = re.compile(r"\$\{?(PG_REALTIME_[A-Z0-9_]+)")
_CONN_PART = re.compile(r"(\w+)=([^;]*)")
# (ConfigMap key, what it must hold, the regex that finds it)
_CONFIG_FILES = [
    ("extconfig.conf", "ps_endpoints => pgsql", re.compile(r"^\s*ps_endpoints\s*=>\s*pgsql\s*,", re.M)),
    ("sorcery.conf", "endpoint = realtime,ps_endpoints",
     re.compile(r"^\s*endpoint\s*=\s*realtime\s*,\s*ps_endpoints\b", re.M)),
]


def entrypoint_reads(root):
    path = os.path.join(root, _ENTRYPOINT)
    try:
        with open(path, encoding="utf-8") as fh:
            names = sorted(set(_REALTIME_READ.findall(fh.read())))
    except OSError as e:
        raise InputError(f"cannot read {path}: {e}") from e
    if not names:
        raise InputError(f"{_ENTRYPOINT} reads no PG_REALTIME_* variable")
    return names


def render(root, chart):
    helm = os.environ.get("CHECK_ASTERISK_REALTIME_HELM", "helm")
    try:
        r = subprocess.run([helm, "template", chart], capture_output=True, text=True, cwd=root, check=False)
    except OSError as e:
        raise InputError(f"cannot run '{helm} template {chart}': {e}") from e
    if r.returncode != 0:
        raise InputError(f"'helm template {chart}' failed (exit {r.returncode}): {r.stderr.strip()}")
    return r.stdout


def _rendered(root, chart, path):
    where = path or f"helm template {chart}"
    if path:
        try:
            with open(path, encoding="utf-8") as fh:
                text = fh.read()
        except OSError as e:
            raise InputError(f"cannot read {path}: {e}") from e
    else:
        text = render(root, chart)
    try:
        return where, [d for d in yaml.safe_load_all(text) if isinstance(d, dict)]
    except yaml.YAMLError as e:
        raise InputError(f"{where}: not YAML: {e}") from e


def _find(docs, kind, name):
    for d in docs:
        if d.get("kind") == kind and (d.get("metadata") or {}).get("name") == name:
            return d
    return None


def _container(workload, name):
    spec = ((workload or {}).get("spec") or {}).get("template", {}).get("spec") or {}
    for c in spec.get("containers") or []:
        if c.get("name") == name:
            return c, spec
    return None, spec


def _secret_value(docs, ref):
    secret = _find(docs, "Secret", ref.get("name"))
    if secret is None:
        return None
    data = secret.get("stringData") or {}
    return None if ref.get("key") not in data else str(data[ref.get("key")])


def _cache_order(where, text):
    failures = []
    lines = [ln.strip() for ln in text.splitlines()]
    for kind in ("endpoint", "auth", "aor"):
        caches = [i for i, ln in enumerate(lines) if re.match(rf"{kind}/cache\s*=", ln)]
        maps = [i for i, ln in enumerate(lines) if re.match(rf"{kind}\s*=", ln)]
        if caches and maps and min(caches) > min(maps):
            failures.append(f"{where}: the {kind} cache comes after another {kind} mapping, so a listing stops at the "
                            f"cache before realtime; put {kind}/cache first")
    return failures


def check_chart(names, ast_where, ast_docs, plat_where, plat_docs):
    failures = []
    sts = _find(ast_docs, "StatefulSet", "asterisk")
    container, pod = _container(sts, "asterisk")
    if container is None:
        return [f"{ast_where}: no StatefulSet asterisk with an asterisk container"]
    where = f"{ast_where}: StatefulSet asterisk, container asterisk"
    env = {e.get("name"): e for e in container.get("env") or [] if isinstance(e, dict)}
    for var in names:
        entry = env.get(var)
        ref = ((entry or {}).get("valueFrom") or {}).get("secretKeyRef")
        if entry is None or not (str(entry.get("value") or "").strip() or (ref and ref.get("name") and ref.get("key"))):
            failures.append(f"{where}: does not set {var}, which the entrypoint reads for res_pgsql.conf")
    command = "\n".join(str(c) for c in (container.get("command") or []) + (container.get("args") or []))
    if container.get("command"):
        if "res_pgsql.conf.template" not in command or _CONFIG_DIR + "res_pgsql.conf" not in command:
            failures.append(f"{where}: its command replaces the image's entrypoint and does not render "
                            f"{_CONFIG_DIR}res_pgsql.conf from res_pgsql.conf.template")
        for var in names:
            if var not in command:
                failures.append(f"{where}: its command replaces the image's entrypoint and never reads {var}")
    volumes = {v.get("name"): v for v in pod.get("volumes") or [] if isinstance(v, dict)}
    mounts = {}
    for m in container.get("volumeMounts") or []:
        cm = ((volumes.get(m.get("name")) or {}).get("configMap") or {}).get("name")
        if cm:
            mounts[m.get("mountPath")] = (cm, m.get("subPath"))
    for key, what, pattern in [("res_pgsql.conf.template", "dbhost..dbpass", None)] + _CONFIG_FILES:
        cm_name, sub = mounts.get(_CONFIG_DIR + key, (None, None))
        cm = _find(ast_docs, "ConfigMap", cm_name) if cm_name else None
        text = ((cm or {}).get("data") or {}).get(sub or "")
        if cm is None or text is None:
            failures.append(f"{where}: mounts no ConfigMap key at {_CONFIG_DIR}{key}")
            continue
        if pattern is None:
            have = {m.group(1) for m in re.finditer(r"^(\w+) = ", text, re.M)}
            for k, _var in SETTINGS:
                if k not in have:
                    failures.append(f"{ast_where}: ConfigMap {cm_name} {key} has no `{k} = ` line to render")
        elif not pattern.search(str(text)):
            failures.append(f"{ast_where}: ConfigMap {cm_name} {key} does not map {what}")
        if key == "sorcery.conf":
            failures += _cache_order(f"{ast_where}: ConfigMap {cm_name} sorcery.conf", str(text))
    api, _ = _container(_find(plat_docs, "Deployment", "platform-api"), "api")
    api_env = {e.get("name"): e for e in (api or {}).get("env") or [] if isinstance(e, dict)}
    conn = str((api_env.get("ConnectionStrings__Postgres") or {}).get("value") or "")
    parts = {k.lower(): v for k, v in _CONN_PART.findall(conn)}
    if not parts:
        return failures + [f"{plat_where}: platform-api renders no ConnectionStrings__Postgres"]
    for var, part in [("PG_REALTIME_HOST", "host"), ("PG_REALTIME_PORT", "port"), ("PG_REALTIME_DB", "database"),
                      ("PG_REALTIME_USER", "username")]:
        got = str((env.get(var) or {}).get("value") or "")
        if var in env and got != parts.get(part, ""):
            failures.append(f"{where}: {var} is '{got}', platform-api's Postgres {part} is '{parts.get(part, '')}'")
    ast_ref = ((env.get("PG_REALTIME_PASSWORD") or {}).get("valueFrom") or {}).get("secretKeyRef")
    api_ref = ((api_env.get("PG_PASSWORD") or {}).get("valueFrom") or {}).get("secretKeyRef")
    if ast_ref and api_ref:
        a, b = _secret_value(ast_docs, ast_ref), _secret_value(plat_docs, api_ref)
        if a is not None and b is not None and a != b:
            failures.append(f"{where}: PG_REALTIME_PASSWORD's Secret {ast_ref.get('name')} holds another password "
                            f"than platform-api's Secret {api_ref.get('name')}")
    return failures


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=_ROOT, help="repository root (tests)")
    ap.add_argument("--rendered-asterisk", help="the asterisk chart already rendered (helm template output)")
    ap.add_argument("--rendered-platform", help="the platform chart already rendered (helm template output)")
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
        names = entrypoint_reads(root)
        ast_where, ast_docs = _rendered(root, _ASTERISK_CHART, args.rendered_asterisk)
        plat_where, plat_docs = _rendered(root, _PLATFORM_CHART, args.rendered_platform)
        failures += check_chart(names, ast_where, ast_docs, plat_where, plat_docs)
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
          + ", ".join(checked) + f"; the {_ASTERISK_CHART} chart renders res_pgsql.conf from the platform's Postgres")
    return 0


if __name__ == "__main__":
    sys.exit(main())
