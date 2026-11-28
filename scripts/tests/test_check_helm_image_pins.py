"""Unit tests for check-helm-image-pins.py (the Helm chart image pin guard, issue #468).

Runs the script as a subprocess against values.yaml / Chart.yaml / CHANGELOG.md / ledger fixtures written to a temp
directory, so every case goes through the one production code path.
Needs PyYAML (the script parses the chart), which the `coverage-scripts` CI job installs before the suite.
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-helm-image-pins.py")

_API_DIGEST = "sha256:" + "a" * 64
_RT_DIGEST = "sha256:" + "b" * 64

# The real shape of #468: 13 released headings, 2.41.3 down to 2.34.1.
_REAL = ["2.41.3", "2.41.2", "2.41.1", "2.41.0", "2.40.0", "2.39.1", "2.39.0", "2.38.1", "2.38.0", "2.37.0",
         "2.36.0", "2.35.0", "2.34.1"]


def _values(api_tag="v2.41.3", rt_tag=None, api_digest=_API_DIGEST, rt_digest=_RT_DIGEST):
    rt_tag = api_tag if rt_tag is None else rt_tag
    return f"""namespace: r55-platform

api:
  image:
    repository: ghcr.io/verbara/platform/api
    tag: "{api_tag}"
    digest: "{api_digest}"
  replicas: 2

web:
  image:
    repository: ghcr.io/verbara/platform/web
    tag: "v3.27.1-web"

realtime:
  image:
    repository: ghcr.io/verbara/platform/realtime
    tag: "{rt_tag}"
    digest: "{rt_digest}"
"""


def _chart(app_version="2.41.3"):
    return f'apiVersion: v2\nname: platform\ntype: application\nversion: 0.2.13\nappVersion: "{app_version}"\n'


def _changelog(versions, unreleased="- Nothing yet."):
    out = "# Changelog\n\n---\n\n## [Unreleased]\n\n" + unreleased + "\n\n"
    for v in versions:
        out += f"## [{v}] - 2026-11-25\n\n### Fixed\n\n- Something.\n\n"
    return out


def _ledger(entries_current=(), entries_deprecated=()):
    def entry(img, tag, digest):
        return {"platform_version": tag, "image_ref": f"ghcr.io/verbara/platform/{img}:{tag}",
                "manifest_list_digest": digest, "released_at": "2026-11-25T10:45:19Z"}
    return json.dumps({"current": [entry(*e) for e in entries_current],
                       "deprecated": [entry(*e) for e in entries_deprecated]})


class _Fixture(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.dir = self._tmp.name

    def _write(self, name, text):
        p = os.path.join(self.dir, name)
        with open(p, "w", encoding="utf-8") as fh:
            fh.write(text)
        return p

    def run_guard(self, values=None, chart=None, changelog=None, max_lag=None, ledger=None, changelog_path=None):
        args = [sys.executable, _SCRIPT,
                "--values", self._write("values.yaml", values if values is not None else _values()),
                "--chart-yaml", self._write("Chart.yaml", chart if chart is not None else _chart()),
                "--changelog", changelog_path or self._write(
                    "CHANGELOG.md", changelog if changelog is not None else _changelog(["2.41.3", "2.41.2"]))]
        if max_lag is not None:
            args += ["--max-lag", str(max_lag)]
        if ledger is not None:
            args += ["--ledger", self._write("authorized-digests.json", ledger)]
        return subprocess.run(args, capture_output=True, text=True, check=False)


class PassTests(_Fixture):
    def test_main_ShouldExitZero_WhenThePinIsTheNewestRelease(self):
        r = self.run_guard()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("OK", r.stdout)

    def test_main_ShouldExitZero_WhenTheBumpPrAddsTheNextHeading(self):
        # A release bump PR adds ## [2.41.4] before its images exist; the chart is still at v2.41.3.
        r = self.run_guard(changelog=_changelog(["2.41.4", "2.41.3"]), max_lag=0)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldExitZero_WhenThePinTrailsByMaxLag(self):
        # K = 2: the newest heading is set aside and two more are allowed, so the 4th newest is the last that passes.
        r = self.run_guard(values=_values("v2.41.0"), chart=_chart("2.41.0"),
                           changelog=_changelog(["2.41.3", "2.41.2", "2.41.1", "2.41.0", "2.40.0"]), max_lag=2)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldFail_WhenThePinTrailsByOneMoreThanMaxLag(self):
        r = self.run_guard(values=_values("v2.40.0"), chart=_chart("2.40.0"),
                           changelog=_changelog(["2.41.3", "2.41.2", "2.41.1", "2.41.0", "2.40.0"]), max_lag=2)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("trails", r.stderr)

    def test_main_ShouldIgnoreUnreleased_WhenCountingLag(self):
        r = self.run_guard(values=_values("v2.41.1"), chart=_chart("2.41.1"),
                           changelog=_changelog(["2.41.4", "2.41.3", "2.41.2", "2.41.1"],
                                                unreleased="- **Next: 2.42.0** (requires Platform >= v9.9.9)."),
                           max_lag=2)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldSortBySemver_WhenHeadingsAreOutOfOrder(self):
        # By version 2.10.0 is the newest and 2.8.0 is two behind; by file order or by string it is not.
        r = self.run_guard(values=_values("v2.8.0"), chart=_chart("2.8.0"),
                           changelog=_changelog(["2.8.0", "2.10.0", "2.9.0"]), max_lag=0)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("by 2 releases", r.stderr)
        self.assertIn("## [2.10.0]", r.stderr)

    def test_main_ShouldExitZero_WhenRunWithTheDefaults(self):
        r = subprocess.run([sys.executable, _SCRIPT], capture_output=True, text=True, check=False)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("values.yaml", r.stdout)


class FailTests(_Fixture):
    def test_main_ShouldFailNamingTheLag_WhenThePinTrailsByMoreThanMaxLag(self):
        r = self.run_guard(values=_values("v2.34.1"), chart=_chart("2.34.1"), changelog=_changelog(_REAL))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("v2.34.1 trails", r.stderr)
        self.assertIn("by 12 releases", r.stderr)
        self.assertIn("## [2.41.3]", r.stderr)

    def test_main_ShouldFail_WhenThePinHasNoChangelogHeading(self):
        r = self.run_guard(values=_values("v2.40.9"), chart=_chart("2.40.9"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("no '## [2.40.9]' heading", r.stderr)

    def test_main_ShouldFailNamingBothTags_WhenApiAndRealtimeTagsDiffer(self):
        r = self.run_guard(values=_values("v2.41.3", rt_tag="v2.41.2"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("api.image.tag v2.41.3 != realtime.image.tag v2.41.2", r.stderr)

    def test_main_ShouldFail_WhenAppVersionDiffersFromTheTag(self):
        r = self.run_guard(chart=_chart("2.34.1"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("appVersion '2.34.1' != tag v2.41.3", r.stderr)

    def test_main_ShouldFail_WhenADigestIsEmptyOrMalformed(self):
        for digest in ("", "sha256:" + "a" * 63):
            with self.subTest(digest=digest):
                r = self.run_guard(values=_values(rt_digest=digest))
                self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
                self.assertIn("realtime.image.digest", r.stderr)

    def test_main_ShouldFail_WhenApiAndRealtimeDigestsAreEqual(self):
        r = self.run_guard(values=_values(rt_digest=_API_DIGEST))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("two images, two digests", r.stderr)

    def test_main_ShouldFail_WhenATagIsNotAReleaseTag(self):
        for tag in ("latest", "v2.41.3-web"):
            with self.subTest(tag=tag):
                r = self.run_guard(values=_values(tag), chart=_chart(tag.lstrip("v")))
                self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
                self.assertIn("is not a release tag", r.stderr)

    def test_main_ShouldFailNamingBothDigests_WhenTheLedgerDisagrees(self):
        other = "sha256:" + "c" * 64
        r = self.run_guard(ledger=_ledger([("api", "v2.41.3", _API_DIGEST), ("realtime", "v2.41.3", other)]))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(_RT_DIGEST, r.stderr)
        self.assertIn(other, r.stderr)
        self.assertIn("ghcr.io/verbara/platform/realtime:v2.41.3", r.stderr)

    def test_main_ShouldFail_WhenTheLedgerHasNoEntryForThePin(self):
        r = self.run_guard(ledger=_ledger([("api", "v2.41.2", _API_DIGEST), ("realtime", "v2.41.2", _RT_DIGEST)]))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("no entry for ghcr.io/verbara/platform/api:v2.41.3", r.stderr)

    def test_main_ShouldExitZero_WhenTheLedgerHoldsThePinInDeprecated(self):
        r = self.run_guard(ledger=_ledger(
            [("api", "v2.41.4", "sha256:" + "d" * 64), ("realtime", "v2.41.4", "sha256:" + "e" * 64)],
            [("api", "v2.41.3", _API_DIGEST), ("realtime", "v2.41.3", _RT_DIGEST)]))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("rules + ledger", r.stdout)


class InputErrorTests(_Fixture):
    def test_main_ShouldExitTwo_WhenTheChangelogIsMissing(self):
        r = self.run_guard(changelog_path=os.path.join(self.dir, "no-such-CHANGELOG.md"))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("cannot read", r.stderr)

    def test_main_ShouldExitTwo_WhenValuesHaveNoApiOrRealtimeImage(self):
        for values in ("api:\n  replicas: 2\nrealtime:\n  image:\n    tag: v2.41.3\n",
                       "api:\n  image:\n    tag: v2.41.3\n"):
            with self.subTest(values=values):
                r = self.run_guard(values=values)
                self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
                self.assertIn(".image mapping", r.stderr)

    def test_main_ShouldExitTwo_WhenAnInputIsNotUtf8(self):
        bad = os.path.join(self.dir, "not-utf8-CHANGELOG.md")
        with open(bad, "wb") as fh:
            fh.write(b"\xff\xfe## [2.41.3]\n")
        r = self.run_guard(changelog_path=bad)
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("cannot read", r.stderr)
        self.assertNotIn("Traceback", r.stderr)

    def test_main_ShouldExitTwo_WhenTheChangelogHasNoReleasedHeading(self):
        r = self.run_guard(changelog=_changelog([]))
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)
        self.assertIn("no released", r.stderr)


if __name__ == "__main__":
    unittest.main()
