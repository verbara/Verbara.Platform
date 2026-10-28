"""Unit tests for check-endpoint-invariants.py (gates #6, #7, #9, #10, #11, #12, #13).

Runs the script as a subprocess against a synthetic src/ tree in a tmp dir and
asserts exit codes + failure grammar. Stdlib unittest only — NO pip deps, so this
runs in the same `coverage-scripts` CI job as the coverage-gate guards.
"""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-endpoint-invariants.py")

# Keep in sync with LOC_BUDGETS in the script under test.
_PROGRAM = "src/Verbara.Platform.Api/Program.cs"
_BUDGET = 1932

# --- Gate #10 fixtures: the banned Npgsql legacy-timestamp switch -------------
_API_CSPROJ = "src/Verbara.Platform.Api/Verbara.Platform.Api.csproj"
_LEGACY_SWITCH = "Npgsql.EnableLegacyTimestampBehavior"

# Both fixtures put the switch on LINE 3, so the path:line assertions are exact.
_CSPROJ_WITH_SWITCH = (
    "<Project>\n"
    "  <ItemGroup>\n"
    '    <RuntimeHostConfigurationOption Include="' + _LEGACY_SWITCH + '" Value="true" />\n'
    "  </ItemGroup>\n"
    "</Project>\n"
)
_RUNTIMECONFIG_WITH_SWITCH = (
    "{\n"
    '  "configProperties": {\n'
    '    "' + _LEGACY_SWITCH + '": true\n'
    "  }\n"
    "}\n"
)

# --- Gate #11 fixtures: SpecifyKind relabel in Postgres store code ------------
_PG_STORE = "src/Verbara.Platform.Storage.Postgres/Stores/PostgresBotConfigStore.cs"
_SPECIFY_KIND_LINE = "var at = DateTime.SpecifyKind(raw, DateTimeKind.Utc);"


# --- Gate #12 fixtures: Asterisk outbound registration stays loadable ----------
_MODULES_CONF = "docker/asterisk-config/modules.conf"
_SORCERY_CONF = "docker/asterisk-config/sorcery.conf"
_MODULES_OK = (
    "[modules]\n"
    "autoload = yes\n"
    "noload = chan_sip.so\n"
)
_MODULES_NOLOAD = (
    "[modules]\n"
    "autoload = yes\n"
    "noload = res_pjsip_outbound_registration.so\n"
)
_SORCERY_OK = (
    "[res_pjsip]\n"
    "endpoint = realtime,ps_endpoints\n"
    "\n"
    "[res_pjsip_outbound_registration]\n"
    "registration = realtime,ps_registrations\n"
)
# The shape that shipped before #385: the mapping on LINE 3, under [res_pjsip].
_SORCERY_UNDER_RES_PJSIP = (
    "[res_pjsip]\n"
    "endpoint = realtime,ps_endpoints\n"
    "registration = realtime,ps_registrations\n"
)

# --- Gate #13 fixtures: the licence trust anchor stays the embedded key --------
_API_HOST = "src/Verbara.Platform.Api/Licensing/TrustAnchorRegistration.cs"
_TRUST_ANCHOR_REGISTRATIONS = (
    "builder.Services.AddSingleton<byte[]>(File.ReadAllBytes(keyPath));",
    "services.TryAddSingleton<byte[]>(key);",
    "services.AddSingleton<byte []>(key);",
    "services.AddKeyedSingleton<byte[]>(\"anchor\", key);",
    "services.Replace(ServiceDescriptor.Singleton<byte[]>(key));",
    "services.RemoveAll<byte[]>();",
    "services.AddSingleton(typeof(byte[]), key);",
    "services.Add(new ServiceDescriptor(typeof(byte[]), key));",
    "builder.Services.AddSingleton(File.ReadAllBytes(keyPath));",
    "builder.Services.AddSingleton(Convert.FromBase64String(raw));",
    "builder.Services.AddSingleton(licensePublicKey);",
    "services.TryAddSingleton(customTrustAnchor);",
    # Qualified type names, untyped scoped/transient, untyped descriptors, block lambdas, other decoders.
    "services.AddSingleton<System.Byte[]>(File.ReadAllBytes(p));",
    "services.AddSingleton<global::System.Byte[]>(sp => key);",
    "services.AddSingleton(typeof(global::System.Byte[]), key);",
    "services.AddTransient(_ => File.ReadAllBytes(p));",
    "services.AddScoped(_ => File.ReadAllBytes(p));",
    "services.Replace(ServiceDescriptor.Singleton(sp => File.ReadAllBytes(p)));",
    "services.Add(ServiceDescriptor.Transient(_ => Convert.FromBase64String(raw)));",
    "services.AddSingleton(sp => { var c = sp.GetRequiredService<IConfiguration>(); "
    "return File.ReadAllBytes(c[\"k\"]!); });",
    "services.AddSingleton(Encoding.ASCII.GetBytes(cfg[\"k\"]!));",
)
# byte[] that is not a DI service: return types, locals, serializers, other singletons.
_TRUST_ANCHOR_NEGATIVES = (
    "public ValueTask<byte[]> RenderAsync(ReportData data, CancellationToken ct) => default;",
    "var bytes = File.ReadAllBytes(path);",
    "services.AddSingleton<IReportRenderer, CsvReportRenderer>();",
    "services.AddSingleton(TimeProvider.System);",
    "var fp = LicenseTrustAnchor.OfficialPublicKeyFingerprintSha256;",
    "var t = typeof(byte[]);",
)


def _store_body(statement):
    """A .cs file whose `statement` sits on LINE 3 (for path:line assertions)."""
    return "class S {\n  void M() {\n    " + statement + "\n  }\n}\n"


def _run(root):
    return subprocess.run(
        [sys.executable, _SCRIPT],
        cwd=root, capture_output=True, text=True,
    )


def _write(root, rel, content):
    path = Path(root) / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def _tree(root, program_lines=10, endpoint_body="catch (Exception ex) { Log(ex); }"):
    """A Program.cs under budget + one Endpoints file with the given catch body."""
    _write(root, _PROGRAM, "\n".join(f"// line {i}" for i in range(program_lines)))
    _write(root, "src/Api/Endpoints/FooEndpoints.cs",
           "class F { void M() { try { Do(); } " + endpoint_body + " } }")


class EndpointInvariantsTest(unittest.TestCase):
    def test_passes_when_no_empty_catch_and_program_under_budget(self):
        with tempfile.TemporaryDirectory() as root:
            _tree(root)
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Gate #6", result.stdout)
            self.assertIn("Gate #9", result.stdout)

    def test_fails_when_empty_catch_in_endpoints(self):
        with tempfile.TemporaryDirectory() as root:
            _tree(root, endpoint_body="catch { }")
            result = _run(root)
            self.assertEqual(result.returncode, 1)
            self.assertIn("empty catch", result.stdout)

    def test_fails_when_typed_empty_catch_in_endpoints(self):
        with tempfile.TemporaryDirectory() as root:
            _tree(root, endpoint_body="catch (Exception) {}")
            result = _run(root)
            self.assertEqual(result.returncode, 1)

    def test_fails_when_multiline_empty_catch_in_endpoints(self):
        with tempfile.TemporaryDirectory() as root:
            _write(root, _PROGRAM, "// x")
            _write(root, "src/Api/Endpoints/BarEndpoints.cs",
                   "class B { void M() { try { Do(); }\n"
                   "            catch\n            {\n            } } }")
            result = _run(root)
            self.assertEqual(result.returncode, 1)
            self.assertIn("empty catch", result.stdout)

    def test_ignores_empty_catch_in_line_comment(self):
        with tempfile.TemporaryDirectory() as root:
            _write(root, _PROGRAM, "// x")
            _write(root, "src/Api/Endpoints/DocEndpoints.cs",
                   "class D { // a doc comment mentioning catch {} must not trip\n"
                   "  void M() { try { Do(); } catch (Exception ex) { Log(ex); } } }")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_ignores_empty_catch_in_block_comment(self):
        with tempfile.TemporaryDirectory() as root:
            _write(root, _PROGRAM, "// x")
            _write(root, "src/Api/Endpoints/DocEndpoints.cs",
                   "class D { /* prose:\n   catch {}\n   still prose */\n"
                   "  void M() { try { Do(); } catch (Exception ex) { Log(ex); } } }")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_ignores_comment_only_catch(self):
        # A catch whose body is only a comment is NOT truly-empty — the ADR gated
        # empty `catch {}`, not the softer comment-only swallow. Must pass.
        with tempfile.TemporaryDirectory() as root:
            _tree(root, endpoint_body="catch { /* best-effort: reconciler backstops */ }")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_ignores_empty_catch_outside_endpoints(self):
        with tempfile.TemporaryDirectory() as root:
            _write(root, _PROGRAM, "// x")
            _write(root, "src/Api/Services/Thing.cs",
                   "class T { void M() { try { Do(); } catch { } } }")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_fails_when_program_over_budget(self):
        with tempfile.TemporaryDirectory() as root:
            _write(root, _PROGRAM, "\n".join("x" for _ in range(_BUDGET + 5)))
            _write(root, "src/Api/Endpoints/FooEndpoints.cs", "class F {}")
            result = _run(root)
            self.assertEqual(result.returncode, 1)
            self.assertIn("LOC budget", result.stdout)

    def test_fails_when_program_missing(self):
        with tempfile.TemporaryDirectory() as root:
            _write(root, "src/Api/Endpoints/FooEndpoints.cs", "class F {}")
            result = _run(root)
            self.assertEqual(result.returncode, 1)
            self.assertIn("MISSING", result.stdout)

    # --- Gate #7: no Guid.NewGuid in a credential mint -------------------------

    def _guid_tree(self, root, api_body,
                   rel="src/Verbara.Platform.Api/Endpoints/KeyEndpoints.cs"):
        """A valid Program.cs + one file under (or, via `rel`, outside) the Api scope."""
        _write(root, _PROGRAM, "// x")
        _write(root, rel, api_body)

    def test_passes_when_credential_minted_via_factory(self):
        with tempfile.TemporaryDirectory() as root:
            self._guid_tree(
                root, 'class K { void M() { var rawKey = SecretTokenGenerator.Mint("mgmt_"); } }')
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Gate #7", result.stdout)

    def test_fails_when_guid_interpolated_into_credential_name(self):
        with tempfile.TemporaryDirectory() as root:
            self._guid_tree(
                root, 'class K { void M() { var rawKey = $"mgmt_{Guid.NewGuid():N}"; } }')
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("gate #7", result.stdout)

    def test_fails_when_guid_mints_token_secret_credential_or_password(self):
        for name in ("authToken", "apiSecret", "clientCredential", "userPassword"):
            with tempfile.TemporaryDirectory() as root:
                self._guid_tree(
                    root, "class K { void M() { var " + name + ' = $"x_{Guid.NewGuid()}"; } }')
                result = _run(root)
                self.assertEqual(result.returncode, 1, f"{name}: {result.stdout}")

    def test_ignores_guid_tostring_id_uses_even_when_token_named(self):
        # The interpolation requirement is the discriminator: `.ToString()`-shaped
        # ids never follow `= $"`, so a token-NAMED record id (TokenId, not the
        # secret) and a plain messageId are legitimate and must NOT be flagged.
        with tempfile.TemporaryDirectory() as root:
            self._guid_tree(
                root,
                'class K { void M() {\n'
                '  var messageId = Guid.NewGuid().ToString("N");\n'
                '  TokenId = Guid.NewGuid().ToString("N");\n'
                '} }')
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_ignores_interpolated_guid_in_non_credential_name(self):
        with tempfile.TemporaryDirectory() as root:
            self._guid_tree(
                root, 'class K { void M() { var visitorAddress = $"webchat-{Guid.NewGuid():N}"; } }')
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_ignores_guid_credential_mint_inside_comment(self):
        with tempfile.TemporaryDirectory() as root:
            self._guid_tree(
                root,
                'class K { // legacy: var rawKey = $"mgmt_{Guid.NewGuid()}" (now via factory)\n'
                '  void M() { var rawKey = SecretTokenGenerator.Mint("mgmt_"); } }')
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_ignores_guid_credential_mint_outside_api_scope(self):
        # Media object-storage keys use Guid for uniqueness (not a secret) and live
        # outside the Api composition — out of the gate's scope, must NOT be flagged.
        with tempfile.TemporaryDirectory() as root:
            self._guid_tree(
                root, 'class S { void M() { var key = $"{t}/{Guid.NewGuid():N}_{f}"; } }',
                rel="src/Verbara.Platform.Media/S3MediaStorage.cs")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    # --- Gate #10: the Npgsql legacy-timestamp switch stays gone ---------------

    def _legacy_tree(self, root, rel=None, content=None):
        """A tree that passes every gate, plus (optionally) one build file whose
        content carries the switch. The baseline csproj is clean, so a test that
        overwrites it with `rel` introduces exactly one violation."""
        _tree(root)
        _write(root, _API_CSPROJ, "<Project>\n  <PropertyGroup />\n</Project>\n")
        if rel is not None:
            _write(root, rel, content)

    def test_passes_when_no_build_file_mentions_legacy_timestamp_switch(self):
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(
                root, "src/Verbara.Platform.Api/runtimeconfig.template.json",
                '{\n  "configProperties": {\n    "System.GC.Server": true\n  }\n}\n')
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Gate #10", result.stdout)

    def test_fails_when_csproj_declares_legacy_timestamp_switch(self):
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(root, _API_CSPROJ, _CSPROJ_WITH_SWITCH)
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("gate #10", result.stdout)

    def test_fails_when_props_or_targets_declares_legacy_timestamp_switch(self):
        for rel in ("Directory.Build.props", "src/Verbara.Platform.Api/Api.targets"):
            with tempfile.TemporaryDirectory() as root:
                self._legacy_tree(root, rel, _CSPROJ_WITH_SWITCH)
                result = _run(root)
                self.assertEqual(result.returncode, 1, f"{rel}: {result.stdout}")
                self.assertIn("gate #10", result.stdout)

    def test_fails_when_runtimeconfig_template_declares_legacy_timestamp_switch(self):
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(root, "src/Verbara.Platform.Api/runtimeconfig.template.json",
                              _RUNTIMECONFIG_WITH_SWITCH)
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("gate #10", result.stdout)

    def test_fails_when_assembly_runtimeconfig_json_declares_legacy_timestamp_switch(self):
        # `<Assembly>.runtimeconfig.json` in SOURCE (not under bin/obj) is covered by
        # the `**/*.runtimeconfig.json` glob, a different pattern from the bare
        # `**/runtimeconfig.json` — a rename must not dodge the gate.
        rel = "src/Verbara.Platform.Api/Verbara.Platform.Api.runtimeconfig.json"
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(root, rel, _RUNTIMECONFIG_WITH_SWITCH)
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("gate #10", result.stdout)
            # reported once, not once per matching glob
            self.assertEqual(result.stdout.count(f"{rel}:3"), 1, result.stdout)

    def test_fails_when_bare_runtimeconfig_json_declares_legacy_timestamp_switch(self):
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(root, "src/Verbara.Platform.Api/runtimeconfig.json",
                              _RUNTIMECONFIG_WITH_SWITCH)
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("gate #10", result.stdout)

    def test_ignores_legacy_timestamp_switch_under_bin(self):
        # DELIBERATE: a built runtimeconfig is derived output, so clean source
        # already guarantees a clean artefact — while a STALE local build would
        # fire the gate red against already-correct source. Must stay passing.
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(
                root,
                "src/Verbara.Platform.Api/bin/Release/net10.0/"
                "Verbara.Platform.Api.runtimeconfig.json",
                _RUNTIMECONFIG_WITH_SWITCH)
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertIn("Gate #10", result.stdout)

    def test_ignores_legacy_timestamp_switch_under_obj(self):
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(
                root,
                "src/Verbara.Platform.Api/obj/Debug/net10.0/"
                "Verbara.Platform.Api.runtimeconfig.json",
                _RUNTIMECONFIG_WITH_SWITCH)
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertIn("Gate #10", result.stdout)

    def test_reports_path_and_line_for_legacy_timestamp_switch(self):
        with tempfile.TemporaryDirectory() as root:
            self._legacy_tree(root, _API_CSPROJ, _CSPROJ_WITH_SWITCH)
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn(f"{_API_CSPROJ}:3", result.stdout)

    # --- Gate #11: no SpecifyKind relabel in Postgres store code ---------------

    def _store_tree(self, root, body, rel=_PG_STORE):
        """A tree that passes every gate + one .cs file at `rel` (in the Postgres
        store scope unless the test moves it out)."""
        _tree(root)
        _write(root, rel, body)

    def test_passes_when_postgres_store_has_no_specify_kind(self):
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(
                root, _store_body("var at = reader.GetDateTime(0).ToUniversalTime();"))
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Gate #11", result.stdout)

    def test_fails_when_specify_kind_utc_in_storage_postgres_component(self):
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(
                root, _store_body(_SPECIFY_KIND_LINE),
                rel="src/Verbara.Platform.Storage.Postgres/PostgresBotConfigStore.cs")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("gate #11", result.stdout)

    def test_fails_when_specify_kind_utc_under_stores_component(self):
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(
                root, _store_body(_SPECIFY_KIND_LINE),
                rel="src/Verbara.Platform.Core/Stores/ThingStore.cs")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("gate #11", result.stdout)

    def test_ignores_specify_kind_utc_outside_postgres_scope(self):
        # DELIBERATE scoping: outside reader-sourced store code, SpecifyKind on a
        # value of known Kind is legitimate and must NOT be flagged.
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(root, _store_body(_SPECIFY_KIND_LINE),
                             rel="src/Verbara.Platform.Api/Foo.cs")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertIn("Gate #11", result.stdout)

    def test_fails_when_specify_kind_utc_in_reader_file_outside_store_paths(self):
        # The path markers alone under-scope the gate: Postgres readers are not
        # confined to Storage.Postgres/Stores (Channels.Sms/CsatSmsCorrelator.cs
        # reads an NpgsqlDataReader directly). A file that touches a reader is in
        # scope wherever it lives — otherwise the gate follows the directory
        # layout instead of the hazard.
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(
                root,
                "class S {\n"
                "  void M(NpgsqlDataReader r) {\n"
                "    " + _SPECIFY_KIND_LINE + "\n"
                "  }\n"
                "}\n",
                rel="src/Verbara.Platform.Channels.Sms/CsatSmsCorrelator.cs")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("CsatSmsCorrelator.cs:3", result.stdout)

    def test_fails_when_specify_kind_utc_in_get_datetime_file_outside_store_paths(self):
        # Same scoping rule via the other reader marker — a bare GetDateTime call
        # is enough to source a value whose Kind must not be relabelled.
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(
                root,
                "class S {\n"
                "  void M() { var raw = r.GetDateTime(0);\n"
                "    " + _SPECIFY_KIND_LINE + "\n"
                "  }\n"
                "}\n",
                rel="src/Verbara.Platform.Audit/Reader.cs")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("Reader.cs:3", result.stdout)

    def test_ignores_specify_kind_utc_in_line_comment(self):
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(
                root,
                "class S {\n"
                "  // legacy: DateTime.SpecifyKind(raw, DateTimeKind.Utc) relabelled\n"
                "  void M() { var at = raw.ToUniversalTime(); }\n"
                "}\n")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertIn("Gate #11", result.stdout)

    def test_ignores_specify_kind_utc_in_block_comment(self):
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(
                root,
                "class S { /* prose:\n"
                "   DateTime.SpecifyKind(raw, DateTimeKind.Utc)\n"
                "   still prose */\n"
                "  void M() { var at = raw.ToUniversalTime(); } }\n")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_ignores_specify_kind_with_non_utc_kind(self):
        # Only the ...,DateTimeKind.Utc) relabel shifts a Local reader value; the
        # Local/Unspecified forms are not the gated pattern.
        for kind in ("DateTimeKind.Local", "DateTimeKind.Unspecified"):
            with tempfile.TemporaryDirectory() as root:
                self._store_tree(
                    root,
                    _store_body(f"var at = DateTime.SpecifyKind(raw, {kind});"))
                result = _run(root)
                self.assertEqual(result.returncode, 0, f"{kind}: {result.stdout}")

    def test_ignores_specify_kind_utc_under_bin_or_obj(self):
        for build_dir in ("bin/Release/net10.0", "obj/Debug/net10.0"):
            with tempfile.TemporaryDirectory() as root:
                self._store_tree(
                    root, _store_body(_SPECIFY_KIND_LINE),
                    rel=f"src/Verbara.Platform.Storage.Postgres/{build_dir}/Gen.cs")
                result = _run(root)
                self.assertEqual(result.returncode, 0, f"{build_dir}: {result.stdout}")

    def test_reports_path_and_line_for_specify_kind_utc(self):
        with tempfile.TemporaryDirectory() as root:
            self._store_tree(root, _store_body(_SPECIFY_KIND_LINE))
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn(f"{_PG_STORE}:3", result.stdout)


    # --- Gate #12: Asterisk outbound registration stays loadable --------------

    def _asterisk_tree(self, root, modules=_MODULES_OK, sorcery=_SORCERY_OK):
        """A passing src/ tree plus an Asterisk config dir; None omits a file."""
        _tree(root)
        if modules is not None:
            _write(root, _MODULES_CONF, modules)
        if sorcery is not None:
            _write(root, _SORCERY_CONF, sorcery)

    def test_passes_when_outbound_registration_loads_with_own_section(self):
        with tempfile.TemporaryDirectory() as root:
            self._asterisk_tree(root)
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertIn("Gate #12", result.stdout)

    def test_passes_when_tree_has_no_asterisk_config(self):
        with tempfile.TemporaryDirectory() as root:
            _tree(root)
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_fails_when_modules_conf_noloads_outbound_registration(self):
        for line in ("noload = res_pjsip_outbound_registration.so",
                     "noload => res_pjsip_outbound_registration",
                     "noload=res_pjsip_outbound_registration.so ; why"):
            with tempfile.TemporaryDirectory() as root:
                self._asterisk_tree(root, modules=f"[modules]\n{line}\n")
                result = _run(root)
                self.assertEqual(result.returncode, 1, f"{line}: {result.stdout}")
                self.assertIn(f"{_MODULES_CONF}:2", result.stdout)

    def test_ignores_noload_of_outbound_registration_in_comment(self):
        with tempfile.TemporaryDirectory() as root:
            self._asterisk_tree(
                root, modules="[modules]\n; noload = res_pjsip_outbound_registration.so\n")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout)

    def test_fails_when_registration_mapped_under_res_pjsip(self):
        with tempfile.TemporaryDirectory() as root:
            self._asterisk_tree(root, sorcery=_SORCERY_UNDER_RES_PJSIP)
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn(f"{_SORCERY_CONF}:3", result.stdout)
            self.assertIn("[res_pjsip]", result.stdout)

    def test_fails_when_registration_mapped_in_both_sections(self):
        with tempfile.TemporaryDirectory() as root:
            self._asterisk_tree(
                root, sorcery=_SORCERY_UNDER_RES_PJSIP + "\n[res_pjsip_outbound_registration]\n"
                "registration = realtime,ps_registrations\n")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn(f"{_SORCERY_CONF}:3", result.stdout)

    def test_fails_when_registration_mapping_missing(self):
        with tempfile.TemporaryDirectory() as root:
            self._asterisk_tree(
                root, sorcery="[res_pjsip]\nendpoint = realtime,ps_endpoints\n")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn("no `registration = realtime,ps_registrations`", result.stdout)

    def test_fails_when_registration_mapping_is_commented_out(self):
        with tempfile.TemporaryDirectory() as root:
            self._asterisk_tree(
                root, sorcery="[res_pjsip_outbound_registration]\n"
                "; registration = realtime,ps_registrations\n")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)

    def test_fails_when_registration_maps_another_backend(self):
        with tempfile.TemporaryDirectory() as root:
            self._asterisk_tree(
                root, sorcery="[res_pjsip_outbound_registration]\n"
                "registration = config,pjsip.conf\n")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)

    def test_fails_when_asterisk_config_file_missing(self):
        for missing in ("modules", "sorcery"):
            with tempfile.TemporaryDirectory() as root:
                self._asterisk_tree(root, **{missing: None})
                result = _run(root)
                self.assertEqual(result.returncode, 1, f"{missing}: {result.stdout}")
                self.assertIn(f"{missing}.conf: MISSING", result.stdout)

    # --- Gate #13: the licence trust anchor stays the embedded key ---------------

    def test_passes_when_src_registers_no_byte_array_service(self):
        with tempfile.TemporaryDirectory() as root:
            _tree(root)
            for i, statement in enumerate(_TRUST_ANCHOR_NEGATIVES):
                _write(root, f"src/Verbara.Platform.Api/N{i}.cs", _store_body(statement))
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Gate #13 (licence trust anchor stays the embedded key): OK.", result.stdout)

    def test_fails_when_src_registers_a_trust_anchor(self):
        for statement in _TRUST_ANCHOR_REGISTRATIONS:
            with tempfile.TemporaryDirectory() as root:
                _tree(root)
                _write(root, _API_HOST, _store_body(statement))
                result = _run(root)
                self.assertEqual(result.returncode, 1, f"{statement}: {result.stdout}")
                self.assertIn("gate #13", result.stdout, statement)
                self.assertIn(f"{_API_HOST}:3", result.stdout, statement)

    def test_fails_when_host_reads_a_key_path_before_add_pro_licensing(self):
        # The shape the Api composition root carried before the trust anchor was fixed.
        with tempfile.TemporaryDirectory() as root:
            _tree(root)
            _write(root, _API_HOST,
                   "var publicKeyPath = licenseConfig[\"PublicKeyPath\"];\n"
                   "if (!string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath))\n"
                   "{\n"
                   "    builder.Services.AddSingleton<byte[]>(File.ReadAllBytes(publicKeyPath));\n"
                   "}\n"
                   "builder.Services.AddProLicensing(o => o.LicenseFilePath = licensePath);\n")
            result = _run(root)
            self.assertEqual(result.returncode, 1, result.stdout)
            self.assertIn(f"{_API_HOST}:4", result.stdout)

    def test_ignores_trust_anchor_registration_in_comments(self):
        with tempfile.TemporaryDirectory() as root:
            _tree(root)
            _write(root, _API_HOST,
                   "// a host must never call services.AddSingleton<byte[]>(key)\n"
                   "/* nor services.AddSingleton(File.ReadAllBytes(p)); */\n"
                   "class C {}\n")
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_ignores_trust_anchor_registration_outside_src(self):
        with tempfile.TemporaryDirectory() as root:
            _tree(root)
            _write(root, "tests/Verbara.Platform.Api.Tests/Factory.cs",
                   _store_body("services.AddSingleton<byte[]>([]);"))
            _write(root, "src/Verbara.Platform.Api/obj/Generated.cs",
                   _store_body("services.AddSingleton<byte[]>([]);"))
            result = _run(root)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_repo_src_passes_trust_anchor_gate(self):
        repo = Path(_HERE).parent.parent
        result = subprocess.run(
            [sys.executable, _SCRIPT], cwd=repo, capture_output=True, text=True)
        self.assertIn("Gate #13 (licence trust anchor stays the embedded key): OK.",
                      result.stdout, result.stdout)

    def test_repo_asterisk_config_passes_gate(self):
        # The shipped config itself — the line #385 found and the section move.
        repo = Path(_HERE).parent.parent
        result = subprocess.run(
            [sys.executable, _SCRIPT], cwd=repo, capture_output=True, text=True)
        self.assertIn("Gate #12 (Asterisk outbound registration loadable): OK.",
                      result.stdout, result.stdout)

if __name__ == "__main__":
    unittest.main()
