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
if [[ "$GITHUB_EVENT_NAME" == push ]]; then
  if [[ "$GITHUB_REF" == "refs/heads/${DEFAULT_BRANCH}" ]]; then
    publish=true
    latest=true
  elif [[ "$GITHUB_REF" == refs/tags/v* ]]; then
    publish=true
  fi
fi

printf 'name=ghcr.io/%s-%s\npublish=%s\nlatest=%s\n' \
  "$repository" "$COMPONENT" "$publish" "$latest" >> "$GITHUB_OUTPUT"
