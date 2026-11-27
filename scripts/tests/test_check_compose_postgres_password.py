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

# The production stack's form: Postgres reads POSTGRES_PASSWORD from the same env file the services read PGPASSWORD from.
_POSTGRES_FROM_ENV_FILE = (
    "  postgres:\n"
    "    image: postgres:18-alpine\n"
    "    env_file:\n"
    "      - .env.production\n"
    "    environment:\n"
    "      POSTGRES_DB: verbara\n"
    "      POSTGRES_USER: platform\n"
)

_ENV_FILE_SERVICE = (
    "  platform-api:\n"
    "    image: verbara/api\n"
    "    env_file:\n"
    "      - .env.production\n"
)

_EXAMPLE = ("POSTGRES_PASSWORD=change_me\nPGPASSWORD=change_me\n"
            "ConnectionStrings__Postgres=Host=postgres;Database=verbara;Username=platform\n")

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


def _realtime(env="", build=True, env_file=None, name="realtime"):
    text = f"  {name}:\n"
    if build is True:
        text += "    build:\n      context: ..\n      dockerfile: src/Verbara.Platform.Realtime/Dockerfile.realtime\n"
    elif build:
        text += f"    image: {build}\n"
    if env_file:
        text += f"    env_file: {env_file}\n"
    text += "    environment:\n      ConnectionStrings__Redis: redis:6379\n" + env
    return text


_RT_PG = ("      ConnectionStrings__Postgres: \"Host=postgres;Database=verbara;Username=platform\"\n"
          "      PGPASSWORD: ${POSTGRES_PASSWORD:-platform_secret}\n")


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

    def test_a_helm_password_before_the_host_fails(self):
        self._stack(_api() + _POSTGRES)
        self._write("infra/k8s/helm/platform/templates/api.yaml",
                    "              value: \"Password=$(PGPASSWORD);Host={{ .Values.host }}\"\n")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("infra/k8s/helm/platform/templates/api.yaml:1:", r.stdout)

    def test_a_helm_password_in_a_helper_or_values_file_fails(self):
        self._stack(_api() + _POSTGRES)
        self._write("infra/k8s/helm/platform/templates/_helpers.tpl",
                    "{{- define \"pg\" -}}\nUsername=u;\n  Pwd={{ .Values.pw }}\n{{- end }}\n")
        self._write("infra/k8s/helm/platform/values.yaml", "conn: \"Host=h;Password={{ .Values.pw }}\"\n")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("infra/k8s/helm/platform/templates/_helpers.tpl:3:", r.stdout)
        self.assertIn("infra/k8s/helm/platform/values.yaml:1:", r.stdout)

    def test_a_root_compose_yaml_is_checked(self):
        self._stack(_api() + _POSTGRES)
        self._write("compose.yaml", "services:\n" + _api(_GOOD_CONN + ";Password=${POSTGRES_PASSWORD}") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("compose.yaml: platform-api", r.stdout)

    def test_an_env_file_connection_string_with_a_password_fails(self):
        self._stack(_api() + _POSTGRES)
        self._write("docker/.env.production.example",
                    "POSTGRES_PASSWORD=x\nConnectionStrings__Postgres=Host=postgres;Username=platform;Password=x\n")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/.env.production.example:2: ConnectionStrings__Postgres holds a Password", r.stdout)

    def test_an_env_file_connection_string_without_pgpassword_fails(self):
        self._stack(_api() + _POSTGRES)
        self._write("docker/.env.production.example",
                    "POSTGRES_PASSWORD=x\nConnectionStrings__Postgres=Host=postgres;Username=platform\n")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("sets no PGPASSWORD", r.stdout)

    def test_an_env_file_with_pgpassword_passes(self):
        self._stack(_api() + _POSTGRES)
        self._write("docker/.env.production.example",
                    "POSTGRES_PASSWORD=x\nPGPASSWORD=x\nConnectionStrings__Postgres=Host=postgres;Username=platform\n")
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/.env.production.example", r.stdout)

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

    def test_fails_when_the_realtime_service_has_no_postgres_connection_string(self):
        self._stack(_api() + _realtime() + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml: realtime runs the realtime image and has no "
                      "ConnectionStrings__Cluster or ConnectionStrings__Postgres", r.stdout)

    def test_a_realtime_image_is_recognised_by_its_name(self):
        self._stack(_api() + _realtime(build="ghcr.io/verbara/platform/realtime:${TAG:-v1}", name="rt") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("rt runs the realtime image", r.stdout)

    def test_the_realtime_service_passes_with_postgres_or_cluster(self):
        cluster = _RT_PG.replace("ConnectionStrings__Postgres", "ConnectionStrings__Cluster")
        for env in (_RT_PG, cluster):
            with self.subTest(env=env.split(":")[0].strip()):
                self._stack(_api() + _realtime(env) + _POSTGRES)
                r = self._run()
                self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_a_blank_realtime_connection_string_fails(self):
        self._stack(_api() + _realtime("      ConnectionStrings__Postgres: \"\"\n") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("realtime runs the realtime image", r.stdout)

    def test_a_blank_cluster_next_to_postgres_fails(self):
        # Program.cs reads Cluster ?? Postgres, and an empty Cluster is not null, so it never reaches Postgres.
        self._stack(_api() + _realtime(_RT_PG + "      ConnectionStrings__Cluster: \"\"\n") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("realtime ConnectionStrings__Cluster is blank, and realtime runs the realtime image", r.stdout)

    def test_a_realtime_connection_string_that_is_not_npgsql_fails(self):
        self._stack(_api() + _realtime("      ConnectionStrings__Postgres: redis:6379\n") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("realtime ConnectionStrings__Postgres", r.stdout)

    def test_an_undefaulted_variable_realtime_connection_string_fails(self):
        self._stack(_api() + _realtime("      ConnectionStrings__Postgres: ${PG_RT}\n") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("realtime ConnectionStrings__Postgres is '${PG_RT}', which is blank when no variable is set",
                      r.stdout)

    def test_a_defaulted_or_required_variable_realtime_connection_string_passes(self):
        for value in ("${PG_RT:-Host=postgres;Database=verbara;Username=platform}", "${PG_RT:?set PG_RT}"):
            with self.subTest(value=value):
                self._stack(_api() + _realtime(f"      ConnectionStrings__Postgres: \"{value}\"\n"
                                               "      PGPASSWORD: ${POSTGRES_PASSWORD:-platform_secret}\n")
                            + _POSTGRES)
                r = self._run()
                self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_environment_overrides_a_blank_env_file_cluster(self):
        self._stack(_api() + _realtime(_RT_PG.replace("Postgres:", "Cluster:", 1), env_file=".env.production")
                    + _POSTGRES)
        self._write("docker/.env.production.example", "ConnectionStrings__Cluster=\n")
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_a_blank_env_file_cluster_next_to_postgres_fails(self):
        self._stack(_api() + _realtime(_RT_PG, env_file=".env.production") + _POSTGRES)
        self._write("docker/.env.production.example", "ConnectionStrings__Cluster=\n")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("realtime ConnectionStrings__Cluster", r.stdout)

    def test_the_realtime_service_passes_with_an_env_file_that_holds_it(self):
        self._stack(_api() + _realtime(env_file=".env.production") + _POSTGRES_FROM_ENV_FILE)
        self._write("docker/.env.production.example",
                    "POSTGRES_PASSWORD=x\nPGPASSWORD=x\nConnectionStrings__Postgres=Host=postgres;Username=platform\n")
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_the_realtime_service_fails_with_an_env_file_that_lacks_it(self):
        self._stack(_api() + _realtime(env_file=".env.production") + _POSTGRES)
        self._write("docker/.env.production.example", "ConnectionStrings__Redis=redis:6379\n")
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("realtime runs the realtime image", r.stdout)

    def test_the_realtime_service_fails_with_an_env_file_not_in_the_tree(self):
        self._stack(_api() + _realtime(env_file=".env.missing") + _POSTGRES)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/.env.missing", r.stdout)

    def test_a_realtime_overlay_without_build_or_image_is_left_to_its_base(self):
        self._stack(_api() + _POSTGRES)
        self._write("docker/docker-compose.scale.yml", "services:\n" + _realtime(build=None))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    # A service that reads PGPASSWORD from an env_file needs a postgres service that reads the same file (#683).

    def test_fails_when_postgres_takes_its_password_from_interpolation_and_the_service_from_an_env_file(self):
        self._stack(_ENV_FILE_SERVICE + _POSTGRES)
        self._write("docker/.env.production.example", _EXAMPLE)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api take PGPASSWORD from .env.production", r.stdout)
        self.assertIn("give postgres env_file: .env.production", r.stdout)

    def test_passes_when_postgres_reads_the_same_env_file(self):
        self._stack(_ENV_FILE_SERVICE + _POSTGRES_FROM_ENV_FILE)
        self._write("docker/.env.production.example", _EXAMPLE)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_when_postgres_reads_another_env_file(self):
        self._stack(_ENV_FILE_SERVICE + _POSTGRES_FROM_ENV_FILE.replace(".env.production", ".env.other"))
        self._write("docker/.env.production.example", _EXAMPLE)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("postgres does not read .env.production", r.stdout)

    def test_fails_when_the_env_file_sets_two_different_passwords(self):
        self._stack(_ENV_FILE_SERVICE + _POSTGRES_FROM_ENV_FILE)
        self._write("docker/.env.production.example", _EXAMPLE.replace("PGPASSWORD=change_me", "PGPASSWORD=other"))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/.env.production.example: PGPASSWORD is 'other'", r.stdout)

    def test_the_compose_dotenv_is_read_by_interpolation(self):
        self._stack(_ENV_FILE_SERVICE.replace(".env.production", ".env") + _POSTGRES)
        self._write("docker/.env", _EXAMPLE)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_the_compose_dotenv_with_two_different_passwords_fails(self):
        self._stack(_ENV_FILE_SERVICE.replace(".env.production", ".env") + _POSTGRES)
        self._write("docker/.env", _EXAMPLE.replace("PGPASSWORD=change_me", "PGPASSWORD=other"))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_a_service_with_its_own_pgpassword_is_left_to_the_environment_rule(self):
        self._stack(_api() + _POSTGRES)
        self._write("docker/.env.production.example", _EXAMPLE)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_this_repositorys_stacks_leave_the_password_to_pgpassword(self):
        r = self._run(_REPO)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)
        self.assertIn("docker/demo/docker-compose.demo.yml", r.stdout)
        self.assertIn("docker/docker-compose.reference-smb.yml", r.stdout)
        self.assertIn("docker/.env.production.example", r.stdout)


if __name__ == "__main__":
    unittest.main()
