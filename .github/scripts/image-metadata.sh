#!/usr/bin/env bash
set -euo pipefail

: "${GITHUB_REPOSITORY:?Repository is required}"
: "${GITHUB_EVENT_NAME:?Event is required}"
: "${GITHUB_REF:?Ref is required}"
: "${GITHUB_OUTPUT:?Output path is required}"
: "${DEFAULT_BRANCH:?Default branch is required}"
: "${COMPONENT:?Component is required}"

case "$COMPONENT" in
  control-plane|agent) ;;
  *) echo "Unsupported image component." >&2; exit 1 ;;
esac

repository="${GITHUB_REPOSITORY,,}"
if [[ ! "$repository" =~ ^[a-z0-9][a-z0-9-]*/[a-z0-9][a-z0-9._-]*$ ]]; then
  echo "Repository cannot be used as a container image name." >&2
  exit 1
fi

publish=false
latest=false
# Generate this once in the workflow's release job and pass it to BOTH image jobs.
# Validate the actual UTC calendar/time, not just the shape of a date-looking tag.
valid_version() {
  local value="$1" normalized
  [[ "$value" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{6}$ ]] || return 1
  normalized=$(LC_ALL=C date -u -d "${value:0:10} ${value:11:2}:${value:13:2}:${value:15:2}" +'%Y-%m-%d-%H%M%S' 2>/dev/null) || return 1
  [[ "$normalized" == "$value" ]]
}
version="${BUILD_VERSION:-$(date -u +'%Y-%m-%d-%H%M%S')}"
if ! valid_version "$version"; then
  echo "Build version must be a valid UTC date in YYYY-MM-DD-HHMMSS format." >&2
  exit 1
fi
if [[ "$GITHUB_EVENT_NAME" == push ]]; then
  if [[ "$GITHUB_REF" == "refs/heads/${DEFAULT_BRANCH}" ]]; then
    publish=true
    latest=true
  elif [[ "$GITHUB_REF" == refs/tags/* ]] && valid_version "${GITHUB_REF#refs/tags/}"; then
    publish=true
    version="${GITHUB_REF#refs/tags/}"
  fi
fi

printf 'name=ghcr.io/%s-%s\npublish=%s\nlatest=%s\nversion=%s\n' \
  "$repository" "$COMPONENT" "$publish" "$latest" "$version" >> "$GITHUB_OUTPUT"
