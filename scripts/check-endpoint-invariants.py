#!/usr/bin/env python3
"""ADR-0012 endpoint-invariant gates for Verbara.Platform (deterministic, no model).

Three gates, all freeze-current / ratchet posture (fail only on regression):

  #6 No silent error-swallowing — an empty `catch {}` (including multi-line and
     `catch (SomeException) {}`) anywhere under an `Endpoints/` directory is
     forbidden. A swallowed exception in an endpoint hides the failure from both
     the caller and the logs. Intentional best-effort swallows (e.g. the Asterisk
     realtime sync in AdminEndpoints, backstopped by the Pro realtime reconciler)
     must carry a body that LOGS the defer, making the eventual-consistency
     contract visible. The 7 pre-existing best-effort swallows were remediated to
     logged catches in the change that introduced this gate, so the floor is zero.

  #9 Orchestrator files stay bounded — Program.cs (the Api composition root)
     measured 1941 LOC at gate-landing. The budget FREEZES that value: the file
     may not grow. It is a ratchet — lower the number as endpoint groups are
     extracted; never raise it to fit new code (raising it is the exact decision
     the gate exists to surface in review).

  #7 Credentials use a CSPRNG — a `Guid.NewGuid` that is string-interpolated into
     a value assigned to a credential-named identifier (key / token / secret /
     credential / password) is forbidden anywhere in the Api composition. A Guid
     is contractually unique, not unguessable (~122 bits, fixed version/variant
     nibbles), so minting a management/service key from one is the finding this
     gate exists to kill. Mint via `SecretTokenGenerator.Mint` (CSPRNG-32) instead.
     The discriminator is deliberate: a `Guid.NewGuid().ToString()` used as an
     entity / message / session id is legitimate and NOT flagged — every real
     credential mint the audit found was the `$"prefix_{Guid.NewGuid}"` shape,
     every legit use was `.ToString()`-shaped. No allowlist; the positive control
     is that every mint routes through SecretTokenGenerator (floor is zero).

Exit 0 = all gates pass; 1 = at least one violation (with ::error:: annotations
so GitHub renders them inline). Run from the repo root:
    python3 scripts/check-endpoint-invariants.py
"""
import re
import sys
from pathlib import Path

# --- Gate #9 config: frozen LOC budgets (ratchet DOWN only) -------------------
# Map of repo-relative path -> max lines. Lower a number when the file shrinks;
# raising one is a reviewed, visible decision to grow an orchestrator file.
LOC_BUDGETS = {
    "src/Verbara.Platform.Api/Program.cs": 1941,  # frozen 2026-07-20
}

# Matches `catch {}`, `catch { }`, `catch (Exception) {}`, and the multi-line
# `catch\n{\n}` form — a TRULY empty catch, which is what ADR-0012 gate #6 forbids
# (and what the audit counted: the 7 best-effort swallows in AdminEndpoints). A
# catch with ANY body does not match — including a comment-only `catch { /*x*/ }`,
# because the comment chars sit between the braces so `\s*\}` cannot follow `{`.
# Comment-only swallows are a softer category the ADR did not gate; they are not
# flagged here. Matches that fall INSIDE a comment (a doc-comment that merely
# mentions `catch {}`) are excluded via _comment_spans.
_EMPTY_CATCH = re.compile(r"catch\s*(\([^)]*\))?\s*\{\s*\}")
_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
_LINE_COMMENT = re.compile(r"//[^\n]*")

# --- Gate #7 config: no Guid.NewGuid in a credential mint ---------------------
# Matches a `Guid.NewGuid` that is string-INTERPOLATED (inside a `$"..."`) into a
# value assigned to a credential-named identifier — e.g. `var rawKey = $"mgmt_{Guid
# .NewGuid():N}"`. The interpolation requirement is the discriminator: it separates a
# minted secret ($"prefix_{Guid.NewGuid}") from a legitimate id (`Guid.NewGuid()
# .ToString("N")`, which never follows `= $"`), so `TokenId = Guid.NewGuid().ToString()`
# and `sessionId = ...` are correctly NOT flagged. Scoped to the Api composition, where
# credentials are minted (Media object keys etc. live elsewhere and are unrelated).
_GUID_CRED_MINT = re.compile(
    r"\b\w*(?:key|token|secret|credential|password)\w*\s*=\s*\$\"[^\"]*\{[^}]*Guid\.NewGuid",
    re.IGNORECASE,
)
_GUID_GATE_SCOPE = "src/Verbara.Platform.Api"


def _comment_spans(text):
    """(start, end) character spans of every // and /* */ comment. Used to drop
    empty-catch matches that live inside a comment. Deliberately naive about
    comment markers inside string literals — a non-case for endpoint code."""
    spans = [(m.start(), m.end()) for m in _BLOCK_COMMENT.finditer(text)]
    spans += [(m.start(), m.end()) for m in _LINE_COMMENT.finditer(text)]
    return spans


def find_empty_catches(root):
    """Repo-relative `path:line` for every truly-empty catch (in code, not in a
    comment) under an Endpoints/ dir."""
    violations = []
    for path in sorted(root.glob("src/**/*.cs")):
        if "Endpoints" not in path.parts:
            continue
        text = path.read_text(encoding="utf-8")
        spans = _comment_spans(text)
        for match in _EMPTY_CATCH.finditer(text):
            if any(start <= match.start() < end for start, end in spans):
                continue  # the match is inside a comment (e.g. a doc-comment)
            line = text.count("\n", 0, match.start()) + 1
            violations.append(f"{path.relative_to(root).as_posix()}:{line}")
    return violations


def check_loc_budgets(root):
    """Violation strings for any budgeted file that grew past its frozen budget."""
    violations = []
    for rel, budget in LOC_BUDGETS.items():
        path = root / rel
        if not path.is_file():
            violations.append(f"{rel}: MISSING (LOC budget target not found)")
            continue
        loc = len(path.read_text(encoding="utf-8").splitlines())
        print(f"  {rel}: {loc} LOC (budget {budget})")
        if loc > budget:
            violations.append(
                f"{rel}: {loc} LOC over the {budget} budget — extract an endpoint "
                f"group instead of growing the composition root; do not raise the "
                f"budget to fit new code."
            )
    return violations


def find_guid_credential_mints(root):
    """Repo-relative `path:line` for every Guid.NewGuid interpolated into a
    credential-named assignment (in code, not a comment) under the Api scope."""
    violations = []
    scope = root / _GUID_GATE_SCOPE
    for path in sorted(scope.glob("**/*.cs")):
        if "obj" in path.parts or "bin" in path.parts:
            continue  # skip generated build output
        text = path.read_text(encoding="utf-8")
        spans = _comment_spans(text)
        for match in _GUID_CRED_MINT.finditer(text):
            if any(start <= match.start() < end for start, end in spans):
                continue  # the match is inside a comment
            line = text.count("\n", 0, match.start()) + 1
            violations.append(f"{path.relative_to(root).as_posix()}:{line}")
    return violations


def main(root=None):
    root = root or Path.cwd()
    failed = False

    empties = find_empty_catches(root)
    if empties:
        failed = True
        print("::error::empty catch {} forbidden in Endpoints/** (ADR-0012 gate #6) — "
              "give the catch a body that logs the swallow, or remove it:")
        for violation in empties:
            print(f"  {violation}")
    else:
        print("Gate #6 (no empty catch in Endpoints/**): OK.")

    loc_violations = check_loc_budgets(root)
    if loc_violations:
        failed = True
        for violation in loc_violations:
            print(f"::error::LOC budget (ADR-0012 gate #9): {violation}")
    else:
        print("Gate #9 (orchestrator LOC budgets): OK.")

    guid_mints = find_guid_credential_mints(root)
    if guid_mints:
        failed = True
        print("::error::Guid.NewGuid forbidden in a credential mint (ADR-0012 gate #7) — "
              "mint secrets via SecretTokenGenerator.Mint (CSPRNG), never Guid.NewGuid:")
        for violation in guid_mints:
            print(f"  {violation}")
    else:
        print("Gate #7 (no Guid.NewGuid in credential mints): OK.")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
