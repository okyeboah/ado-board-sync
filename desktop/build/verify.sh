#!/usr/bin/env bash
#
# Assert that a published build is actually for the runtime identifier it claims.
#
# This is the failure a cross-compiled self-contained publish really has: the
# file exists, it has a plausible size, and it is for the wrong machine. Size
# checks cannot see that; the binary's own header can.
#
# It lives here rather than inline in the workflow so that it runs locally too —
# a developer cross-publishing on a Mac gets the same assertion CI does, and the
# runtime identifier is dispatched on in one place instead of three.
#
#   ./verify.sh              # this machine's runtime identifier
#   ./verify.sh win-x64
#
# shellcheck source=common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_tool file

rid="${1:-$(detect_rid)}"
exe="$publish_root/$rid/$(exe_name "$rid")"

if [[ ! -f "$exe" ]]; then
  echo "No published executable at $exe. Run ./publish.sh $rid first." >&2
  exit 1
fi

# Matched against `file`'s description of the binary. Both halves matter: the
# format proves the operating system, the architecture token proves the machine.
case "$rid" in
  osx-arm64) expected='Mach-O 64-bit.*arm64' ;;
  osx-x64) expected='Mach-O 64-bit.*x86_64' ;;
  linux-x64) expected='ELF 64-bit.*x86-64' ;;
  linux-arm64) expected='ELF 64-bit.*aarch64' ;;
  win-x64) expected='PE32\+.*x86-64' ;;
  win-arm64) expected='PE32\+.*Aarch64' ;;
  *) echo "No architecture expectation for runtime identifier '$rid'." >&2; exit 1 ;;
esac

described="$(file -b "$exe")"
if ! grep -qiE "$expected" <<<"$described"; then
  echo "Wrong architecture for $rid." >&2
  echo "  expected to match: $expected" >&2
  echo "  file says:         $described" >&2
  exit 1
fi

echo "Verified $(du -h "$exe" | cut -f1) $rid executable: $described"
