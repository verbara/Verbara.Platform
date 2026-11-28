"""Unit tests for check-env-example-startup-settings.py (an env file lists every setting its services need to start).

Runs the script as a subprocess against a small repository tree written to a temp directory (`--root`), and once
against this repository's own tree. Needs PyYAML, which the `coverage-scripts` CI job installs.
"""
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-env-example-startup-settings.py")
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))

# The shape of the Api's production check in Program.cs: one key read through an outer variable, one through a
# fallback, and two that apply only while a third has a value.
_PROGRAM = """
var serviceKey = builder.Configuration["Services:ServiceKey"] ?? "";
var corsOrigins = builder.Configuration["CORS_ORIGINS"]?.Split(',') ?? new[] { "*" };
var unrelated = builder.Configuration["Unrelated:Key"];

if (app.Environment.IsProduction())
{
    var configErrors = new List<string>();

    // a comment naming unrelated
    if (string.IsNullOrEmpty(serviceKey) || serviceKey == "platform_internal_secret")
        configErrors.Add("Services:ServiceKey must be configured, unrelated to anything");

    if (corsOrigins.Contains("*"))
        configErrors.Add("CORS_ORIGINS must not contain '*' in production");

    var amiHost = builder.Configuration["Asterisk:Ami:Hostname"];
    if (!string.IsNullOrEmpty(amiHost))
    {
        var amiUser = builder.Configuration["Asterisk:Ami:Username"];
        var amiPass = builder.Configuration["Asterisk:Ami:Password"];
        if (string.IsNullOrEmpty(amiUser) || string.IsNullOrEmpty(amiPass))
            configErrors.Add($"required {string.Join(",", configErrors)}");
    }

    if (configErrors.Count > 0)
        throw new InvalidOperationException("Production configuration errors");
}
"""

_APPSETTINGS = '{ "Asterisk": { "Ami": { "Hostname": "localhost", "Port": 5038 } } }\n'

# The shape of the mail service's reply-mailbox check: a switch, settings required while it is on (one per mailbox,
# by index) and one that falls back to a setting required elsewhere.
_SWITCH_VALIDATOR = """
internal sealed class CsatReplyMailboxSettingsValidator : IValidateOptions<ImapPollerOptions>
{
    internal const string SwitchKey = "Imap:Enabled";

    public ValidateOptionsResult Validate(string? name, ImapPollerOptions options)
    {
        Require(failures, "CsatCapture:ApiBaseAddress", forward.ApiBaseAddress);
        RequireEither(failures, "CsatCapture:ServiceKey", "Services:ServiceKey", forward.ServiceKey);
        for (var i = 0; i < count; i++)
        {
            Require(failures, $"Imap:Mailboxes:{i}:Host", mailbox?.Host);
        }
    }

    private static void Require(List<string> failures, string key, string? value) { }
}
"""

_MAILBOX_LISTED = (
    "# Imap__Enabled=true\n"
    "# CsatCapture__ApiBaseAddress=http://platform-api:5000\n"
    "# Imap__Mailboxes__0__Host=\n"
)

_MAILBOX_ON = (
    "Imap__Enabled=true\n"
    "CsatCapture__ApiBaseAddress=http://platform-api:5000\n"
    "Imap__Mailboxes__0__Host=imap.example.test\n"
)

_COMPLETE = (
    "CORS_ORIGINS=https://your-domain.com\n"
    "Services__ServiceKey=\n"
    "Asterisk__Ami__Username=\n"
    "Asterisk__Ami__Password=\n"
)


def _service(name, dockerfile, env_file=".env.production", environment=None):
    text = f"  {name}:\n    build:\n      context: ..\n      dockerfile: {dockerfile}\n"
    if env_file:
        text += f"    env_file:\n      - {env_file}\n"
    if environment:
        text += "    environment:\n" + "".join(f"      - {line}\n" for line in environment)
    return text


_API = _service("platform-api", "Dockerfile", environment=["ASPNETCORE_ENVIRONMENT=Production"])
_MAIL_SVC = _service("mail", "src/Verbara.Platform.Mail/Dockerfile.mail")


class CheckEnvExampleStartupSettingsTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = self._tmp.name
        self._write("src/Verbara.Platform.Api/Program.cs", _PROGRAM)
        self._write("src/Verbara.Platform.Api/appsettings.json", _APPSETTINGS)

    def _write(self, rel, text):
        path = os.path.join(self.root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text)

    def _stack(self, services, example=_COMPLETE, rel="docker/docker-compose.production.yml"):
        self._write(rel, "services:\n" + services)
        if example is not None:
            self._write("docker/.env.production.example", example)

    def _run(self, root=None):
        return subprocess.run([sys.executable, _SCRIPT, "--root", root or self.root],
                              capture_output=True, text=True, check=False)

    def _drop(self, key):
        return "".join(line + "\n" for line in _COMPLETE.splitlines() if not line.startswith(key + "="))

    def test_passes_when_the_example_lists_every_setting(self):
        self._stack(_API + _MAIL_SVC)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        for key in ("Services__ServiceKey", "CORS_ORIGINS", "Asterisk__Ami__Username", "Asterisk__Ami__Password"):
            self.assertIn(key, r.stdout)
        self.assertIn("docker/docker-compose.production.yml:platform-api", r.stdout)
        self.assertIn("docker/docker-compose.production.yml:mail", r.stdout)

    def test_fails_for_each_api_setting_the_example_lacks(self):
        for key in ("Services__ServiceKey", "CORS_ORIGINS", "Asterisk__Ami__Username", "Asterisk__Ami__Password"):
            with self.subTest(key=key):
                self._stack(_API + _MAIL_SVC, example=self._drop(key))
                r = self._run()
                self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
                self.assertIn(f"platform-api refuses to start without {key}", r.stdout)
                self.assertIn("docker/.env.production.example has no", r.stdout)
                self.assertIn("src/Verbara.Platform.Api/Program.cs:", r.stdout)

    def test_a_secret_the_mail_service_reads_at_first_use_is_not_required_at_start(self):
        # The reply-token signer is built the first time a reply is checked (#690), so only the reply-mailbox switch
        # makes its secret a start-up setting; building it from a field read at first use is no start-up refusal.
        self._write("src/Verbara.Platform.Mail/ServiceCollectionExtensions.cs",
                    'services.Configure<ImapPollerOptions>(configuration.GetSection("Imap"));\n'
                    "services.AddSingleton<ICsatReplyTokenSigner, DeferredCsatReplyTokenSigner>();\n")
        self._write("src/Verbara.Platform.Mail/Services/DeferredCsatReplyTokenSigner.cs",
                    "return new HmacCsatReplyTokenSigner(options.TokenSigningSecret, options.TokenTtl);\n")
        self._stack(_API + _MAIL_SVC)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertNotIn("Imap__TokenSigningSecret", r.stdout)

    def test_a_key_in_the_service_environment_counts(self):
        api = _service("platform-api", "Dockerfile",
                       environment=["ASPNETCORE_ENVIRONMENT=Production", "Services__ServiceKey=${SERVICE_KEY:?set}"])
        self._stack(api + _MAIL_SVC, example=self._drop("Services__ServiceKey"))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_keys_are_matched_without_case(self):
        self._stack(_API + _MAIL_SVC, example=_COMPLETE.replace("Services__ServiceKey", "SERVICES__SERVICEKEY"))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_the_ami_pair_is_not_required_when_the_hostname_is_cleared(self):
        example = self._drop("Asterisk__Ami__Username").replace("Asterisk__Ami__Password=\n", "")
        self._stack(_API + _MAIL_SVC, example=example + "Asterisk__Ami__Hostname=\n")
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_the_ami_pair_is_required_when_appsettings_sets_the_hostname(self):
        self._stack(_API + _MAIL_SVC, example=self._drop("Asterisk__Ami__Username"))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("while Asterisk__Ami__Hostname is set", r.stdout)

    def test_the_ami_pair_is_not_required_when_no_hostname_is_configured(self):
        self._write("src/Verbara.Platform.Api/appsettings.json", '{ "Asterisk": { "Ami": { "Port": 5038 } } }\n')
        self._stack(_API + _MAIL_SVC, example=self._drop("Asterisk__Ami__Username"))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_the_hostname_itself_and_unrelated_keys_are_not_required(self):
        self._stack(_API + _MAIL_SVC)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertNotIn("Unrelated__Key", r.stdout)
        self.assertNotIn("Asterisk__Ami__Hostname,", r.stdout)

    def test_the_production_check_is_skipped_outside_production(self):
        api = _service("platform-api", "Dockerfile", environment=["ASPNETCORE_ENVIRONMENT=Development"])
        self._stack(api + _MAIL_SVC, example=self._drop("Services__ServiceKey"))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_an_unset_environment_is_production(self):
        api = _service("platform-api", "Dockerfile")
        self._stack(api + _MAIL_SVC, example=self._drop("Services__ServiceKey"))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_an_api_image_is_recognised(self):
        api = ("  platform-api:\n    image: ghcr.io/verbara/platform/api:v2.40.0\n"
               "    env_file: .env.production\n")
        self._stack(api, example=self._drop("Services__ServiceKey"))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("platform-api refuses to start without Services__ServiceKey", r.stdout)

    def test_a_mail_image_is_recognised(self):
        mail = "  mail:\n    image: ghcr.io/verbara/platform/mail:v2.40.0\n    env_file: [.env.production]\n"
        self._with_validator()
        self._stack(_API + mail)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("mail reads Imap__Enabled", r.stdout)

    def test_a_stack_without_env_file_is_left_to_its_defaults(self):
        api = _service("platform-api", "Dockerfile", env_file=None, environment=["ASPNETCORE_ENVIRONMENT=Production"])
        self._stack(api + _MAIL_SVC, example=_COMPLETE)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertNotIn("production.yml:platform-api", r.stdout)

    def test_an_overlay_service_is_left_to_its_base(self):
        overlay = "  platform-api:\n    env_file: .env.production\n"
        self._stack(overlay + _MAIL_SVC, example=self._drop("Services__ServiceKey"))
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_when_neither_the_env_file_nor_its_example_is_in_the_tree(self):
        self._stack(_API + _MAIL_SVC, example=None)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("docker/.env.production, which is not in the tree", r.stdout)

    def test_the_operators_own_file_wins_over_the_example(self):
        self._stack(_API + _MAIL_SVC, example=self._drop("Services__ServiceKey"))
        self._write("docker/.env.production", _COMPLETE)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_a_reset_build_in_an_overlay_falls_back_to_the_image(self):
        api = ("  platform-api:\n    build: !reset []\n    image: ghcr.io/verbara/platform/api:v2.40.0\n"
               "    env_file: .env.production\n")
        self._stack(api, example=self._drop("Services__ServiceKey"))
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_exits_2_when_the_production_block_is_gone(self):
        self._write("src/Verbara.Platform.Api/Program.cs", "var x = 1;\n")
        self._stack(_API + _MAIL_SVC)
        r = self._run()
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("IsProduction", r.stderr)

    def test_exits_2_when_no_service_reads_an_env_file(self):
        api = _service("platform-api", "Dockerfile", env_file=None)
        self._stack(api)
        r = self._run()
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)

    def _with_validator(self):
        self._write("src/Verbara.Platform.Mail/Services/CsatReplyMailboxSettingsValidator.cs", _SWITCH_VALIDATOR)

    def test_passes_when_the_mailbox_settings_are_listed_commented_next_to_the_switch(self):
        self._with_validator()
        self._stack(_API + _MAIL_SVC, example=_COMPLETE + _MAILBOX_LISTED)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("CsatCapture__ApiBaseAddress (while Imap__Enabled is true)", r.stdout)
        self.assertIn("Imap__Mailboxes__0__Host (while Imap__Enabled is true)", r.stdout)

    def test_fails_for_each_mailbox_setting_the_example_lacks(self):
        self._with_validator()
        for key in ("CsatCapture__ApiBaseAddress", "Imap__Mailboxes__0__Host"):
            with self.subTest(key=key):
                listed = "".join(line + "\n" for line in _MAILBOX_LISTED.splitlines() if key + "=" not in line)
                self._stack(_API + _MAIL_SVC, example=_COMPLETE + listed)
                r = self._run()
                self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
                self.assertIn(f"mail needs {key} while Imap__Enabled is true", r.stdout)
                self.assertIn("src/Verbara.Platform.Mail/Services/CsatReplyMailboxSettingsValidator.cs:", r.stdout)
                self.assertIn(f"add # {key}= next to Imap__Enabled", r.stdout)

    def test_fails_when_the_example_does_not_mention_the_switch(self):
        self._with_validator()
        listed = "".join(line + "\n" for line in _MAILBOX_LISTED.splitlines() if "Imap__Enabled" not in line)
        self._stack(_API + _MAIL_SVC, example=_COMPLETE + listed)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("has no Imap__Enabled line", r.stdout)

    def test_fails_when_the_switch_is_on_and_a_setting_is_only_commented(self):
        self._with_validator()
        example = _COMPLETE + _MAILBOX_LISTED.replace("# Imap__Enabled=true", "Imap__Enabled=true")
        self._stack(_API + _MAIL_SVC, example=example)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("mail needs CsatCapture__ApiBaseAddress while Imap__Enabled is true", r.stdout)
        self.assertIn("sets Imap__Enabled=true", r.stdout)

    def test_passes_when_the_switch_is_on_and_every_setting_is_set(self):
        self._with_validator()
        example = _COMPLETE + _MAILBOX_ON
        self._stack(_API + _MAIL_SVC, example=example)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_fails_when_the_switch_is_on_and_a_setting_is_empty(self):
        # The service refuses an empty value as it refuses a missing one, so `Key=` with the switch on fails too.
        self._with_validator()
        example = _COMPLETE + _MAILBOX_ON.replace("__Host=imap.example.test", "__Host=")
        self._stack(_API + _MAIL_SVC, example=example)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("mail needs Imap__Mailboxes__0__Host while Imap__Enabled is true", r.stdout)
        self.assertIn("give Imap__Mailboxes__0__Host a value", r.stdout)
        self.assertNotIn("CsatCapture__ApiBaseAddress while", r.stdout)

    def test_fails_when_the_switch_is_on_and_a_setting_interpolates_to_empty(self):
        self._with_validator()
        mail = _service("mail", "src/Verbara.Platform.Mail/Dockerfile.mail",
                        environment=["Imap__Enabled=true", "CsatCapture__ApiBaseAddress=http://platform-api:5000",
                                     "Imap__Mailboxes__0__Host=${IMAP_HOST:-}"])
        self._stack(_API + mail)
        r = self._run()
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("give Imap__Mailboxes__0__Host a value", r.stdout)

    def test_an_empty_setting_is_fine_while_the_switch_is_off(self):
        self._with_validator()
        mail = _service("mail", "src/Verbara.Platform.Mail/Dockerfile.mail",
                        environment=["Imap__Enabled=${IMAP_ENABLED:-false}", "CsatCapture__ApiBaseAddress=",
                                     "Imap__Mailboxes__0__Host=${IMAP_HOST:-}"])
        self._stack(_API + mail)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_a_switch_set_to_false_needs_the_settings_listed_only(self):
        self._with_validator()
        example = _COMPLETE + _MAILBOX_LISTED.replace("# Imap__Enabled=true", "Imap__Enabled=false")
        self._stack(_API + _MAIL_SVC, example=example)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_a_setting_with_a_fallback_is_not_required(self):
        self._with_validator()
        self._stack(_API + _MAIL_SVC, example=_COMPLETE + _MAILBOX_LISTED)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertNotIn("CsatCapture__ServiceKey", r.stdout)

    def test_the_service_environment_counts_for_the_switch_and_its_settings(self):
        self._with_validator()
        mail = _service("mail", "src/Verbara.Platform.Mail/Dockerfile.mail",
                        environment=["Imap__Enabled=true", "CsatCapture__ApiBaseAddress=http://platform-api:5000",
                                     "Imap__Mailboxes__0__Host=imap.example.test"])
        self._stack(_API + mail)
        r = self._run()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_exits_2_when_a_file_requires_settings_without_naming_its_switch(self):
        self._write("src/Verbara.Platform.Mail/Services/CsatReplyMailboxSettingsValidator.cs",
                    _SWITCH_VALIDATOR.replace('internal const string SwitchKey = "Imap:Enabled";', ""))
        self._stack(_API + _MAIL_SVC, example=_COMPLETE + _MAILBOX_LISTED)
        r = self._run()
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("SwitchKey", r.stderr)

    def test_the_repository_passes(self):
        r = self._run(_REPO)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker/docker-compose.production.yml:platform-api", r.stdout)
        self.assertIn("docker/docker-compose.production.yml:mail", r.stdout)
        # The mail service's reply-mailbox check is read from the code, so the repository run covers it.
        self.assertIn("CsatCapture__ApiBaseAddress (while Imap__Enabled is true)", r.stdout)
        # The reply-token secret is required only with the reply mailbox on (#690).
        self.assertIn("Imap__TokenSigningSecret (while Imap__Enabled is true)", r.stdout)
        self.assertNotIn("Imap__TokenSigningSecret,", r.stdout)


if __name__ == "__main__":
    unittest.main()
