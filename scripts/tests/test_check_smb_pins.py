"""Unit tests for check-smb-pins.py (the SMB reference stack pin guard).

Runs the script as a subprocess against compose / template / Web CHANGELOG fixtures written to a temp
directory, so every case goes through the one production code path.
Stdlib unittest only — NO pip deps, so this runs in the `coverage-scripts` CI job.
"""
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-smb-pins.py")

_API = "v2.33.1"
_WEB = "v3.27.1-web"


def _compose(api=_API, web=_WEB, realtime=None, mail=None):
    realtime = realtime or api
    mail = mail or api
    return f"""services:
  platform-api:
    image: ghcr.io/verbara/platform/api:${{PLATFORM_API_TAG:-{api}}}
  realtime:
    image: ghcr.io/verbara/platform/realtime:${{PLATFORM_API_TAG:-{realtime}}}
  web:
    image: ghcr.io/verbara/platform/web:${{PLATFORM_WEB_TAG:-{web}}}
  renderer:
    image: ghcr.io/verbara/platform/renderer:${{PLATFORM_API_TAG:-{api}}}
  mail:
    image: ghcr.io/verbara/platform/mail:${{PLATFORM_API_TAG:-{mail}}}
  postgres:
    image: postgres:18-alpine
"""


def _template(api=_API, web=_WEB):
    return f"""# 1. RELEASE IMAGES
#   cosign verify --key docker/cosign.pub ghcr.io/verbara/platform/api:{api}
PLATFORM_API_TAG={api}
PLATFORM_WEB_TAG={web}
ASTERISK_VERSION=22
"""


def _changelog(sections):
    out = "# Changelog\n\n## [Unreleased]\n\n- **Something unreleased (requires Platform ≥ v9.9.9).**\n\n"
    for heading, body in sections:
        out += f"## [{heading}] - 2026-10-28\n\n{body}\n\n"
    return out


# The real 3.25.3-web / 3.25.2-web shapes: a "paired with" sentence, and a section with no floor at all.
_PAIRED_WITH = "Patch release, paired with Platform v2.31.1: the DNC list form creates tenant lists."
_NO_FLOOR = ("**v3.25.1-web was tagged but never published.** 3.25.2-web carries the same changes.\n\n"
             "### Changed — CI\n\n- **The release job's image build may now take 45 minutes.**")


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

    def run_guard(self, compose=None, template=None, changelog=None):
        args = [sys.executable, _SCRIPT,
                "--compose", self._write("compose.yml", compose if compose is not None else _compose()),
                "--template", self._write("env.example", template if template is not None else _template())]
        if changelog is not None:
            args += ["--web-changelog", self._write("CHANGELOG.md", changelog)]
        return subprocess.run(args, capture_output=True, text=True, check=False)


class AgreementTests(_Fixture):
    def test_main_ShouldExitZero_WhenEveryDefaultAgreesWithTheTemplate(self):
        r = self.run_guard()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldFailNamingBothValues_WhenOneComposeDefaultDiffers(self):
        r = self.run_guard(compose=_compose(mail="v2.8.1"))
        self.assertNotEqual(r.returncode, 0)
        out = r.stdout + r.stderr
        self.assertIn("v2.8.1", out)
        self.assertIn(_API, out)

    def test_main_ShouldFailNamingBothValues_WhenTheTemplateWebTagDiffers(self):
        r = self.run_guard(template=_template(web="v3.2.0-web"))
        self.assertNotEqual(r.returncode, 0)
        out = r.stdout + r.stderr
        self.assertIn("v3.2.0-web", out)
        self.assertIn(_WEB, out)

    def test_main_ShouldFail_WhenTheTemplateApiTagDiffers(self):
        r = self.run_guard(template=_template(api="v2.6.0"))
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("v2.6.0", r.stdout + r.stderr)

    def test_main_ShouldFail_WhenTheTemplateLacksAPin(self):
        r = self.run_guard(template="PLATFORM_API_TAG=v2.33.1\n# PLATFORM_WEB_TAG=v3.27.1-web\n")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("PLATFORM_WEB_TAG", r.stdout + r.stderr)

    def test_main_ShouldFail_WhenAPlatformImageHasNoDefault(self):
        compose = _compose().replace("realtime:${PLATFORM_API_TAG:-v2.33.1}", "realtime:${PLATFORM_API_TAG}")
        r = self.run_guard(compose=compose)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("realtime", r.stdout + r.stderr)

    def test_main_ShouldFail_WhenAPlatformImageIsHardCoded(self):
        compose = _compose().replace("renderer:${PLATFORM_API_TAG:-v2.33.1}", "renderer:v2.33.1")
        r = self.run_guard(compose=compose)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("renderer", r.stdout + r.stderr)

    def test_main_ShouldFail_WhenAPinIsNotAReleaseTag(self):
        r = self.run_guard(compose=_compose(api="latest"), template=_template(api="latest"))
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("latest", r.stdout + r.stderr)


class FloorTests(_Fixture):
    def test_main_ShouldFailNamingBothVersions_WhenTheWebFloorExceedsTheApiPin(self):
        log = _changelog([("3.27.1-web", "Patch; **requires Platform ≥ v2.34.0** for something.")])
        r = self.run_guard(changelog=log)
        self.assertNotEqual(r.returncode, 0)
        out = r.stdout + r.stderr
        self.assertIn("v2.34.0", out)
        self.assertIn(_API, out)

    def test_main_ShouldExitZero_WhenTheWebFloorIsBelowTheApiPin(self):
        log = _changelog([("3.27.1-web", "Patch; **requires Platform ≥ v2.33.0** for the tiers.")])
        r = self.run_guard(changelog=log)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldExitZero_WhenTheWebFloorEqualsTheApiPin(self):
        log = _changelog([("3.27.1-web", "Patch; requires Platform >= v2.33.1.")])
        r = self.run_guard(changelog=log)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldUseTheHighestFloor_WhenTheSectionStatesItPerItem(self):
        body = ("- **A (requires Platform ≥ v2.29.0).** x\n"
                "- **B (requires Platform ≥ v2.40.0).** y")
        r = self.run_guard(changelog=_changelog([("3.27.1-web", body)]))
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("v2.40.0", r.stdout + r.stderr)

    def test_main_ShouldFailFloorUnknown_WhenNoSectionExistsForTheWebPin(self):
        log = _changelog([("3.27.0-web", "**requires Platform ≥ v2.33.0**")])
        r = self.run_guard(changelog=log)
        self.assertNotEqual(r.returncode, 0)
        out = r.stdout + r.stderr
        self.assertIn("floor unknown", out)
        self.assertIn("3.27.1-web", out)

    def test_main_ShouldFailFloorUnknown_WhenTheSectionSaysOnlyPairedWith(self):
        log = _changelog([("3.27.1-web", _PAIRED_WITH),
                          ("3.27.0-web", "**requires Platform ≥ v2.33.0**")])
        r = self.run_guard(changelog=log)
        self.assertNotEqual(r.returncode, 0)
        out = r.stdout + r.stderr
        self.assertIn("floor unknown", out)
        self.assertIn("3.27.1-web", out)

    def test_main_ShouldFailFloorUnknown_WhenTheSectionStatesNoFloor(self):
        log = _changelog([("3.27.1-web", _NO_FLOOR),
                          ("3.27.0-web", "**requires Platform ≥ v2.33.0**")])
        r = self.run_guard(changelog=log)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("floor unknown", r.stdout + r.stderr)

    def test_main_ShouldNotReadTheNextSection_WhenTheWebPinSectionHasNoFloor(self):
        # The floor of an older section below must never leak into the pinned one.
        log = _changelog([("3.27.1-web", _NO_FLOOR), ("3.27.0-web", "requires Platform ≥ v2.0.0")])
        r = self.run_guard(changelog=log)
        self.assertNotEqual(r.returncode, 0)

    def test_main_ShouldFailFloorCheck_WhenAgreementAlsoFails(self):
        log = _changelog([("3.27.1-web", "**requires Platform ≥ v2.33.0**")])
        r = self.run_guard(compose=_compose(mail="v2.8.1"), changelog=log)
        self.assertNotEqual(r.returncode, 0)


class InputTests(_Fixture):
    def test_main_ShouldFail_WhenTheComposeHasNoPlatformImage(self):
        r = self.run_guard(compose="services:\n  pg:\n    image: postgres:18-alpine\n")
        self.assertNotEqual(r.returncode, 0)

    def test_main_ShouldFail_WhenTheChangelogIsMissing(self):
        args = [sys.executable, _SCRIPT,
                "--compose", self._write("compose.yml", _compose()),
                "--template", self._write("env.example", _template()),
                "--web-changelog", os.path.join(self.dir, "nope.md")]
        r = subprocess.run(args, capture_output=True, text=True, check=False)
        self.assertNotEqual(r.returncode, 0)


if __name__ == "__main__":
    unittest.main()
