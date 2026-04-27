#!/usr/bin/env bash
# release.sh — local helper to bump, commit, tag, and push a new Tracelit SDK release.
#
# Usage:
#   ./release.sh patch          # x.y.Z+1
#   ./release.sh minor          # x.Y+1.0
#   ./release.sh major          # X+1.0.0
#   ./release.sh 1.2.3          # explicit version
#   ./release.sh patch --dry-run
set -euo pipefail

# ── Config ────────────────────────────────────────────────────────────────────
MANIFEST="src/Tracelit/Tracelit.csproj"
SEMVER_RE='^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9._-]+)?$'

# ── Helpers ───────────────────────────────────────────────────────────────────
red()    { printf '\033[31m%s\033[0m\n' "$*"; }
green()  { printf '\033[32m%s\033[0m\n' "$*"; }
yellow() { printf '\033[33m%s\033[0m\n' "$*"; }
bold()   { printf '\033[1m%s\033[0m\n'  "$*"; }

die() { red "ERROR: $*" >&2; exit 1; }

confirm() {
  local prompt="${1:-Continue?}"
  printf '%s [y/N] ' "${prompt}"
  read -r answer
  [[ "${answer}" =~ ^[Yy]$ ]]
}

# ── Argument parsing ──────────────────────────────────────────────────────────
BUMP=""
DRY_RUN=false

for arg in "$@"; do
  case "${arg}" in
    --dry-run) DRY_RUN=true ;;
    patch|minor|major) BUMP="${arg}" ;;
    *)
      if [[ "${arg}" =~ ${SEMVER_RE} ]]; then
        BUMP="${arg}"   # explicit version string
      else
        die "Unknown argument: '${arg}'. Usage: ./release.sh patch|minor|major|<x.y.z> [--dry-run]"
      fi
      ;;
  esac
done

[[ -z "${BUMP}" ]] && die "Bump type required. Usage: ./release.sh patch|minor|major|<x.y.z> [--dry-run]"

# ── Pre-flight checks ─────────────────────────────────────────────────────────
command -v git  >/dev/null 2>&1 || die "'git' is not installed."
command -v perl >/dev/null 2>&1 || die "'perl' is not installed (needed to update the manifest)."

[[ -f "${MANIFEST}" ]] || die "Manifest not found: ${MANIFEST}"

if [[ "${DRY_RUN}" == false ]]; then
  if ! git diff --quiet || ! git diff --cached --quiet; then
    die "Working tree is not clean. Commit or stash your changes first."
  fi
fi

# ── Branch check ─────────────────────────────────────────────────────────────
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
if [[ "${CURRENT_BRANCH}" != "main" ]]; then
  yellow "Warning: you are on branch '${CURRENT_BRANCH}', not 'main'."
  confirm "Continue anyway?" || exit 0
fi

# ── Fetch latest tags ─────────────────────────────────────────────────────────
if [[ "${DRY_RUN}" == false ]]; then
  printf 'Fetching latest tags… '
  git fetch --tags --quiet
  green "done"
fi

# ── Read current version from manifest ────────────────────────────────────────
CURRENT_VERSION=$(perl -ne 'print $1 if /<Version>([^<]+)<\/Version>/' "${MANIFEST}")
[[ -n "${CURRENT_VERSION}" ]] || die "Could not read <Version> from ${MANIFEST}."

# Strip pre-release suffix for semver arithmetic
BASE_VERSION="${CURRENT_VERSION%%-*}"
IFS='.' read -r V_MAJOR V_MINOR V_PATCH <<< "${BASE_VERSION}"

# ── Compute new version ───────────────────────────────────────────────────────
case "${BUMP}" in
  patch) NEW_VERSION="${V_MAJOR}.${V_MINOR}.$((V_PATCH + 1))" ;;
  minor) NEW_VERSION="${V_MAJOR}.$((V_MINOR + 1)).0"          ;;
  major) NEW_VERSION="$((V_MAJOR + 1)).0.0"                   ;;
  *)
    # Explicit version — validate semver
    if [[ ! "${BUMP}" =~ ${SEMVER_RE} ]]; then
      die "Invalid semver '${BUMP}'. Expected format: x.y.z or x.y.z-suffix"
    fi
    NEW_VERSION="${BUMP}"
    ;;
esac

NEW_TAG="v${NEW_VERSION}"

# ── Check tag doesn't already exist ──────────────────────────────────────────
if git rev-parse "${NEW_TAG}" >/dev/null 2>&1; then
  die "Tag '${NEW_TAG}' already exists. Choose a different version."
fi

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
bold "Release summary"
printf '  Manifest   : %s\n' "${MANIFEST}"
printf '  Current    : %s\n' "${CURRENT_VERSION}"
printf '  New version: '
green "${NEW_VERSION}"
printf '  Tag        : %s\n' "${NEW_TAG}"
[[ "${DRY_RUN}" == true ]] && yellow "  (dry run — no changes will be made)"
echo ""

confirm "Proceed with release?" || { echo "Aborted."; exit 0; }

if [[ "${DRY_RUN}" == true ]]; then
  green "Dry run complete — no files changed, no commits made."
  exit 0
fi

# ── Bump version in manifest ──────────────────────────────────────────────────
perl -i -pe \
  "s|<Version>[^<]*</Version>|<Version>${NEW_VERSION}</Version>|g" \
  "${MANIFEST}"

green "Bumped <Version> in ${MANIFEST} → ${NEW_VERSION}"

# ── Commit and push the version bump ─────────────────────────────────────────
git add "${MANIFEST}"
git commit -m "chore: release ${NEW_TAG}"
git push origin "${CURRENT_BRANCH}"
green "Pushed version bump commit to ${CURRENT_BRANCH}"

# ── Create and push the annotated tag ────────────────────────────────────────
git tag -a "${NEW_TAG}" -m "Release ${NEW_TAG}"
git push origin "${NEW_TAG}"
green "Pushed tag ${NEW_TAG}"

# ── Print link to GitHub Actions ─────────────────────────────────────────────
REMOTE=$(git remote get-url origin 2>/dev/null || true)
if [[ "${REMOTE}" =~ github\.com[:/]([^/]+)/([^/.]+)(\.git)?$ ]]; then
  OWNER="${BASH_REMATCH[1]}"
  REPO="${BASH_REMATCH[2]}"
  echo ""
  bold "Release workflow triggered:"
  printf '  https://github.com/%s/%s/actions\n' "${OWNER}" "${REPO}"
fi

echo ""
green "Done! Release ${NEW_TAG} is on its way."
