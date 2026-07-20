"""Unit tests for check-endpoint-invariants.py (ADR-0012 gates #6 + #9).

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
_BUDGET = 1941


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


if __name__ == "__main__":
    unittest.main()
