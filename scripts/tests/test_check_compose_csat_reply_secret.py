"""Unit tests for check-compose-csat-reply-secret.py (the Api and the mail service use one CSAT reply secret).

Runs the script as a subprocess against a small repository tree written to a temp directory (`--root`), and once
against this repository's own tree. Needs PyYAML, which the `coverage-scripts` CI job installs.
"""
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-compose-csat-reply-secret.py")
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))

_SHARED = "${CSAT_REPLY_TOKEN_SECRET:-local_dev_csat_reply_token_secret}"


def _api(secret=_SHARED, key="Csat__Email__TokenSigningSecret", build=True):
    text = "  platform-api:\n"
    text += ("    build:\n      context: ..\n      dockerfile: Dockerfile\n" if build
             else "    image: ghcr.io/verbara/platform/api:v1.0.0\n")
    text += "    environment:\n      ASPNETCORE_ENVIRONMENT: Production\n"
    if secret is not None:
        text += f"      {key}: {secret}\n"
    return text


def _mail(secret=_SHARED, build=True):
    text = "  mail:\n"
    text += ("    build:\n      context: ..\n      dockerfile: src/Verbara.Platform.Mail/Dockerfile.mail\n" if build
             else "    image: ghcr.io/verbara/platform/mail:v1.0.0\n")
    text += "    environment:\n      ASPNETCORE_URLS: http://+:5020\n"
    if secret is not None:
        text += f"      Imap__TokenSigningSecret: {secret}\n"
    return text


class CheckComposeCsatReplySecretTests(unittest.TestCase):
    def _run(self, files):
        with tempfile.TemporaryDirectory() as root:
            for rel, text in files.items():
                path = os.path.join(root, rel)
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w", encoding="utf-8") as fh:
                    fh.write(text)
            return subprocess.run([sys.executable, _SCRIPT, "--root", root], capture_output=True, text=True)

    def _stack(self, *services):
        return {"docker/docker-compose.lab.yml": "services:\n" + "".join(services)}

    def test_repository_tree_passes(self):
        r = subprocess.run([sys.executable, _SCRIPT, "--root", _REPO], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.full.yml", r.stdout)
        self.assertIn("docker/docker-compose.reference-smb.yml", r.stdout)

    def test_same_variable_passes(self):
        r = self._run(self._stack(_api(), _mail()))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_api_without_secret_fails(self):
        # The full stack before #718: the mail service reads the variable, the Api sets nothing.
        r = self._run(self._stack(_api(secret=None), _mail()))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api sets no Csat__Email__TokenSigningSecret", r.stdout)

    def test_different_default_fails(self):
        r = self._run(self._stack(_api(secret="${CSAT_REPLY_TOKEN_SECRET:-another_default_value_of_32_chars}"),
                                  _mail()))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("(no variables set)", r.stdout)

    def test_different_variable_fails(self):
        # Equal defaults, different variables: an operator's .env sets one of them and the two part.
        r = self._run(self._stack(_api(secret="${API_CSAT_SECRET:-local_dev_csat_reply_token_secret}"), _mail()))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("set in .env", r.stdout)

    def test_webchat_fallback_counts_as_the_api_secret(self):
        r = self._run(self._stack(_api(key="Csat__WebChatTokenSecret"), _mail()))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_required_variable_on_both_passes(self):
        req = "${CSAT_REPLY_TOKEN_SECRET:?CSAT_REPLY_TOKEN_SECRET required}"
        r = self._run(self._stack(_api(secret=req, build=False), _mail(secret=req, build=False)))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_released_images_are_recognised(self):
        r = self._run(self._stack(_api(secret=None, build=False), _mail(build=False)))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_mail_without_secret_is_not_checked(self):
        r = self._run(self._stack(_api(secret=None), _mail(secret=None)))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def test_overlay_without_api_is_not_checked(self):
        r = self._run(self._stack(_mail()))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def test_console_dockerfile_is_not_the_api(self):
        web = "  web:\n    build:\n      context: ../../Verbara.Platform.Web\n      dockerfile: Dockerfile\n"
        r = self._run(self._stack(web, _mail()))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def _env_file_stack(self, example):
        services = ("services:\n"
                    "  platform-api:\n    build:\n      context: ..\n      dockerfile: Dockerfile\n"
                    "    env_file:\n      - .env.production\n"
                    "  mail:\n    build:\n      context: ..\n      dockerfile: src/Verbara.Platform.Mail/Dockerfile.mail\n"
                    "    env_file:\n      - .env.production\n")
        return {"docker/docker-compose.production.yml": services, "docker/.env.production.example": example}

    def test_env_file_with_equal_values_passes(self):
        r = self._run(self._env_file_stack("Csat__Email__TokenSigningSecret=abc\nImap__TokenSigningSecret=abc\n"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_env_file_with_different_values_fails(self):
        r = self._run(self._env_file_stack("Csat__Email__TokenSigningSecret=abc\nImap__TokenSigningSecret=xyz\n"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_environment_wins_over_env_file(self):
        files = self._env_file_stack("Csat__Email__TokenSigningSecret=abc\nImap__TokenSigningSecret=abc\n")
        files["docker/docker-compose.production.yml"] += "    environment:\n      Imap__TokenSigningSecret: xyz\n"
        r = self._run(files)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()
