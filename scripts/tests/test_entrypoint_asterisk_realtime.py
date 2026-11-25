"""Unit tests for the realtime block of docker/entrypoint-asterisk.sh: what it writes into res_pgsql.conf reaches
Postgres unchanged, whatever characters the operator's database, user or password hold.

The block is cut out of the entrypoint (from its `# Realtime Postgres rewiring` comment to the EXTERNAL_IP block) and
run with `sh` against a copy of docker/asterisk-config/res_pgsql.conf.template. The rendered file is then read the
way Asterisk 22 does:
  - main/config.c: a `;` starts a comment unless a backslash precedes it, and that backslash is dropped; the value
    after `=` is stripped of surrounding whitespace;
  - res/res_config_pgsql.c: the connection string is built with
    "host=%s port=%d dbname=%s user=%s" and " password=%s", with no quoting, and handed to PQconnectdb;
  - libpq's conninfo parser: an unquoted value ends at whitespace, a single-quoted one at the closing quote, and in
    both a backslash takes the next character literally.
"""
import os
import shutil
import subprocess
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))
_ENTRYPOINT = os.path.join(_REPO, "docker", "entrypoint-asterisk.sh")
_TEMPLATE = os.path.join(_REPO, "docker", "asterisk-config", "res_pgsql.conf.template")

_START = "# Realtime Postgres rewiring"
_END = 'if [ -n "$EXTERNAL_IP" ]'


def _realtime_block():
    with open(_ENTRYPOINT, encoding="utf-8") as fh:
        text = fh.read()
    start = text.index(_START)
    end = text.index(_END, start)
    return text[start:end]


def _asterisk_value(conf_text, key):
    """The value Asterisk's config parser reads for `key` in [general]."""
    for raw in conf_text.splitlines():
        out = []
        i = 0
        while i < len(raw):
            c = raw[i]
            if c == ";":
                if out and out[-1] == "\\":
                    out[-1] = ";"   # an escaped semicolon is not a comment; the backslash is dropped
                    i += 1
                    continue
                break
            out.append(c)
            i += 1
        line = "".join(out)
        if "=" not in line:
            continue
        name, value = line.split("=", 1)
        if name.strip() == key:
            return value.strip()
    raise AssertionError(f"{key} not found in res_pgsql.conf")


def _conninfo(text):
    """libpq's conninfo_parse: keyword=value pairs."""
    params = {}
    i, n = 0, len(text)
    while i < n:
        while i < n and text[i].isspace():
            i += 1
        if i >= n:
            break
        start = i
        while i < n and not text[i].isspace() and text[i] != "=":
            i += 1
        name = text[start:i]
        while i < n and text[i].isspace():
            i += 1
        if i >= n or text[i] != "=":
            raise ValueError(f"missing '=' after {name!r} in connection info string")
        i += 1
        while i < n and text[i].isspace():
            i += 1
        value = []
        if i < n and text[i] == "'":
            i += 1
            while True:
                if i >= n:
                    raise ValueError("unterminated quoted string in connection info string")
                if text[i] == "\\":
                    i += 1
                    if i < n:
                        value.append(text[i])
                        i += 1
                elif text[i] == "'":
                    i += 1
                    break
                else:
                    value.append(text[i])
                    i += 1
        else:
            while i < n and not text[i].isspace():
                if text[i] == "\\":
                    i += 1
                    if i < n:
                        value.append(text[i])
                        i += 1
                else:
                    value.append(text[i])
                    i += 1
        params[name] = "".join(value)
    return params


class EntrypointRealtimeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.conf = os.path.join(self.tmp, "res_pgsql.conf")
        self.block = os.path.join(self.tmp, "block.sh")
        with open(self.block, "w", encoding="utf-8") as fh:
            fh.write(_realtime_block())

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def _render(self, **env):
        shutil.copyfile(_TEMPLATE, self.conf)
        run_env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "PGSQL_CONF": self.conf}
        run_env.update(env)
        result = subprocess.run(["sh", self.block], env=run_env, capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stderr, "", "the block writes no error")
        with open(self.conf, encoding="utf-8") as fh:
            return fh.read()

    def _libpq_params(self, conf_text):
        # res_config_pgsql.c builds exactly this string.
        info = "host={} port={} dbname={} user={}".format(
            _asterisk_value(conf_text, "dbhost"), int(_asterisk_value(conf_text, "dbport")),
            _asterisk_value(conf_text, "dbname"), _asterisk_value(conf_text, "dbuser"))
        info += " password={}".format(_asterisk_value(conf_text, "dbpass"))
        return _conninfo(info)

    def test_realtime_block_should_hand_postgres_the_exact_values_when_they_hold_special_characters(self):
        values = [
            "platform_secret",
            "a/b+c=d",
            "a&b",
            "a;b",
            "a b",
            "a'b",
            "a\\b",
            "a\\;b",
            "a|b#c$d",
            "Zq8/kL+3&w;x y'z\\",
        ]
        for value in values:
            with self.subTest(value=value):
                conf = self._render(
                    PG_REALTIME_DB="db_" + value, PG_REALTIME_USER="user_" + value, PG_REALTIME_PASSWORD=value)
                params = self._libpq_params(conf)
                self.assertEqual(params["password"], value)
                self.assertEqual(params["dbname"], "db_" + value)
                self.assertEqual(params["user"], "user_" + value)

    def test_realtime_block_should_write_plain_values_unchanged_when_they_need_no_escaping(self):
        conf = self._render(
            PG_REALTIME_HOST="127.0.0.1", PG_REALTIME_PORT="5433", PG_REALTIME_DB="verbara",
            PG_REALTIME_USER="platform", PG_REALTIME_PASSWORD="platform_secret")
        lines = conf.splitlines()
        for expected in ["dbhost = 127.0.0.1", "dbport = 5433", "dbname = verbara", "dbuser = platform",
                         "dbpass = platform_secret"]:
            self.assertIn(expected, lines)

    def test_realtime_block_should_keep_the_template_values_when_no_variable_is_set(self):
        conf = self._render()
        with open(_TEMPLATE, encoding="utf-8") as fh:
            self.assertEqual(conf, fh.read())


if __name__ == "__main__":
    unittest.main()
