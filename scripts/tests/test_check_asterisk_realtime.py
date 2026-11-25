"""Unit tests for check-asterisk-realtime.py (every shipped stack's Asterisk reads realtime from its own Postgres).

Runs the script as a subprocess against a small repository tree written to a temp directory (`--root`), and once
against this repository's own docker/ tree. Needs PyYAML, which the `coverage-scripts` CI job installs.
"""
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-asterisk-realtime.py")
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))

_TEMPLATE = textwrap.dedent("""\
    [general]
    dbhost = postgres
    dbport = 5432
    dbname = platform
    dbuser = platform
    dbpass = platform_secret
    requirements = createclose
    """)

_POSTGRES = (
    "  postgres:\n"
    "    image: postgres:18-alpine\n"
    "    environment:\n"
    "      POSTGRES_DB: verbara\n"
    "      POSTGRES_USER: platform\n"
    "      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-platform_secret}\n"
)

_REALTIME_ENV = textwrap.dedent("""\
          PG_REALTIME_DB: verbara
          PG_REALTIME_USER: platform
          PG_REALTIME_PASSWORD: ${POSTGRES_PASSWORD:-platform_secret}
    """)


def _asterisk(env="", extra="", config="./asterisk-config"):
    return (
        "  asterisk:\n"
        "    build:\n"
        "      context: .\n"
        "      dockerfile: Dockerfile.asterisk\n"
        + extra
        + "    environment:\n"
        "      AMI_PASSWORD: ${AMI_PASSWORD:-platform_secret}\n"
        + textwrap.indent(textwrap.dedent(env), "      ")
        + "    volumes:\n"
        f"      - {config}:/etc/asterisk\n"
    )


class CheckAsteriskRealtimeTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = self._tmp.name
        os.makedirs(os.path.join(self.root, "docker", "asterisk-config"))
        self._write("docker/asterisk-config/res_pgsql.conf.template", _TEMPLATE)

    def _write(self, rel, text):
        path = os.path.join(self.root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)

    def _stack(self, asterisk, postgres=_POSTGRES, rel="docker/docker-compose.full.yml"):
        self._write(rel, "services:\n" + asterisk + postgres)

    def _run(self, root=None):
        return subprocess.run([sys.executable, _SCRIPT, "--root", root or self.root],
                              capture_output=True, text=True, check=False)

    def test_passes_when_the_realtime_variables_follow_postgres(self):
        self._stack(_asterisk(_REALTIME_ENV))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)

    def test_fails_on_the_full_stack_shape_without_realtime_variables(self):
        self._stack(_asterisk())
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml (no variables set): asterisk reads realtime dbname 'platform'",
                      r.stdout)
        self.assertIn("'verbara'", r.stdout)
        self.assertIn("PG_REALTIME_DB", r.stdout)

    def test_fails_when_the_password_is_fixed_and_the_operator_sets_their_own(self):
        env = _REALTIME_ENV.replace("${POSTGRES_PASSWORD:-platform_secret}", "platform_secret")
        self._stack(_asterisk(env))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("(POSTGRES_* set in .env): asterisk reads realtime dbpass 'platform_secret'", r.stdout)
        self.assertNotIn("(no variables set)", r.stdout)

    def test_fails_when_a_variable_follows_the_wrong_postgres_variable(self):
        pg = _POSTGRES.replace("POSTGRES_DB: verbara", "POSTGRES_DB: ${POSTGRES_DB:-verbara}")
        self._stack(_asterisk(_REALTIME_ENV), postgres=pg)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("dbname 'verbara', the stack's Postgres has 'probe_db'", r.stdout)

    def test_postgres_database_defaults_to_the_user(self):
        pg = _POSTGRES.replace("      POSTGRES_DB: verbara\n", "")
        self._stack(_asterisk(_REALTIME_ENV), postgres=pg)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("dbname 'verbara', the stack's Postgres has 'platform'", r.stdout)

    def test_fails_when_the_host_is_not_the_postgres_service_on_a_bridge_network(self):
        self._stack(_asterisk(_REALTIME_ENV + "PG_REALTIME_HOST: 127.0.0.1\n"))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("dbhost '127.0.0.1'", r.stdout)

    def test_host_network_leaves_host_and_port_to_the_operator(self):
        env = _REALTIME_ENV + "PG_REALTIME_HOST: ${PG_REALTIME_HOST:-127.0.0.1}\nPG_REALTIME_PORT: '5433'\n"
        self._stack(_asterisk(env, extra="    network_mode: host\n"))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_an_environment_compose_refuses_to_start_is_skipped(self):
        env = _REALTIME_ENV.replace("${POSTGRES_PASSWORD:-platform_secret}", "${POSTGRES_PASSWORD:?required}")
        pg = _POSTGRES.replace("${POSTGRES_PASSWORD:-platform_secret}", "${POSTGRES_PASSWORD}")
        self._stack(_asterisk(env), postgres=pg)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_reads_the_list_form_and_a_template_mounted_from_a_parent_directory(self):
        asterisk = (
            "  asterisk:\n"
            "    build:\n"
            "      context: ..\n"
            "      dockerfile: Dockerfile.asterisk\n"
            "    environment:\n"
            "      - PG_REALTIME_PASSWORD=${POSTGRES_PASSWORD:-platform_secret}\n"
            "    volumes:\n"
            "      - ../asterisk-config:/etc/asterisk\n"
        )
        pg = _POSTGRES.replace("POSTGRES_DB: verbara", "POSTGRES_DB: platform")
        self._stack(asterisk, postgres=pg, rel="docker/demo/docker-compose.demo.yml")
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/demo/docker-compose.demo.yml", r.stdout)

    def test_a_service_from_another_image_is_not_checked(self):
        asterisk = "  asterisk:\n    image: asterisk:22-loadtest\n"
        self._stack(asterisk)
        r = self._run()
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("no compose file", r.stderr)

    def test_exits_2_when_the_template_is_missing(self):
        self._stack(_asterisk(_REALTIME_ENV, config="./nowhere"))
        r = self._run()
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("res_pgsql.conf.template", r.stderr)

    def test_reports_every_stack_not_only_the_first(self):
        self._stack(_asterisk())
        self._stack(_asterisk(), rel="docker/docker-compose.other.yml")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)
        self.assertIn("docker/docker-compose.other.yml", r.stdout)

    def test_this_repositorys_stacks_point_asterisk_at_their_own_postgres(self):
        r = self._run(_REPO)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)


if __name__ == "__main__":
    unittest.main()
