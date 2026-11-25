"""Wiring tests for the `tag-on-main` guard in .github/workflows/release.yml.

test_check_tag_on_main.py proves the script decides correctly; this file proves release.yml
still runs it, and that nothing that builds, pushes, signs or publishes can run without it.
Deleting a `needs:`, dropping the result check from an `always()` job, or neutering the run
step (`true || bash scripts/ci/check-tag-on-main.sh ...`) each turns this suite red, while
the script tests alone stay green.

  * a `tag-on-main` job exists, cannot be skipped or soft-failed, and its run step ends
    with the bare script call, with nothing before it that can short-circuit the call
  * every other job reaches `tag-on-main` through `needs:`, directly or transitively
  * every job whose `if:` uses a status function that runs it past a failed need
    (`always()`, `failure()`, `cancelled()`) needs `tag-on-main` directly and requires
    `needs.tag-on-main.result == 'success'` as a top-level `&&` term

PyYAML is installed (pinned) by the `coverage-scripts` CI job that runs
`python3 -m unittest discover scripts/tests`; it is imported unconditionally so a missing
module fails the suite instead of skipping it.
"""
import os
import re
import unittest

import yaml

_HERE = os.path.dirname(os.path.abspath(__file__))
_RELEASE = os.path.abspath(
    os.path.join(_HERE, os.pardir, os.pardir, ".github", "workflows", "release.yml"))

GUARD_JOB = "tag-on-main"
GUARD_SCRIPT = "scripts/ci/check-tag-on-main.sh"
GUARD_TERM = "needs.tag-on-main.result == 'success'"

# The step's last command must be the bare call: no `||`, `;`, `&` or pipe after it that
# could swallow its exit code, and nothing before it on the same line.
_CALL_LINE = re.compile(r"^bash " + re.escape(GUARD_SCRIPT) + r"(\s+[^;&|]*)?$")
# Anything on an earlier line that could end the step early with success, or turn `-e` off.
_SHORT_CIRCUIT = re.compile(
    r"\b(exit|return|exec|trap|if|then|else|elif|fi|case|esac|while|until|for|do|done|"
    r"function|source)\b|\|\||&&|;|\(\)|^\.\s|set\s+\+")
# Status functions that let a job run after a failed or skipped need.
_BYPASSING_STATUS = re.compile(r"\b(always|failure|cancelled)\s*\(\s*\)")


def _load_jobs():
    with open(_RELEASE, encoding="utf-8") as fh:
        return yaml.safe_load(fh)["jobs"]


def _needs(job):
    needs = job.get("needs", [])
    return [needs] if isinstance(needs, str) else list(needs)


def _strip_expr(expr):
    expr = str(expr).strip()
    if expr.startswith("${{") and expr.endswith("}}"):
        expr = expr[3:-2].strip()
    return expr


def _top_level(expr, op):
    """Split `expr` on `op` outside parentheses and quotes."""
    parts, depth, quote, start, i = [], 0, None, 0, 0
    while i < len(expr):
        ch = expr[i]
        if quote:
            if ch == quote:
                quote = None
        elif ch == "'":
            quote = ch
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        elif depth == 0 and expr.startswith(op, i):
            parts.append(expr[start:i].strip())
            i += len(op)
            start = i
            continue
        i += 1
    parts.append(expr[start:].strip())
    return parts


class ReleaseTagOnMainWiringTests(unittest.TestCase):
    def setUp(self):
        self.jobs = _load_jobs()

    def test_guardJob_ShouldExist_WhenReleaseWorkflowIsParsed(self):
        self.assertIn(GUARD_JOB, self.jobs, msg=f"{_RELEASE} has no `{GUARD_JOB}` job")

    def test_guardJob_ShouldNotBeSkippableOrSoftFailing_WhenItRuns(self):
        job = self.jobs[GUARD_JOB]
        self.assertNotIn("if", job, msg="an `if:` on the guard job could skip it")
        self.assertFalse(job.get("continue-on-error", False),
                         msg="continue-on-error would let a failed guard count as success")
        self.assertEqual(_needs(job), [], msg="the guard must be the first job, needing none")

    def test_guardStep_ShouldEndWithTheBareScriptCall_WhenNothingCanShortCircuitIt(self):
        steps = [s for s in self.jobs[GUARD_JOB].get("steps", [])
                 if GUARD_SCRIPT in str(s.get("run", ""))]
        self.assertEqual(len(steps), 1, msg=f"exactly one step must call {GUARD_SCRIPT}")
        step = steps[0]
        self.assertNotIn("if", step, msg="an `if:` on the guard step could skip the call")
        self.assertFalse(step.get("continue-on-error", False),
                         msg="continue-on-error would swallow the call's exit code")
        self.assertIn(step.get("shell", "bash"), ("bash",),
                      msg="the guard step must run under the default bash shell")

        lines = [ln.strip() for ln in step["run"].splitlines()]
        commands = [ln for ln in lines if ln and not ln.startswith("#")]
        self.assertGreaterEqual(len(commands), 2, msg=f"commands: {commands}")
        self.assertEqual(commands[0], "set -euo pipefail",
                         msg="the step must start with `set -euo pipefail`")
        self.assertRegex(commands[-1], _CALL_LINE,
                         msg="the last command must be the bare script call, nothing around it")
        self.assertEqual(sum(GUARD_SCRIPT in c for c in commands), 1,
                         msg="the script must be called once, as the last command")
        for earlier in commands[1:-1]:
            self.assertIsNone(_SHORT_CIRCUIT.search(earlier),
                              msg=f"`{earlier}` could end the step before the call runs")

    def test_everyOtherJob_ShouldReachTheGuardThroughNeeds_WhenDirectOrTransitive(self):
        for name in self.jobs:
            if name == GUARD_JOB:
                continue
            seen, stack = set(), list(_needs(self.jobs[name]))
            while stack:
                dep = stack.pop()
                if dep in seen:
                    continue
                seen.add(dep)
                stack.extend(_needs(self.jobs.get(dep, {})))
            with self.subTest(job=name):
                self.assertIn(GUARD_JOB, seen,
                              msg=f"job `{name}` can run without `{GUARD_JOB}`: needs {sorted(seen)}")

    def test_jobsThatRunPastAFailedNeed_ShouldRequireTheGuardSucceeded_WhenIfUsesAlways(self):
        checked = 0
        for name, job in self.jobs.items():
            if name == GUARD_JOB or "if" not in job:
                continue
            expr = _strip_expr(job["if"])
            if not _BYPASSING_STATUS.search(expr):
                continue
            checked += 1
            with self.subTest(job=name):
                self.assertIn(GUARD_JOB, _needs(job),
                              msg=f"`{name}` must need `{GUARD_JOB}` directly to test its result")
                self.assertEqual(_top_level(expr, "||"), [expr],
                                 msg=f"`{name}`'s if: has a top-level `||` that could bypass the guard")
                self.assertIn(GUARD_TERM, _top_level(expr, "&&"),
                              msg=f"`{name}`'s if: must AND in `{GUARD_TERM}`")
        # release.yml's smoke job runs under always(); finding none means the detector broke
        # (or smoke no longer uses always(), in which case drop this line).
        self.assertGreater(checked, 0, msg="no always() job found; the detector is broken")

    def test_topLevelSplit_ShouldIgnoreOperatorsInsideParensAndQuotes(self):
        self.assertEqual(_top_level("a && (b || c) && 'x && y'", "&&"),
                         ["a", "(b || c)", "'x && y'"])
        self.assertEqual(_top_level("a && (b || c)", "||"), ["a && (b || c)"])


if __name__ == "__main__":
    unittest.main()
