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
# shellcheck source=common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

version="$(release_version)"

rids=("$@")
if [[ ${#rids[@]} -eq 0 ]]; then
  rids=("$(detect_rid)")
fi

for rid in "${rids[@]}"; do
  target="$publish_root/$rid"
  echo "==> Publishing $rid (version $version)"
  rm -rf "$target"

  # PublishSingleFile bundles the runtime beside the app in one executable.
  # Trimming is deliberately OFF: Avalonia resolves controls and converters
  # reflectively, and a trimmed build fails at window construction rather than at
  # build time — the worst possible place to find out.
  dotnet publish "$desktop_dir/src/AdoBoardSync.Desktop/AdoBoardSync.Desktop.csproj" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=embedded \
    -p:Version="$version" \
    --output "$target"

  # The version this output actually carries, recorded beside it. package.sh
  # reads this rather than re-deriving from pyproject.toml, so a package can
  # never be labelled with a version its binary was not built from.
  echo "$version" > "$publish_root/$rid.version"

  echo "==> $rid -> $target"
done

echo
echo "Published $version for: ${rids[*]}"
echo "These builds are UNSIGNED. macOS Gatekeeper and Windows SmartScreen will"
echo "warn about them until ABSD-601's signing step runs over the same output."
