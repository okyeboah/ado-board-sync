#!/usr/bin/env bash
#
# What publish.sh and package.sh both need to agree on.
#
# They are two halves of one pipeline: publish.sh writes artifacts/publish/<rid>
# and package.sh reads it. Every fact that has to match across that boundary --
# where the output lives, what the executable is called, which release version
# this is -- belongs here rather than in both files. It was in both, and had
# already drifted: only one of them validated the version, and only one of them
# knew how to detect a Windows runtime identifier.
#
# Sourced, never executed.

set -euo pipefail

build_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
desktop_dir="$(cd "$build_dir/.." && pwd)"
repo_dir="$(cd "$desktop_dir/.." && pwd)"

publish_root="$desktop_dir/artifacts/publish"
packages_root="$desktop_dir/artifacts/packages"

# One release number for the CLI and the desktop app. A user reporting a bug
# names one version, so they cannot be allowed to disagree.
release_version() {
  local version
  version="$(sed -n 's/^version = "\(.*\)"/\1/p' "$repo_dir/pyproject.toml" | head -1)"
  if [[ -z "$version" ]]; then
    echo "Could not read version from $repo_dir/pyproject.toml" >&2
    return 1
  fi
  echo "$version"
}

detect_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    MINGW* | MSYS* | CYGWIN*) os=win ;;
    *) echo "Unsupported operating system: $(uname -s)" >&2; return 1 ;;
  esac
  case "$(uname -m)" in
    arm64 | aarch64) arch=arm64 ;;
    x86_64 | amd64) arch=x64 ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; return 1 ;;
  esac
  echo "$os-$arch"
}

# The published executable's name, which differs only by Windows' suffix. It is
# spelled once here because CI asserts against it too.
exe_name() {
  case "$1" in
    win-*) echo "AdoBoardSync.Desktop.exe" ;;
    *) echo "AdoBoardSync.Desktop" ;;
  esac
}

# A missing tool is an environment error, not a reason to produce a lesser
# artifact quietly. Every branch that needs a tool says so through this.
require_tool() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "$1 is required to package for this platform and was not found." >&2
    return 1
  }
}
