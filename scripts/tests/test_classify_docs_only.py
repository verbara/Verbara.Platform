"""Unit tests for scripts/ci/classify-docs-only.sh (verbara-meta/ADR-0016 §3.4).

Builds a throwaway git repo in a tmp dir, commits a base tree, mutates it, then runs
the bash classifier against the two commits and asserts the `docs_only=` verdict. Stdlib
unittest + git only — NO pip deps, so this runs in the same `coverage-scripts` CI job as the
coverage-gate guards (`python3 -m unittest discover scripts/tests`).

Guards the strict, fail-closed allowlist so a mis-widened rule (e.g. a blanket **/*.md) is
caught before it can mis-skip a code PR:
  * docs/** · openspec/** · CHANGELOG.md · top-level *.md · **/README.md  => docs_only=true
  * ANY other path (src, .github, scripts, a nested non-README .md)       => docs_only=false
  * empty diff / classifier error                                         => docs_only=false
  * a rename touching a code path                                         => docs_only=false
"""
import os
import subprocess
import unittest
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.abspath(os.path.join(_HERE, os.pardir, "ci", "classify-docs-only.sh"))


def _git(root, *args):
    return subprocess.run(
        ["git", *args], cwd=root, capture_output=True, text=True, check=True,
    )


def _write(root, rel, content="x\n"):
    path = Path(root) / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def _init_repo(root):
    _git(root, "init", "-q")
    _git(root, "config", "user.email", "ci@verbara.test")
    _git(root, "config", "user.name", "CI Test")
    # A baseline tree so the "changed" commit has something to diff against.
    _write(root, "README.md", "# root\n")
    _write(root, "src/App/Program.cs", "// base\n")
    _git(root, "add", "-A")
    _git(root, "commit", "-q", "-m", "base")
    return _git(root, "rev-parse", "HEAD").stdout.strip()


class ClassifyDocsOnlyTests(unittest.TestCase):
    def _classify(self, root, base):
        """Run the classifier for the current HEAD vs `base`; return the verdict string."""
        proc = subprocess.run(
            [_SCRIPT, base, "HEAD"],
            cwd=root, capture_output=True, text=True,
        )
        self.assertEqual(proc.returncode, 0, msg=f"stderr: {proc.stderr}")
        return proc.stdout.strip()

    def _run_case(self, mutate, expected):
        import tempfile
        with tempfile.TemporaryDirectory() as root:
            base = _init_repo(root)
            mutate(root)
            _git(root, "add", "-A")
            _git(root, "commit", "-q", "-m", "change")
            self.assertEqual(self._classify(root, base), expected)

    # --- allowlisted => docs_only=true ---

    def test_ShouldBeDocsOnly_WhenOnlyDocsDirChanged(self):
        self._run_case(lambda r: _write(r, "docs/adr/0016.md", "text\n"), "docs_only=true")

    def test_ShouldBeDocsOnly_WhenOnlyOpenspecChanged(self):
        self._run_case(lambda r: _write(r, "openspec/changes/x/proposal.md"), "docs_only=true")

    def test_ShouldBeDocsOnly_WhenOnlyChangelogChanged(self):
        self._run_case(lambda r: _write(r, "CHANGELOG.md", "## x\n"), "docs_only=true")

    def test_ShouldBeDocsOnly_WhenOnlyTopLevelMarkdownChanged(self):
        self._run_case(lambda r: _write(r, "SECURITY.md"), "docs_only=true")

    def test_ShouldBeDocsOnly_WhenNestedReadmeChanged(self):
        self._run_case(lambda r: _write(r, "src/App/README.md"), "docs_only=true")

    def test_ShouldBeDocsOnly_WhenMixOfAllowlistedPathsChanged(self):
        def mutate(r):
            _write(r, "docs/x.md")
            _write(r, "openspec/y.md")
            _write(r, "CHANGELOG.md", "## y\n")
            _write(r, "lib/README.md")
        self._run_case(mutate, "docs_only=true")

    # --- non-allowlisted => docs_only=false (fail-closed) ---

    def test_ShouldNotBeDocsOnly_WhenSourceChanged(self):
        self._run_case(lambda r: _write(r, "src/App/Program.cs", "// changed\n"), "docs_only=false")

    def test_ShouldNotBeDocsOnly_WhenWorkflowChanged(self):
        self._run_case(lambda r: _write(r, ".github/workflows/ci.yml", "on: push\n"), "docs_only=false")

    def test_ShouldNotBeDocsOnly_WhenNestedNonReadmeMarkdownChanged(self):
        # NOT a blanket **/*.md — a nested non-README .md is a code-adjacent doc, fail-closed.
        self._run_case(lambda r: _write(r, "src/App/NOTES.md"), "docs_only=false")

    def test_ShouldNotBeDocsOnly_WhenTopLevelNonMarkdownChanged(self):
        self._run_case(lambda r: _write(r, "Directory.Build.props", "<Project/>\n"), "docs_only=false")

    def test_ShouldNotBeDocsOnly_WhenDocAndCodeBothChanged(self):
        def mutate(r):
            _write(r, "docs/x.md")
            _write(r, "src/App/Program.cs", "// changed\n")
        self._run_case(mutate, "docs_only=false")

    def test_ShouldNotBeDocsOnly_WhenRenameTouchesCodePath(self):
        # --no-renames surfaces a rename as delete(old)+add(new); a code path on either
        # side => false. Rename a doc INTO a code path.
        def mutate(r):
            os.remove(Path(r) / "src/App/Program.cs")
            _write(r, "src/App/Program.renamed.cs", "// base\n")
        self._run_case(mutate, "docs_only=false")

    def test_ShouldBeFailClosed_WhenDiffIsEmpty(self):
        import tempfile
        with tempfile.TemporaryDirectory() as root:
            base = _init_repo(root)
            # No new commit — HEAD == base, so the diff is empty => fail-closed false.
            self.assertEqual(self._classify(root, base), "docs_only=false")


if __name__ == "__main__":
    unittest.main()
