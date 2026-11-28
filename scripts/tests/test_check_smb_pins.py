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


def _platform_changelog(versions=("2.33.1", "2.33.0", "2.32.0")):
    out = "# Changelog\n\n## [Unreleased]\n\n- Something unreleased.\n\n"
    for v in versions:
        out += f"## [{v}] - 2026-10-28\n\n- A release.\n\n"
    return out


def _helm_values(web=_WEB):
    return f"""api:
  image:
    repository: ghcr.io/verbara/platform/api
    tag: "v2.33.1"
web:
  image:
    # The Web pin the SMB reference stack pairs with this API line.
    repository: ghcr.io/verbara/platform/web
    tag: "{web}"
  replicas: 1
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

    def run_guard(self, compose=None, template=None, changelog=None, follower=None, platform_changelog=None,
                  helm_values=None, extra=()):
        args = [sys.executable, _SCRIPT,
                "--compose", self._write("compose.yml", compose if compose is not None else _compose()),
                "--template", self._write("env.example", template if template is not None else _template()),
                "--changelog", self._write("PLATFORM-CHANGELOG.md", platform_changelog if platform_changelog is not None
                                           else _platform_changelog())]
        if helm_values is not None:
            args += ["--helm-values", self._write("values.yaml", helm_values)]
        args += list(extra)
        if changelog is not None:
            args += ["--web-changelog", self._write("CHANGELOG.md", changelog)]
        if follower is not None:
            args += ["--web-follower", self._write("follower.yml", follower)]
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
                "--changelog", self._write("PLATFORM-CHANGELOG.md", _platform_changelog()),
                "--web-changelog", os.path.join(self.dir, "nope.md")]
        r = subprocess.run(args, capture_output=True, text=True, check=False)
        self.assertNotEqual(r.returncode, 0)



def _follower(web_line):
    return f"""services:
  platform-api:
    build:
      context: ..
  web:
    {web_line}
  nginx-gateway:
    image: nginx:1.27-alpine
"""


class WebFollowerTests(_Fixture):
    """A stack that builds the API from the tree but runs the published Web image (docker-compose.production.yml)
    follows the reference stack's Web pin, so moving the pin moves both."""

    def test_main_ShouldExitZero_WhenTheFollowerWebDefaultEqualsTheTemplate(self):
        r = self.run_guard(follower=_follower(f"image: ghcr.io/verbara/platform/web:${{PLATFORM_WEB_TAG:-{_WEB}}}"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldFailNamingBothValues_WhenTheFollowerWebDefaultDiffers(self):
        r = self.run_guard(follower=_follower("image: ghcr.io/verbara/platform/web:${PLATFORM_WEB_TAG:-v3.20.0-web}"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        out = r.stdout + r.stderr
        self.assertIn("v3.20.0-web", out)
        self.assertIn(_WEB, out)

    def test_main_ShouldFail_WhenTheFollowerWebTagIsHardCoded(self):
        r = self.run_guard(follower=_follower(f"image: ghcr.io/verbara/platform/web:{_WEB}"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("PLATFORM_WEB_TAG", r.stdout + r.stderr)

    def test_main_ShouldFail_WhenTheFollowerRunsNoWebImage(self):
        r = self.run_guard(follower=_follower("build:\n      context: ../../Verbara.Platform.Web"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("no ghcr.io/verbara/platform/web image", r.stdout + r.stderr)

    def test_main_ShouldCheckTheProductionStack_WhenRunWithTheDefaults(self):
        r = subprocess.run([sys.executable, _SCRIPT], capture_output=True, text=True, check=False)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker-compose.production.yml", r.stdout)

    def test_main_ShouldCheckTheProductionStack_WhenTheDefaultComposeIsNamedExplicitly(self):
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        r = subprocess.run([sys.executable, _SCRIPT, "--compose", "docker/docker-compose.reference-smb.yml"],
                           capture_output=True, text=True, check=False, cwd=root)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("docker-compose.production.yml", r.stdout)

class ApiLagTests(_Fixture):
    """The reference stack's API pin is a Platform release no more than --max-lag releases behind the newest released
    CHANGELOG heading, the newest one set aside (it may be the release being cut) — issue #704: the stack ran v2.34.0
    while v2.41.3 was out, so its Web pin could not move either."""

    def test_main_ShouldExitZero_WhenTheApiPinIsTheNewestRelease(self):
        r = self.run_guard()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldExitZero_WhenTheBumpPrAddsTheNextHeading(self):
        r = self.run_guard(platform_changelog=_platform_changelog(("2.34.0", "2.33.1", "2.33.0")),
                           extra=("--max-lag", "0"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldExitZero_WhenTheApiPinTrailsByMaxLag(self):
        log = _platform_changelog(("2.36.0", "2.35.0", "2.34.0", "2.33.1"))
        r = self.run_guard(platform_changelog=log)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldFailNamingThePinAndTheNewest_WhenTheApiPinTrailsByMoreThanMaxLag(self):
        log = _platform_changelog(("2.37.0", "2.36.0", "2.35.0", "2.34.0", "2.33.1"))
        r = self.run_guard(platform_changelog=log)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        out = r.stdout + r.stderr
        self.assertIn(_API, out)
        self.assertIn("2.37.0", out)

    def test_main_ShouldSortBySemver_WhenHeadingsAreOutOfOrder(self):
        log = _platform_changelog(("2.33.1", "2.10.0", "2.9.0", "2.8.0", "2.37.0", "2.36.0", "2.35.0", "2.34.0"))
        r = self.run_guard(platform_changelog=log)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)

    def test_main_ShouldIgnoreUnreleased_WhenCountingTheLag(self):
        log = _platform_changelog(("2.35.0", "2.34.0", "2.33.1")).replace("## [Unreleased]", "## [Unreleased]\n\n## [Unreleased]")
        r = self.run_guard(platform_changelog=log, extra=("--max-lag", "0"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        r = self.run_guard(platform_changelog=_platform_changelog(("2.34.0", "2.33.1")), extra=("--max-lag", "0"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldFail_WhenTheApiPinIsNotAReleasedHeading(self):
        r = self.run_guard(platform_changelog=_platform_changelog(("2.34.0", "2.33.0")))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("not a release", r.stdout + r.stderr)

    def test_main_ShouldFailWithInputError_WhenTheChangelogHasNoReleasedHeading(self):
        r = self.run_guard(platform_changelog="# Changelog\n\n## [Unreleased]\n")
        self.assertEqual(r.returncode, 2, r.stdout + r.stderr)


def _web_changelog(versions, floor="v2.33.0"):
    return _changelog([(v, f"**Requires Platform ≥ {floor}**.") for v in versions])


class WebLagTests(_Fixture):
    """With --web-changelog, the Web pin is a Web release no more than --max-lag releases behind the newest released
    `X.Y.Z-web` heading (issue #704: v3.27.1-web while v3.32.1-web was out)."""

    def test_main_ShouldExitZero_WhenTheWebPinTrailsByMaxLag(self):
        log = _web_changelog(("3.30.0-web", "3.29.0-web", "3.28.0-web", "3.27.1-web"))
        r = self.run_guard(changelog=log)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldFailNamingThePinAndTheNewest_WhenTheWebPinTrailsByMoreThanMaxLag(self):
        log = _web_changelog(("3.32.1-web", "3.32.0-web", "3.31.0-web", "3.30.0-web", "3.29.0-web", "3.28.0-web",
                              "3.27.1-web"))
        r = self.run_guard(changelog=log)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        out = r.stdout + r.stderr
        self.assertIn(_WEB, out)
        self.assertIn("3.32.1-web", out)

    def test_main_ShouldReadPreSuffixHeadings_WhenCountingTheWebLag(self):
        # Before the -web suffix the Web CHANGELOG headed its releases `## [X.Y.Z]`; they count as releases too.
        log = _web_changelog(("3.27.0", "3.26.0"))
        r = self.run_guard(compose=_compose(web="v3.26.0-web"), template=_template(web="v3.26.0-web"), changelog=log,
                           extra=("--max-lag", "0"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)


class HelmWebFollowerTests(_Fixture):
    """The Kubernetes chart's Web image follows the reference stack's Web pin (infra/k8s/helm/platform/values.yaml)."""

    def test_main_ShouldExitZero_WhenTheChartWebTagEqualsTheTemplate(self):
        r = self.run_guard(helm_values=_helm_values())
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_main_ShouldFailNamingBothValues_WhenTheChartWebTagDiffers(self):
        r = self.run_guard(helm_values=_helm_values(web="v3.20.0-web"))
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        out = r.stdout + r.stderr
        self.assertIn("v3.20.0-web", out)
        self.assertIn(_WEB, out)

    def test_main_ShouldNotReadTheApiTag_WhenLookingForTheChartWebTag(self):
        values = _helm_values().replace(f'    tag: "{_WEB}"\n', "")
        r = self.run_guard(helm_values=values)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("web.image.tag", r.stdout + r.stderr)

    def test_main_ShouldCheckTheChart_WhenRunWithTheDefaults(self):
        r = subprocess.run([sys.executable, _SCRIPT], capture_output=True, text=True, check=False)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("values.yaml", r.stdout)


if __name__ == "__main__":
    unittest.main()
