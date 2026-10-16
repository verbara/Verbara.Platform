"""Unit tests for check-image-provenance.py (the release provenance gate).

Runs the script as a subprocess. The `gate` subcommand is pointed at a stub `docker`
(a tiny shell script written per test) so every inspect outcome — labels, empty output,
non-JSON, a failing inspect, a missing binary — goes through the one production code path.
Stdlib unittest only — NO pip deps, so this runs in the `coverage-scripts` CI job.
"""
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-image-provenance.py")

_SHA = "ca52e26b677b6c27e45a643b2c35285f3551fae3"
_OTHER_SHA = "54162987aff5c1e0d9b8a7f6e5d4c3b2a1908172"
_TAG = "v2.29.0"
_CREATED = "2026-10-16T03:37:49Z"
_IMAGE = "verbara-provenance-gate/api:v2.29.0"

_REV = "org.opencontainers.image.revision"
_VER = "org.opencontainers.image.version"
_CRE = "org.opencontainers.image.created"

_EXIT_UNSTAMPED = 1
_EXIT_UNVERIFIABLE = 3
_EXIT_INVALID_INPUTS = 4


def _labels(**overrides):
    labels = {
        _REV: _SHA,
        _VER: _TAG,
        _CRE: _CREATED,
        "org.opencontainers.image.title": "Verbara Platform API",
    }
    for key, value in overrides.items():
        full = {"revision": _REV, "version": _VER, "created": _CRE}[key]
        if value is _ABSENT:
            labels.pop(full, None)
        else:
            labels[full] = value
    return labels


_ABSENT = object()


class _ScriptCase(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.mkdtemp()

    def tearDown(self):
        for name in os.listdir(self._dir):
            os.unlink(os.path.join(self._dir, name))
        os.rmdir(self._dir)

    def _run(self, *args):
        result = subprocess.run(
            [sys.executable, _SCRIPT, *args],
            capture_output=True,
            text=True,
            check=False,
        )
        return result.returncode, result.stdout + result.stderr


class InputsTests(_ScriptCase):
    def _inputs(self, revision=_SHA, version=_TAG, created=_CREATED, ref_name=_TAG):
        return self._run(
            "inputs",
            "--revision", revision,
            "--version", version,
            "--created", created,
            "--ref-name", ref_name,
        )

    def test_inputs_ShouldPass_WhenAllThreeAreValid(self):
        rc, out = self._inputs()
        self.assertEqual(rc, 0, out)

    def test_inputs_ShouldPass_WhenCreatedHasOffsetAndFraction(self):
        rc, out = self._inputs(created="2026-10-16T05:37:49.123+02:00")
        self.assertEqual(rc, 0, out)

    def test_inputs_ShouldFail_WhenRevisionIsShortSha(self):
        rc, out = self._inputs(revision=_SHA[:7])
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)
        self.assertIn("revision", out)
        self.assertIn(_SHA[:7], out)

    def test_inputs_ShouldFail_WhenRevisionHas41Chars(self):
        rc, out = self._inputs(revision=_SHA + "a")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenRevisionIsUppercaseHex(self):
        rc, out = self._inputs(revision=_SHA.upper())
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenRevisionIsUnknown(self):
        rc, out = self._inputs(revision="unknown")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenRevisionIsEmpty(self):
        rc, out = self._inputs(revision="")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenVersionDiffersFromRefName(self):
        rc, out = self._inputs(version="v2.29.1")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)
        self.assertIn("version", out)
        self.assertIn("v2.29.1", out)
        self.assertIn(_TAG, out)

    def test_inputs_ShouldFail_WhenVersionAndRefNameAreEmpty(self):
        rc, out = self._inputs(version="", ref_name="")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenVersionAndRefNameAreUnknown(self):
        rc, out = self._inputs(version="unknown", ref_name="unknown")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenCreatedIsUnknown(self):
        rc, out = self._inputs(created="unknown")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)
        self.assertIn("created", out)

    def test_inputs_ShouldFail_WhenCreatedIsEmpty(self):
        rc, out = self._inputs(created="")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenCreatedHasNoOffset(self):
        rc, out = self._inputs(created="2026-10-16T03:37:49")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenCreatedIsDateOnly(self):
        rc, out = self._inputs(created="2026-10-16")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldFail_WhenCreatedIsNotACalendarInstant(self):
        rc, out = self._inputs(created="2026-13-40T25:61:00Z")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_inputs_ShouldReportEveryInvalidInput_WhenAllThreeAreInvalid(self):
        rc, out = self._inputs(revision="unknown", version="x", created="unknown")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)
        self.assertEqual(out.count("::error"), 3, out)


class GateTests(_ScriptCase):
    def _stub_docker(self, stdout="", stderr="", rc=0):
        """Write a stub `docker` that prints fixed output and exits with rc."""
        out_file = os.path.join(self._dir, "stdout.txt")
        err_file = os.path.join(self._dir, "stderr.txt")
        args_file = os.path.join(self._dir, "args.txt")
        with open(out_file, "w", encoding="utf-8") as handle:
            handle.write(stdout)
        with open(err_file, "w", encoding="utf-8") as handle:
            handle.write(stderr)
        stub = os.path.join(self._dir, "docker")
        with open(stub, "w", encoding="utf-8") as handle:
            handle.write(
                "#!/bin/sh\n"
                f'printf \'%s\\n\' "$@" > "{args_file}"\n'
                f'cat "{out_file}"\n'
                f'cat "{err_file}" >&2\n'
                f"exit {rc}\n"
            )
        os.chmod(stub, os.stat(stub).st_mode | stat.S_IXUSR)
        return stub

    def _gate(self, docker, revision=_SHA, version=_TAG, created=_CREATED, image=_IMAGE):
        return self._run(
            "gate",
            "--image", image,
            "--revision", revision,
            "--version", version,
            "--created", created,
            "--docker", docker,
        )

    def _gate_labels(self, labels, **expected):
        return self._gate(self._stub_docker(stdout=json.dumps(labels) + "\n"), **expected)

    def _assert_unstamped(self, rc, out, label, found, expected):
        self.assertEqual(rc, _EXIT_UNSTAMPED, out)
        self.assertIn(_IMAGE, out)
        self.assertIn(label, out)
        self.assertIn(f"found={found}", out)
        self.assertIn(f"expected={expected}", out)
        self.assertIn("class=unstamped", out)

    # -- pass ---------------------------------------------------------------

    def test_gate_ShouldPass_WhenAllThreeLabelsEqualExpected(self):
        rc, out = self._gate_labels(_labels())
        self.assertEqual(rc, 0, out)
        self.assertNotIn("::error", out)

    def test_gate_ShouldInspectTheNamedImageLocally(self):
        stub = self._stub_docker(stdout=json.dumps(_labels()))
        rc, out = self._gate(stub)
        self.assertEqual(rc, 0, out)
        with open(os.path.join(self._dir, "args.txt"), encoding="utf-8") as handle:
            args = handle.read().splitlines()
        self.assertEqual(
            args, ["image", "inspect", "--format", "{{json .Config.Labels}}", _IMAGE])

    # -- unstamped: revision ------------------------------------------------

    def test_gate_ShouldFailUnstamped_WhenRevisionIsAbsent(self):
        rc, out = self._gate_labels(_labels(revision=_ABSENT))
        self._assert_unstamped(rc, out, _REV, "(absent)", _SHA)

    def test_gate_ShouldFailUnstamped_WhenRevisionIsEmpty(self):
        rc, out = self._gate_labels(_labels(revision=""))
        self._assert_unstamped(rc, out, _REV, "(empty)", _SHA)

    def test_gate_ShouldFailUnstamped_WhenRevisionIsUnknown(self):
        rc, out = self._gate_labels(_labels(revision="unknown"))
        self._assert_unstamped(rc, out, _REV, "unknown", _SHA)

    def test_gate_ShouldFailUnstamped_WhenRevisionIsAnotherCommit(self):
        rc, out = self._gate_labels(_labels(revision=_OTHER_SHA))
        self._assert_unstamped(rc, out, _REV, _OTHER_SHA, _SHA)

    def test_gate_ShouldFailUnstamped_WhenRevisionIsAPrefixOfExpected(self):
        rc, out = self._gate_labels(_labels(revision=_SHA[:12]))
        self._assert_unstamped(rc, out, _REV, _SHA[:12], _SHA)

    def test_gate_ShouldFailUnstamped_WhenRevisionIsNotAString(self):
        rc, out = self._gate_labels(_labels(revision=12345))
        self._assert_unstamped(rc, out, _REV, "12345", _SHA)

    # -- unstamped: version / created ---------------------------------------

    def test_gate_ShouldFailUnstamped_WhenVersionDiffers(self):
        rc, out = self._gate_labels(_labels(version="v2.28.0"))
        self._assert_unstamped(rc, out, _VER, "v2.28.0", _TAG)

    def test_gate_ShouldFailUnstamped_WhenVersionIsAbsent(self):
        rc, out = self._gate_labels(_labels(version=_ABSENT))
        self._assert_unstamped(rc, out, _VER, "(absent)", _TAG)

    def test_gate_ShouldFailUnstamped_WhenCreatedIsAbsent(self):
        rc, out = self._gate_labels(_labels(created=_ABSENT))
        self._assert_unstamped(rc, out, _CRE, "(absent)", _CREATED)

    def test_gate_ShouldFailUnstamped_WhenCreatedIsUnknown(self):
        rc, out = self._gate_labels(_labels(created="unknown"))
        self._assert_unstamped(rc, out, _CRE, "unknown", _CREATED)

    def test_gate_ShouldFailUnstamped_WhenCreatedIsEmpty(self):
        rc, out = self._gate_labels(_labels(created=""))
        self._assert_unstamped(rc, out, _CRE, "(empty)", _CREATED)

    def test_gate_ShouldFailUnstamped_WhenCreatedDiffers(self):
        rc, out = self._gate_labels(_labels(created="2026-10-16T03:37:50Z"))
        self._assert_unstamped(rc, out, _CRE, "2026-10-16T03:37:50Z", _CREATED)

    def test_gate_ShouldReportEveryFailingLabel_WhenAllThreeAreUnknown(self):
        rc, out = self._gate_labels(
            _labels(revision="unknown", version="unknown", created="unknown"))
        self.assertEqual(rc, _EXIT_UNSTAMPED, out)
        self.assertEqual(out.count("class=unstamped"), 3, out)
        for label in (_REV, _VER, _CRE):
            self.assertIn(label, out)

    def test_gate_ShouldFailUnstamped_WhenImageHasNoLabelsAtAll(self):
        rc, out = self._gate(self._stub_docker(stdout="null\n"))
        self.assertEqual(rc, _EXIT_UNSTAMPED, out)
        self.assertEqual(out.count("class=unstamped"), 3, out)
        self.assertIn("found=(absent)", out)

    def test_gate_ShouldNameTheNewTagRemedy_WhenUnstamped(self):
        rc, out = self._gate_labels(_labels(revision="unknown"))
        self.assertEqual(rc, _EXIT_UNSTAMPED, out)
        self.assertIn("new tag", out)

    def test_gate_ShouldEscapeAnnotationData_WhenLabelHoldsANewline(self):
        rc, out = self._gate_labels(_labels(revision="abc\n::error::forged"))
        self.assertEqual(rc, _EXIT_UNSTAMPED, out)
        self.assertIn("abc%0A::error::forged", out)
        self.assertNotIn("\n::error::forged", out)

    # -- unverifiable --------------------------------------------------------

    def _assert_unverifiable(self, rc, out):
        self.assertEqual(rc, _EXIT_UNVERIFIABLE, out)
        self.assertIn(_IMAGE, out)
        self.assertIn("class=unverifiable", out)
        self.assertIn("re-run", out)

    def test_gate_ShouldFailUnverifiable_WhenInspectFails(self):
        stub = self._stub_docker(
            stderr="Error response from daemon: No such image: x\n", rc=1)
        rc, out = self._gate(stub)
        self._assert_unverifiable(rc, out)
        self.assertIn("No such image", out)

    def test_gate_ShouldFailUnverifiable_WhenInspectFailsAfterPrintingLabels(self):
        stub = self._stub_docker(stdout=json.dumps(_labels()), stderr="boom", rc=1)
        rc, out = self._gate(stub)
        self._assert_unverifiable(rc, out)

    def test_gate_ShouldFailUnverifiable_WhenDockerBinaryIsMissing(self):
        rc, out = self._gate(os.path.join(self._dir, "no-such-docker"))
        self._assert_unverifiable(rc, out)

    def test_gate_ShouldFailUnverifiable_WhenOutputIsEmpty(self):
        rc, out = self._gate(self._stub_docker(stdout=""))
        self._assert_unverifiable(rc, out)

    def test_gate_ShouldFailUnverifiable_WhenOutputIsWhitespace(self):
        rc, out = self._gate(self._stub_docker(stdout="  \n"))
        self._assert_unverifiable(rc, out)

    def test_gate_ShouldFailUnverifiable_WhenOutputIsNotJson(self):
        rc, out = self._gate(self._stub_docker(stdout="<no value>\n"))
        self._assert_unverifiable(rc, out)

    def test_gate_ShouldFailUnverifiable_WhenJsonIsNotAnObject(self):
        rc, out = self._gate(self._stub_docker(stdout='["a", "b"]\n'))
        self._assert_unverifiable(rc, out)

    # -- invalid expected values ---------------------------------------------

    def test_gate_ShouldRefuse_WhenExpectedAndFoundAreBothUnknown(self):
        rc, out = self._gate_labels(
            _labels(revision="unknown", version="unknown", created="unknown"),
            revision="unknown", version="unknown", created="unknown")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_gate_ShouldRefuse_WhenExpectedCreatedIsEmpty(self):
        rc, out = self._gate_labels(_labels(created=""), created="")
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)

    def test_gate_ShouldRefuse_WhenExpectedRevisionIsShort(self):
        rc, out = self._gate_labels(_labels(revision=_SHA[:7]), revision=_SHA[:7])
        self.assertEqual(rc, _EXIT_INVALID_INPUTS, out)


if __name__ == "__main__":
    unittest.main()
