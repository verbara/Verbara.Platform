#!/usr/bin/env bash
# check-tag-on-main.sh — refuse a release tag whose commit is not on main.
#
# release.yml builds, pushes and cosign-signs images on ANY pushed `v*` tag, and CI runs only
# on pull requests into main and on the merge queue. A tag on a commit that never reached main
# would ship a signed image no CI run ever tested. The `tag-on-main` job runs this script before
# every build/push/sign job, and they all `needs:` it.
#
# usage: check-tag-on-main.sh <tag-name> [main-ref]     (main-ref defaults to refs/remotes/origin/main)
#
# Exit codes (fail closed: anything but 0 stops the release):
#   0  the tag's commit is main-ref or an ancestor of it, OR the tag carries the
#      `RETROACTIVE-TAG` marker (ADR-0024: such a run builds, pushes and signs nothing, so
#      the release job's own marker guard turns it into a clean no-op, as before)
#   1  the tag's commit is not on main-ref
#   2  usage error, or the tag or main-ref cannot be resolved to a commit
set -euo pipefail

TAG="${1:-}"
MAIN_REF="${2:-refs/remotes/origin/main}"
if [ -z "$TAG" ]; then
  echo "usage: check-tag-on-main.sh <tag-name> [main-ref]" >&2
  exit 2
fi

if ! git rev-parse --verify --quiet "refs/tags/${TAG}" >/dev/null; then
  echo "::error::refs/tags/${TAG} not found locally; cannot check that it is on main." >&2
  exit 2
fi

annotation=$(git for-each-ref --format='%(contents)' "refs/tags/${TAG}")
if printf '%s' "$annotation" | grep -qF 'RETROACTIVE-TAG'; then
  echo "::notice::Retroactive tag ${TAG}: nothing is built, so the on-main check does not apply."
  exit 0
fi

if ! tag_commit=$(git rev-parse --verify --quiet "refs/tags/${TAG}^{commit}"); then
  echo "::error::refs/tags/${TAG} does not point to a commit." >&2
  exit 2
fi
if ! main_commit=$(git rev-parse --verify --quiet "${MAIN_REF}^{commit}"); then
  echo "::error::${MAIN_REF} not found locally; fetch main before this check." >&2
  exit 2
fi

rc=0
git merge-base --is-ancestor "$tag_commit" "$main_commit" || rc=$?
case "$rc" in
  0)
    echo "Tag ${TAG} (${tag_commit}) is on ${MAIN_REF} (${main_commit})."
    exit 0
    ;;
  1)
    echo "::error::Tag ${TAG} points to ${tag_commit}, which is not on ${MAIN_REF} (${main_commit}). Releases are cut only from main: merge the change through a PR, then tag a commit of main. Nothing was built, pushed or signed." >&2
    exit 1
    ;;
  *)
    echo "::error::git merge-base --is-ancestor failed (exit ${rc}) for ${tag_commit} and ${main_commit}." >&2
    exit 2
    ;;
esac
