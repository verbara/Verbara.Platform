"""Unit tests for scripts/ci/check-tag-on-main.sh.

release.yml's `tag-on-main` job runs the script before any image is built, pushed or signed,
and every build/push/sign/release job `needs:` it. Each test builds a throwaway git repo with
a `main` branch and a side branch, tags commits on either, and asserts the exit code. Stdlib
unittest + git only, so it runs in the `coverage-scripts` CI job
(`python3 -m unittest discover scripts/tests`).

  * tag on main's tip, or on an older commit of main (lightweight or annotated)   => 0
  * tag on a commit that is not on main (side branch, or main later reset past it) => 1
  * RETROACTIVE-TAG marker, even off main (the release builds nothing)             => 0
  * missing tag, missing main ref, no arguments                                    => 2
"""
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.abspath(os.path.join(_HERE, os.pardir, "ci", "check-tag-on-main.sh"))


def _git(root, *args):
    return subprocess.run(
        ["git", *args], cwd=root, capture_output=True, text=True, check=True,
    )


def _commit(root, name):
    Path(root, name).write_text(name + "\n", encoding="utf-8")
    _git(root, "add", name)
    _git(root, "commit", "-q", "-m", name)
    return _git(root, "rev-parse", "HEAD").stdout.strip()


class CheckTagOnMainTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = self._tmp.name
        _git(self.root, "init", "-q", "-b", "main")
        _git(self.root, "config", "user.email", "ci@verbara.test")
        _git(self.root, "config", "user.name", "CI Test")
        _git(self.root, "config", "tag.gpgSign", "false")
        _git(self.root, "config", "commit.gpgSign", "false")
        self.first = _commit(self.root, "a")
        self.tip = _commit(self.root, "b")
        _git(self.root, "checkout", "-q", "-b", "side", self.first)
        self.side = _commit(self.root, "c")
        _git(self.root, "checkout", "-q", "main")

    def tearDown(self):
        self._tmp.cleanup()

    def _run(self, *args):
        return subprocess.run(
            ["bash", _SCRIPT, *args], cwd=self.root, capture_output=True, text=True,
        )

    def _annotation(self, tag):
        return _git(
            self.root, "for-each-ref", "--format=%(contents)", f"refs/tags/{tag}",
        ).stdout

    def assertExit(self, proc, code):
        self.assertEqual(
            proc.returncode, code,
            msg=f"stdout: {proc.stdout}\nstderr: {proc.stderr}",
        )

    def test_tagOnMainTip_ShouldPass_WhenLightweight(self):
        _git(self.root, "tag", "v1.0.0", self.tip)
        self.assertExit(self._run("v1.0.0", "refs/heads/main"), 0)

    def test_tagOnOlderMainCommit_ShouldPass_WhenAnnotated(self):
        _git(self.root, "tag", "-a", "v1.0.0", self.first, "-m", "release 1.0.0")
        self.assertExit(self._run("v1.0.0", "refs/heads/main"), 0)

    def test_tagOffMain_ShouldFail_WhenCommitIsOnlyOnASideBranch(self):
        _git(self.root, "tag", "-a", "v1.0.1", self.side, "-m", "release 1.0.1")
        proc = self._run("v1.0.1", "refs/heads/main")
        self.assertExit(proc, 1)
        self.assertIn("not on refs/heads/main", proc.stderr)

    def test_tagOffMain_ShouldFail_WhenLightweight(self):
        _git(self.root, "tag", "v1.0.1", self.side)
        self.assertExit(self._run("v1.0.1", "refs/heads/main"), 1)

    def test_tagOnTip_ShouldFail_WhenMainNoLongerContainsIt(self):
        _git(self.root, "tag", "v1.0.0", self.tip)
        _git(self.root, "update-ref", "refs/heads/main", self.first)
        self.assertExit(self._run("v1.0.0", "refs/heads/main"), 1)

    def test_defaultMainRef_ShouldBeOriginMain_WhenNoSecondArgument(self):
        _git(self.root, "tag", "v1.0.0", self.tip)
        # No refs/remotes/origin/main yet: the default ref is missing, fail closed.
        self.assertExit(self._run("v1.0.0"), 2)
        _git(self.root, "update-ref", "refs/remotes/origin/main", self.tip)
        self.assertExit(self._run("v1.0.0"), 0)
        _git(self.root, "tag", "v1.0.1", self.side)
        self.assertExit(self._run("v1.0.1"), 1)

    def test_retroactiveTag_ShouldPass_WhenOffMain(self):
        _git(self.root, "tag", "-a", "v0.9.9", self.side,
             "-m", "RETROACTIVE-TAG: image pushed by hand; digests recorded")
        proc = self._run("v0.9.9", "refs/heads/main")
        self.assertExit(proc, 0)
        self.assertIn("Retroactive tag", proc.stdout)

    def test_marker_ShouldExempt_ExactlyWhenTheReleaseJobSkips(self):
        # The script reads the marker the way release.yml's own retroactive-tag step does
        # (`git for-each-ref --format='%(contents)'`). For a lightweight tag that is the
        # commit message, so the release job skips every build step there too; the two
        # readings must agree, or a skip-or-build tag could slip past this check.
        _git(self.root, "checkout", "-q", "side")
        marked = _commit(self.root, "RETROACTIVE-TAG")
        _git(self.root, "checkout", "-q", "main")
        _git(self.root, "tag", "v1.0.2", marked)
        self.assertIn("RETROACTIVE-TAG", self._annotation("v1.0.2"))
        self.assertExit(self._run("v1.0.2", "refs/heads/main"), 0)
        # An annotated tag on that same commit, without the marker in its own message,
        # is a normal release: the release job builds, so the check applies.
        _git(self.root, "tag", "-a", "v1.0.3", marked, "-m", "release 1.0.3")
        self.assertNotIn("RETROACTIVE-TAG", self._annotation("v1.0.3"))
        self.assertExit(self._run("v1.0.3", "refs/heads/main"), 1)

    def test_missingTag_ShouldFailClosed(self):
        self.assertExit(self._run("v9.9.9", "refs/heads/main"), 2)

    def test_missingMainRef_ShouldFailClosed(self):
        _git(self.root, "tag", "v1.0.0", self.tip)
        self.assertExit(self._run("v1.0.0", "refs/heads/nope"), 2)

    def test_noArguments_ShouldFailClosed(self):
        self.assertExit(self._run(), 2)


if __name__ == "__main__":
    unittest.main()
