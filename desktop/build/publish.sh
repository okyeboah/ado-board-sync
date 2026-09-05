#!/usr/bin/env bash
#
# ABSD-602 — produce a self-contained build of the desktop application.
#
# Self-contained and single-file, so the result runs on a machine with no .NET
# SDK and no runtime installed (PRD-AC-17). That is the point: the CLI's users
# already have Python, but the people this desktop app is for are the ones who
# should not have to install a toolchain to use it.
#
# It deliberately does NOT sign or notarise anything — that is ABSD-601, needs
# credentials this repository must never hold, and is a separate, authorised step.
#
#   ./publish.sh                  # this machine's runtime identifier
#   ./publish.sh osx-arm64 win-x64 linux-x64
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
desktop="$(cd "$here/.." && pwd)"
project="$desktop/src/AdoBoardSync.Desktop/AdoBoardSync.Desktop.csproj"
out="$desktop/artifacts/publish"

# The version the package carries. Taken from the Python package's pyproject.toml
# so the CLI and the desktop app cannot claim different versions of the same
# product — they are one release, and a user reporting a bug names one number.
version="$(sed -n 's/^version = "\(.*\)"/\1/p' "$desktop/../pyproject.toml" | head -1)"
if [[ -z "$version" ]]; then
  echo "Could not read version from pyproject.toml" >&2
  exit 1
fi

detect_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    MINGW* | MSYS* | CYGWIN*) os=win ;;
    *) echo "Unsupported operating system: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    arm64 | aarch64) arch=arm64 ;;
    x86_64 | amd64) arch=x64 ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
  esac
  echo "$os-$arch"
}

rids=("$@")
if [[ ${#rids[@]} -eq 0 ]]; then
  rids=("$(detect_rid)")
fi

for rid in "${rids[@]}"; do
  target="$out/$rid"
  echo "==> Publishing $rid (version $version)"
  rm -rf "$target"

  # PublishSingleFile bundles the runtime beside the app in one executable.
  # Trimming is deliberately OFF: Avalonia resolves controls and converters
  # reflectively, and a trimmed build fails at window construction rather than at
  # build time — the worst possible place to find out.
  dotnet publish "$project" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=embedded \
    -p:Version="$version" \
    --output "$target"

  echo "==> $rid -> $target"
done

echo
echo "Published $version for: ${rids[*]}"
echo "These builds are UNSIGNED. macOS Gatekeeper and Windows SmartScreen will"
echo "warn about them until ABSD-601's signing step runs over the same output."
