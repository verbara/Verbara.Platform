#!/usr/bin/env python3
"""Scaling overlay push-backplane guard (agent-assist-works-end-to-end, design D9 "The shipped scaling overlay").

Usage:
  check-scale-backplane.py [--rendered FILE]

Every shipped compose pair that runs more than one `platform-api` replica must put each replica on the Redis push
backplane: the Api registers the cross-replica push relay (and with it the Agent Assist stream feed and the shared
live-session list) only when `ConnectionStrings:Redis` is set. Without it each replica serves those from its own
process, which is correct on one replica only.

For each pair in PAIRS the script renders the merge with `docker compose -f <base> -f <overlay> config --format json`
(it reads the rendered model, not the overlay's text, so an edit to either file is seen) and requires:
  - `platform-api.environment.ConnectionStrings__Redis` present and non-blank;
  - equal to `realtime.environment.ConnectionStrings__Redis` (the same Redis, the same push channels);
  - `platform-api.depends_on.redis.condition` == `service_healthy` (the Redis client aborts the boot on a failed
    connect);
  - the `redis` service has no `profiles` (it runs whenever the overlay is applied).
Each failure line names the file pair and the key.

The render runs as an operator's does after filling the base's example (docker-compose.<name>.yml reads
.env.<name>.example): each variable the example leaves empty, such as SERVICE_KEY, which the base refuses to start
without, is given a placeholder unless the environment already sets it.

--rendered FILE reads an already rendered model (JSON) for the single pair instead of running docker (tests).
CHECK_SCALE_BACKPLANE_DOCKER overrides the docker executable (tests).

Exit codes:
  0  pass
  1  a pair fails one of the rules above
  2  usage error, no docker, a render that fails, or a rendering that is not a compose model with platform-api
"""
import argparse
import json
import os
import re
import subprocess
import sys

_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))

# Every compose pair that runs more than one platform-api replica (`replicas:` > 1, or a documented
# `--scale platform-api=N` with N > 1). Paths relative to the repository root.
PAIRS = [
    ("docker/docker-compose.full.yml", "docker/docker-compose.scale.yml"),
]

_EXIT_FAIL = 1
_EXIT_INPUT = 2


class InputError(Exception):
    pass


_EMPTY_ENV_LINE = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*$")


def _render_env(base):
    """The environment for rendering `base`: the variables its example leaves for the operator to fill get a
    placeholder, so a `${VAR:?...}` the operator fills does not stop the render."""
    env = dict(os.environ)
    m = re.match(r"^docker-compose\.(.+)\.ya?ml$", os.path.basename(base))
    example = os.path.join(_ROOT, os.path.dirname(base), f".env.{m.group(1)}.example") if m else None
    if example and os.path.isfile(example):
        with open(example, encoding="utf-8") as fh:
            for line in fh:
                em = _EMPTY_ENV_LINE.match(line)
                if em and not env.get(em.group(1)):
                    env[em.group(1)] = "filled-by-check-scale-backplane"
    return env


def render(base, overlay):
    docker = os.environ.get("CHECK_SCALE_BACKPLANE_DOCKER", "docker")
    cmd = [docker, "compose", "-f", os.path.join(_ROOT, base), "-f", os.path.join(_ROOT, overlay),
           "config", "--format", "json"]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, cwd=_ROOT, env=_render_env(base), check=False)
    except OSError as e:
        raise InputError(f"{base} + {overlay}: cannot run '{docker} compose' to render the pair: {e}") from e
    if r.returncode != 0:
        raise InputError(f"{base} + {overlay}: 'docker compose config' failed to render the pair "
                         f"(exit {r.returncode}): {r.stderr.strip()}")
    return parse(r.stdout, f"{base} + {overlay}")


def parse(text, where):
    try:
        model = json.loads(text)
    except ValueError as e:
        raise InputError(f"{where}: the rendering is not JSON: {e}") from e
    services = model.get("services") if isinstance(model, dict) else None
    if not isinstance(services, dict) or not isinstance(services.get("platform-api"), dict):
        raise InputError(f"{where}: the rendering has no platform-api service")
    return services


def _env(service):
    env = (service or {}).get("environment") or {}
    if isinstance(env, list):  # compose renders a map; accept the list form too
        env = dict(item.split("=", 1) if "=" in item else (item, None) for item in env)
    return env


def check(services, where):
    failures = []
    api = services["platform-api"]
    api_redis = _env(api).get("ConnectionStrings__Redis")
    realtime_redis = _env(services.get("realtime")).get("ConnectionStrings__Redis")
    if api_redis is None or not str(api_redis).strip():
        failures.append(f"{where}: platform-api has no ConnectionStrings__Redis (environment.ConnectionStrings__Redis); "
                        "its replicas are off the push backplane")
    elif realtime_redis is None or not str(realtime_redis).strip():
        failures.append(f"{where}: realtime has no ConnectionStrings__Redis to compare platform-api's "
                        f"'{api_redis}' with (environment.ConnectionStrings__Redis)")
    elif str(api_redis).strip() != str(realtime_redis).strip():
        failures.append(f"{where}: platform-api environment.ConnectionStrings__Redis is '{api_redis}', "
                        f"realtime's is '{realtime_redis}'; both must name the same Redis")
    condition = (((api.get("depends_on") or {}).get("redis")) or {}).get("condition")
    if condition != "service_healthy":
        failures.append(f"{where}: platform-api depends_on.redis.condition is '{condition}', "
                        "expected 'service_healthy'")
    redis = services.get("redis")
    if not isinstance(redis, dict):
        failures.append(f"{where}: no redis service")
    elif redis.get("profiles"):
        failures.append(f"{where}: redis keeps profiles {redis['profiles']}; the overlay must reset them")
    return failures


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--rendered", help="a rendered compose model (JSON) for the single pair, instead of docker")
    args = ap.parse_args(argv)
    if args.rendered and len(PAIRS) != 1:
        print("--rendered needs exactly one pair", file=sys.stderr)
        return _EXIT_INPUT
    failures = []
    try:
        for base, overlay in PAIRS:
            where = f"{base} + {overlay}"
            if args.rendered:
                try:
                    with open(args.rendered, encoding="utf-8") as fh:
                        services = parse(fh.read(), where)
                except OSError as e:
                    raise InputError(f"cannot read {args.rendered}: {e}") from e
            else:
                services = render(base, overlay)
            failures += check(services, where)
    except InputError as e:
        print(f"check-scale-backplane: {e}", file=sys.stderr)
        return _EXIT_INPUT
    for line in failures:
        print(line)
    if failures:
        return _EXIT_FAIL
    print(f"check-scale-backplane: {len(PAIRS)} pair(s) put every platform-api replica on the push backplane")
    return 0


if __name__ == "__main__":
    sys.exit(main())
