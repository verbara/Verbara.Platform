"""Unit tests for check-scale-backplane.py (the scaling overlay's push-backplane guard).

Runs the script as a subprocess, either against a rendered compose model written to a temp directory
(`--rendered`) or through a stand-in `docker` (CHECK_SCALE_BACKPLANE_DOCKER) that fails, so every case goes through
the one production code path. Stdlib unittest only — NO pip deps, so this runs in the `coverage-scripts` CI job.
"""
import copy
import json
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-scale-backplane.py")

_PASSING = {
    "services": {
        "platform-api": {
            "environment": {
                "ConnectionStrings__Postgres": "Host=postgres;Database=verbara",
                "ConnectionStrings__IdentityRedis": "redis:6379",
                "ConnectionStrings__Redis": "redis:6379",
            },
            "depends_on": {
                "postgres": {"condition": "service_healthy", "required": True},
                "redis": {"condition": "service_healthy", "required": True},
            },
        },
        "realtime": {"environment": {"ConnectionStrings__Redis": "redis:6379"}},
        "redis": {"image": "redis:7-alpine"},
    }
}


def _model(mutate=None):
    model = copy.deepcopy(_PASSING)
    if mutate:
        mutate(model["services"])
    return model


class CheckScaleBackplaneTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)

    def _run(self, model=None, raw=None, env=None, extra=()):
        args = [sys.executable, _SCRIPT, *extra]
        if model is not None or raw is not None:
            path = os.path.join(self._tmp.name, "rendered.json")
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(raw if raw is not None else json.dumps(model))
            args += ["--rendered", path]
        full_env = dict(os.environ)
        full_env.update(env or {})
        return subprocess.run(args, capture_output=True, text=True, env=full_env, check=False)

    def test_passes_when_the_api_shares_realtimes_redis(self):
        r = self._run(_model())
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_on_the_shipped_shape_without_connectionstrings_redis(self):
        r = self._run(_model(lambda s: s["platform-api"]["environment"].pop("ConnectionStrings__Redis")))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api has no ConnectionStrings__Redis", r.stdout)
        self.assertIn("docker/docker-compose.full.yml + docker/docker-compose.scale.yml", r.stdout)

    def test_fails_when_the_value_is_blank(self):
        def blank(s):
            s["platform-api"]["environment"]["ConnectionStrings__Redis"] = "  "
        r = self._run(_model(blank))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api has no ConnectionStrings__Redis", r.stdout)

    def test_fails_when_the_value_differs_from_realtimes(self):
        def other(s):
            s["platform-api"]["environment"]["ConnectionStrings__Redis"] = "other-redis:6379"
        r = self._run(_model(other))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("ConnectionStrings__Redis", r.stdout)
        self.assertIn("other-redis:6379", r.stdout)
        self.assertIn("realtime", r.stdout)

    def test_fails_when_realtime_has_no_redis_to_compare_with(self):
        r = self._run(_model(lambda s: s["realtime"]["environment"].pop("ConnectionStrings__Redis")))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("realtime", r.stdout)

    def test_fails_when_the_redis_dependency_is_removed(self):
        r = self._run(_model(lambda s: s["platform-api"]["depends_on"].pop("redis")))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("depends_on.redis.condition", r.stdout)

    def test_fails_when_the_redis_dependency_is_not_healthy(self):
        def started(s):
            s["platform-api"]["depends_on"]["redis"]["condition"] = "service_started"
        r = self._run(_model(started))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("depends_on.redis.condition", r.stdout)

    def test_fails_when_redis_keeps_a_profile(self):
        def gated(s):
            s["redis"]["profiles"] = ["cluster"]
        r = self._run(_model(gated))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("redis", r.stdout)
        self.assertIn("profiles", r.stdout)

    def test_reports_every_failure_not_only_the_first(self):
        def broken(s):
            s["platform-api"]["environment"].pop("ConnectionStrings__Redis")
            s["platform-api"]["depends_on"].pop("redis")
        r = self._run(_model(broken))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api has no ConnectionStrings__Redis", r.stdout)
        self.assertIn("depends_on.redis.condition", r.stdout)

    def test_exits_2_when_the_rendering_is_not_json(self):
        r = self._run(raw="services: [not json")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def test_exits_2_when_the_rendering_has_no_platform_api(self):
        r = self._run(_model(lambda s: s.pop("platform-api")))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def test_exits_2_when_docker_compose_fails_to_render(self):
        r = self._run(env={"CHECK_SCALE_BACKPLANE_DOCKER": "false"})
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("render", r.stderr)

    def test_the_render_fills_the_variables_the_base_example_leaves_empty(self):
        # docker/docker-compose.full.yml refuses to render without SERVICE_KEY, which docker/.env.full.example leaves
        # for the operator to fill (#689): a stand-in docker that renders only with SERVICE_KEY set.
        fake = os.path.join(self._tmp.name, "docker")
        with open(fake, "w", encoding="utf-8") as fh:
            fh.write("#!/bin/sh\n"
                     "[ -n \"$SERVICE_KEY\" ] || { echo 'required variable SERVICE_KEY is missing a value' >&2; exit 1; }\n"
                     f"cat <<'JSON'\n{json.dumps(_PASSING)}\nJSON\n")
        os.chmod(fake, 0o755)
        env = {"CHECK_SCALE_BACKPLANE_DOCKER": fake}
        saved = os.environ.pop("SERVICE_KEY", None)
        try:
            r = self._run(env=env)
        finally:
            if saved is not None:
                os.environ["SERVICE_KEY"] = saved
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_exits_2_when_docker_is_missing(self):
        missing = os.path.join(self._tmp.name, "no-such-docker")
        r = self._run(env={"CHECK_SCALE_BACKPLANE_DOCKER": missing})
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()
