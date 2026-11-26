"""Unit tests for check-asterisk-realtime.py (every shipped stack's Asterisk reads realtime from its own Postgres).

Runs the script as a subprocess against a small repository tree written to a temp directory (`--root`) with the two
Kubernetes charts given already rendered (`--rendered-asterisk`, `--rendered-platform`), and once against this
repository's own tree, which renders the charts with `helm template`. Needs PyYAML, which the `coverage-scripts` CI
job installs, and helm for the last case (the GitHub-hosted runner has it).
"""
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest

import yaml

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


_ENTRYPOINT = textwrap.dedent("""\
    #!/bin/sh
    PGSQL_CONF="/etc/asterisk/res_pgsql.conf"
    [ -n "$PG_REALTIME_HOST" ] && set_realtime dbhost "$PG_REALTIME_HOST"
    [ -n "$PG_REALTIME_PORT" ] && set_realtime dbport "$PG_REALTIME_PORT"
    [ -n "$PG_REALTIME_DB" ] && set_realtime dbname "$PG_REALTIME_DB"
    [ -n "$PG_REALTIME_USER" ] && set_realtime dbuser "$PG_REALTIME_USER"
    [ -n "$PG_REALTIME_PASSWORD" ] && set_realtime dbpass "$PG_REALTIME_PASSWORD"
    """)

_CHART_COMMAND = (
    "set -eu\n"
    "awk '/^dbhost = / { print ENVIRON[\"PG_REALTIME_HOST\"] } /^dbport = / { print ENVIRON[\"PG_REALTIME_PORT\"] }\n"
    "  /^dbname = / { print ENVIRON[\"PG_REALTIME_DB\"] } /^dbuser = / { print ENVIRON[\"PG_REALTIME_USER\"] }\n"
    "  /^dbpass = / { print ENVIRON[\"PG_REALTIME_PASSWORD\"] }' /etc/asterisk/res_pgsql.conf.template"
    " > /etc/asterisk/res_pgsql.conf\n"
    "exec asterisk -fpvvv\n")


def _asterisk_chart():
    """The asterisk chart's rendered shape this check reads (`helm template infra/k8s/helm/asterisk`)."""
    mounts = [{"name": "config", "mountPath": "/etc/asterisk/" + k, "subPath": k}
              for k in ("pjsip.conf", "res_pgsql.conf.template", "extconfig.conf", "sorcery.conf")]
    return [
        {"kind": "Secret", "metadata": {"name": "asterisk-pg-credentials"},
         "stringData": {"username": "platform", "password": "pg_probe_secret"}},
        {"kind": "ConfigMap", "metadata": {"name": "asterisk-config"}, "data": {
            "pjsip.conf": "[anonymous]\ntype=endpoint\n",
            "res_pgsql.conf.template": "[general]\ndbhost = x\ndbport = x\ndbname = x\ndbuser = x\ndbpass = x\n",
            "extconfig.conf": "[settings]\nps_endpoints => pgsql,general,ps_endpoints\n",
            "sorcery.conf": "[res_pjsip]\nendpoint = config,pjsip.conf,criteria=type=endpoint\n"
                            "endpoint = realtime,ps_endpoints\n",
        }},
        {"kind": "StatefulSet", "metadata": {"name": "asterisk"}, "spec": {"template": {"spec": {
            "containers": [{
                "name": "asterisk",
                "command": ["sh", "-c", _CHART_COMMAND],
                "env": [
                    {"name": "PG_REALTIME_HOST", "value": "postgres-pooler.r55-data.svc.cluster.local"},
                    {"name": "PG_REALTIME_PORT", "value": "5432"},
                    {"name": "PG_REALTIME_DB", "value": "verbara"},
                    {"name": "PG_REALTIME_USER", "value": "platform"},
                    {"name": "PG_REALTIME_PASSWORD", "valueFrom": {"secretKeyRef": {
                        "name": "asterisk-pg-credentials", "key": "password"}}},
                ],
                "volumeMounts": mounts,
            }],
            "volumes": [{"name": "config", "configMap": {"name": "asterisk-config"}}],
        }}}},
    ]


def _platform_chart():
    """The platform chart's rendered shape this check reads (`helm template infra/k8s/helm/platform`)."""
    return [
        {"kind": "Secret", "metadata": {"name": "platform-pg-credentials"},
         "stringData": {"username": "platform", "password": "pg_probe_secret"}},
        {"kind": "Deployment", "metadata": {"name": "platform-api"}, "spec": {"template": {"spec": {
            "containers": [{"name": "api", "env": [
                {"name": "PGPASSWORD", "valueFrom": {"secretKeyRef": {
                    "name": "platform-pg-credentials", "key": "password"}}},
                {"name": "ConnectionStrings__Postgres",
                 "value": "Host=postgres-pooler.r55-data.svc.cluster.local;Port=5432;Database=verbara;"
                          "Username=platform"},
            ]}]}}}},
    ]


def _doc(docs, kind):
    return next(d for d in docs if d["kind"] == kind)


def _env(docs, name):
    c = _doc(docs, "StatefulSet")["spec"]["template"]["spec"]["containers"][0]
    return next(e for e in c["env"] if e["name"] == name)


class CheckAsteriskRealtimeTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = self._tmp.name
        os.makedirs(os.path.join(self.root, "docker", "asterisk-config"))
        self._write("docker/asterisk-config/res_pgsql.conf.template", _TEMPLATE)
        self._write("docker/entrypoint-asterisk.sh", _ENTRYPOINT)
        self.asterisk_chart = _asterisk_chart()
        self.platform_chart = _platform_chart()

    def _write(self, rel, text):
        path = os.path.join(self.root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)

    def _stack(self, asterisk, postgres=_POSTGRES, rel="docker/docker-compose.full.yml"):
        self._write(rel, "services:\n" + asterisk + postgres)

    def _run(self, root=None, rendered=True, env=None):
        args = [sys.executable, _SCRIPT, "--root", root or self.root]
        if rendered:
            for flag, docs in (("--rendered-asterisk", self.asterisk_chart),
                               ("--rendered-platform", self.platform_chart)):
                path = os.path.join(self.root, flag.lstrip("-") + ".yaml")
                with open(path, "w", encoding="utf-8") as fh:
                    yaml.safe_dump_all(docs, fh)
                args += [flag, path]
        run_env = dict(os.environ)
        run_env.update(env or {})
        return subprocess.run(args, capture_output=True, text=True, check=False, env=run_env)

    def _chart_run(self):
        """A passing compose stack, so only the chart decides the outcome."""
        self._stack(_asterisk(_REALTIME_ENV))
        return self._run()

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

    def test_chart_passes_when_its_asterisk_renders_realtime_from_the_platforms_postgres(self):
        r = self._chart_run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("infra/k8s/helm/asterisk chart renders res_pgsql.conf", r.stdout)

    def test_chart_fails_when_it_passes_the_pg_names_the_entrypoint_never_reads(self):
        c = _doc(self.asterisk_chart, "StatefulSet")["spec"]["template"]["spec"]["containers"][0]
        for e in c["env"]:
            e["name"] = {"PG_REALTIME_HOST": "PG_HOST", "PG_REALTIME_PORT": "PG_PORT", "PG_REALTIME_DB": "PG_DATABASE",
                         "PG_REALTIME_USER": "PG_USER", "PG_REALTIME_PASSWORD": "PG_PASSWORD"}[e["name"]]
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        for var in ("PG_REALTIME_HOST", "PG_REALTIME_PORT", "PG_REALTIME_DB", "PG_REALTIME_USER",
                    "PG_REALTIME_PASSWORD"):
            self.assertIn(f"container asterisk: does not set {var}, which the entrypoint reads", r.stdout)

    def test_chart_fails_when_a_variable_is_empty(self):
        _env(self.asterisk_chart, "PG_REALTIME_DB")["value"] = ""
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("does not set PG_REALTIME_DB", r.stdout)

    def test_chart_requires_a_variable_the_entrypoint_starts_reading(self):
        self._write("docker/entrypoint-asterisk.sh", _ENTRYPOINT + 'set_realtime sslmode "${PG_REALTIME_SSLMODE}"\n')
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("does not set PG_REALTIME_SSLMODE", r.stdout)
        self.assertIn("never reads PG_REALTIME_SSLMODE", r.stdout)

    def test_chart_fails_when_its_command_does_not_render_res_pgsql_conf(self):
        c = _doc(self.asterisk_chart, "StatefulSet")["spec"]["template"]["spec"]["containers"][0]
        c["command"] = ["sh", "-c", "env | grep PG_REALTIME_HOST PG_REALTIME_PORT PG_REALTIME_DB PG_REALTIME_USER "
                                    "PG_REALTIME_PASSWORD; exec asterisk -fpvvv"]
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("does not render /etc/asterisk/res_pgsql.conf from res_pgsql.conf.template", r.stdout)

    def test_chart_fails_when_its_command_never_reads_the_password(self):
        c = _doc(self.asterisk_chart, "StatefulSet")["spec"]["template"]["spec"]["containers"][0]
        c["command"][2] = c["command"][2].replace('ENVIRON[\"PG_REALTIME_PASSWORD\"]', '"x"')
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("never reads PG_REALTIME_PASSWORD", r.stdout)
        self.assertNotIn("never reads PG_REALTIME_DB", r.stdout)

    def test_chart_without_a_command_leaves_the_rendering_to_the_image_entrypoint(self):
        c = _doc(self.asterisk_chart, "StatefulSet")["spec"]["template"]["spec"]["containers"][0]
        del c["command"]
        r = self._chart_run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_chart_fails_when_the_template_is_not_mounted(self):
        sts = _doc(self.asterisk_chart, "StatefulSet")["spec"]["template"]["spec"]
        sts["containers"][0]["volumeMounts"] = [
            m for m in sts["containers"][0]["volumeMounts"] if m["subPath"] != "res_pgsql.conf.template"]
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("mounts no ConfigMap key at /etc/asterisk/res_pgsql.conf.template", r.stdout)

    def test_chart_fails_when_the_template_has_no_line_for_the_password(self):
        cm = _doc(self.asterisk_chart, "ConfigMap")
        cm["data"]["res_pgsql.conf.template"] = cm["data"]["res_pgsql.conf.template"].replace("dbpass = x\n", "")
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("res_pgsql.conf.template has no `dbpass = ` line", r.stdout)

    def test_chart_fails_when_sorcery_or_extconfig_do_not_map_endpoints_to_realtime(self):
        cm = _doc(self.asterisk_chart, "ConfigMap")
        cm["data"]["sorcery.conf"] = "[res_pjsip]\nendpoint = config,pjsip.conf,criteria=type=endpoint\n"
        cm["data"]["extconfig.conf"] = "[settings]\n;ps_endpoints => pgsql,general,ps_endpoints\n"
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("sorcery.conf does not map endpoint = realtime,ps_endpoints", r.stdout)
        self.assertIn("extconfig.conf does not map ps_endpoints => pgsql", r.stdout)

    def test_chart_fails_when_a_cache_follows_the_pjsip_conf_mapping(self):
        cm = _doc(self.asterisk_chart, "ConfigMap")
        cm["data"]["sorcery.conf"] = (
            "[res_pjsip]\nendpoint = config,pjsip.conf,criteria=type=endpoint\n"
            "endpoint/cache = memory_cache,name=c\nendpoint = realtime,ps_endpoints\n")
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("the endpoint cache comes after another endpoint mapping", r.stdout)
        cm["data"]["sorcery.conf"] = (
            "[res_pjsip]\nendpoint/cache = memory_cache,name=c\n"
            "endpoint = config,pjsip.conf,criteria=type=endpoint\nendpoint = realtime,ps_endpoints\n")
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_chart_fails_when_the_database_is_not_the_platforms(self):
        _env(self.asterisk_chart, "PG_REALTIME_DB")["value"] = "platform"
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("PG_REALTIME_DB is 'platform', platform-api's Postgres database is 'verbara'", r.stdout)

    def test_chart_fails_when_the_password_secrets_disagree(self):
        _doc(self.platform_chart, "Secret")["stringData"]["password"] = "another"
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("PG_REALTIME_PASSWORD's Secret asterisk-pg-credentials holds another password", r.stdout)

    def test_chart_fails_when_platform_api_reads_no_password_from_a_secret(self):
        api = _doc(self.platform_chart, "Deployment")["spec"]["template"]["spec"]["containers"][0]
        api["env"] = [e for e in api["env"] if e["name"] != "PGPASSWORD"]
        r = self._chart_run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api reads no PGPASSWORD from a Secret", r.stdout)

    def test_chart_fails_when_the_rendered_platform_chart_holds_another_password(self):
        """The real platform render with only its password changed: the comparison must reach it."""
        text = subprocess.run([os.environ.get("CHECK_ASTERISK_REALTIME_HELM", "helm"), "template",
                               os.path.join(_REPO, "infra", "k8s", "helm", "platform")],
                              capture_output=True, text=True, check=True).stdout
        docs = [d for d in yaml.safe_load_all(text) if isinstance(d, dict)]
        api = next(d for d in docs if d.get("kind") == "Deployment" and d["metadata"]["name"] == "platform-api")
        env = next(c for c in api["spec"]["template"]["spec"]["containers"] if c["name"] == "api")["env"]
        ref = next(e for e in env if e["name"] == "PGPASSWORD")["valueFrom"]["secretKeyRef"]
        secret = next(d for d in docs if d.get("kind") == "Secret" and d["metadata"]["name"] == ref["name"])
        secret["stringData"][ref["key"]] = "other"
        path = os.path.join(self.root, "platform.yaml")
        with open(path, "w", encoding="utf-8") as fh:
            yaml.safe_dump_all(docs, fh)
        r = subprocess.run([sys.executable, _SCRIPT, "--root", _REPO, "--rendered-platform", path],
                           capture_output=True, text=True, check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("holds another password than platform-api's Secret " + ref["name"], r.stdout)

    def test_chart_password_is_not_compared_when_the_operator_creates_the_secret(self):
        self.asterisk_chart = [d for d in self.asterisk_chart if d["kind"] != "Secret"]
        r = self._chart_run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_exits_2_when_helm_cannot_run(self):
        self._stack(_asterisk(_REALTIME_ENV))
        r = self._run(rendered=False, env={"CHECK_ASTERISK_REALTIME_HELM": os.path.join(self.root, "no-helm")})
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("cannot run", r.stderr)

    def test_this_repositorys_stacks_point_asterisk_at_their_own_postgres(self):
        r = self._run(_REPO, rendered=False)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)
        self.assertIn("infra/k8s/helm/asterisk chart renders res_pgsql.conf", r.stdout)


if __name__ == "__main__":
    unittest.main()
