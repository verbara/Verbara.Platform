"""Unit tests for check-compose-postgres-password.py (no stack pastes the Postgres password into a connection string).

Runs the script as a subprocess against a small repository tree written to a temp directory (`--root`), and once
against this repository's own tree. Needs PyYAML, which the `coverage-scripts` CI job installs.
"""
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-compose-postgres-password.py")
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))

_POSTGRES = (
    "  postgres:\n"
    "    image: postgres:18-alpine\n"
    "    environment:\n"
    "      POSTGRES_DB: verbara\n"
    "      POSTGRES_USER: platform\n"
    "      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-platform_secret}\n"
)

_GOOD_CONN = "Host=postgres;Database=verbara;Username=platform"


def _api(conn=_GOOD_CONN, pgpassword="${POSTGRES_PASSWORD:-platform_secret}", name="platform-api"):
    text = (
        f"  {name}:\n"
        "    image: verbara/api\n"
        "    environment:\n"
        f"      ConnectionStrings__Postgres: \"{conn}\"\n"
        "      ConnectionStrings__Redis: redis:6379\n"
    )
    if pgpassword is not None:
        text += f"      PGPASSWORD: {pgpassword}\n"
    return text


class CheckComposePostgresPasswordTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = self._tmp.name

    def _write(self, rel, text):
        path = os.path.join(self.root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)

    def _stack(self, services, rel="docker/docker-compose.full.yml"):
        self._write(rel, "services:\n" + services)

    def _run(self, root=None):
        return subprocess.run([sys.executable, _SCRIPT, "--root", root or self.root],
                              capture_output=True, text=True, check=False)

    def test_passes_when_the_password_comes_from_pgpassword(self):
        self._stack(_api() + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)

    def test_fails_when_the_password_is_pasted_into_the_connection_string(self):
        self._stack(_api(_GOOD_CONN + ";Password=${POSTGRES_PASSWORD:-platform_secret}") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml: platform-api ConnectionStrings__Postgres pastes "
                      "'${POSTGRES_PASSWORD:-platform_secret}'", r.stdout)
        self.assertIn("set PGPASSWORD on platform-api", r.stdout)

    def test_quoting_the_pasted_password_still_fails(self):
        self._stack(_api(_GOOD_CONN + ";Password='${POSTGRES_PASSWORD}'") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("pastes", r.stdout)

    def test_a_password_alias_in_the_middle_is_found(self):
        self._stack(_api("Host=postgres;PWD=${POSTGRES_PASSWORD};Database=verbara;Pooling=true") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("'${POSTGRES_PASSWORD}'", r.stdout)

    def test_a_fixed_literal_password_is_left_alone(self):
        self._stack(_api(_GOOD_CONN + ";Password=loadtest", pgpassword=None)
                    + _POSTGRES.replace("${POSTGRES_PASSWORD:-platform_secret}", "loadtest"))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_when_the_service_sets_no_pgpassword(self):
        self._stack(_api(pgpassword=None) + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api sets no PGPASSWORD", r.stdout)

    def test_fails_when_pgpassword_is_fixed_and_the_operator_sets_their_own(self):
        self._stack(_api(pgpassword="platform_secret") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("(POSTGRES_PASSWORD set in .env): platform-api PGPASSWORD is 'platform_secret'", r.stdout)
        self.assertNotIn("(no variables set)", r.stdout)

    def test_fails_when_the_defaults_differ(self):
        self._stack(_api(pgpassword="${POSTGRES_PASSWORD:-platform_demo}") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("(no variables set): platform-api PGPASSWORD is 'platform_demo', the stack's Postgres has "
                      "'platform_secret'", r.stdout)

    def test_an_overlay_without_postgres_must_follow_postgres_password(self):
        self._stack(_api(), rel="docker/docker-compose.scale.yml")
        self.assertEqual(self._run().returncode, 0)
        self._stack(_api(pgpassword="${OTHER_PASSWORD:-platform_secret}"), rel="docker/docker-compose.scale.yml")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.scale.yml (POSTGRES_PASSWORD set in .env)", r.stdout)

    def test_the_list_form_is_read(self):
        self._stack(
            "  mail:\n"
            "    image: verbara/mail\n"
            "    environment:\n"
            "      - ConnectionStrings__Postgres=Host=postgres;Username=platform;Password=${POSTGRES_PASSWORD}\n"
            + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("mail ConnectionStrings__Postgres pastes", r.stdout)

    def test_an_operator_supplied_whole_string_is_skipped(self):
        self._stack(_api(conn="${PG_CONNECTION_STRING}", pgpassword=None) + _api(name="mail") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("1 connection string(s)", r.stdout)

    def test_an_environment_compose_refuses_to_start_is_skipped(self):
        pg = _POSTGRES.replace("${POSTGRES_PASSWORD:-platform_secret}", "${POSTGRES_PASSWORD:?required}")
        self._stack(_api(pgpassword="${POSTGRES_PASSWORD:?required}") + pg)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_reports_every_file_not_only_the_first(self):
        bad = _api(_GOOD_CONN + ";Password=${POSTGRES_PASSWORD}") + _POSTGRES
        self._stack(bad)
        self._stack(bad, rel="docker/demo/docker-compose.demo.yml")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)
        self.assertIn("docker/demo/docker-compose.demo.yml", r.stdout)

    def test_a_helm_template_that_expands_the_password_fails(self):
        self._stack(_api() + _POSTGRES)
        self._write("infra/k8s/helm/platform/templates/api.yaml",
                    "            - name: ConnectionStrings__Postgres\n"
                    "              value: \"Host={{ .Values.host }};Username=u;Password=$(PG_PASSWORD)\"\n")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("infra/k8s/helm/platform/templates/api.yaml:2:", r.stdout)

    def test_exits_2_when_no_connection_string_is_found(self):
        self._stack("  redis:\n    image: redis:8-alpine\n")
        r = self._run()
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("no compose file", r.stderr)

    def test_exits_2_on_a_file_that_is_not_yaml(self):
        self._write("docker/docker-compose.broken.yml", "services: [\n")
        r = self._run()
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("cannot read", r.stderr)

    def test_this_repositorys_stacks_leave_the_password_to_pgpassword(self):
        r = self._run(_REPO)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)
        self.assertIn("docker/demo/docker-compose.demo.yml", r.stdout)
        self.assertIn("docker/docker-compose.reference-smb.yml", r.stdout)


if __name__ == "__main__":
    unittest.main()
