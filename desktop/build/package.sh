#!/usr/bin/env bash
#
# ABSD-601 — turn a published build into a per-user installable package.
#
#   macOS   .app bundle inside a .dmg      (drag to /Applications, or ~/Applications)
#   Windows .zip beside a portable .exe    (per-user, no admin, no MSI runtime)
#   Linux   .tar.gz with a .desktop entry  (installs under ~/.local)
#
# Signing is a SEPARATE, AUTHORISED step and is not performed here. This script
# refuses to pretend: it stamps every package as unsigned and prints exactly what
# a maintainer must run to sign it. A build script that silently produced an
# unsigned artifact named like a signed one is how unsigned software ships.
#
#   ./package.sh              # package whatever publish.sh left for this machine
#   ./package.sh osx-arm64
#
# shellcheck source=common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

app_name="ADO Board Sync"
bundle_id="com.okyeboah.adoboardsync"

rid="${1:-$(detect_rid)}"
source_dir="$publish_root/$rid"
if [[ ! -d "$source_dir" ]]; then
  echo "No published build at $source_dir. Run ./publish.sh $rid first." >&2
  exit 1
fi

# The version the published binary was actually built from, not whatever
# pyproject.toml says today — a stale publish directory must not be relabelled.
stamp="$publish_root/$rid.version"
if [[ ! -f "$stamp" ]]; then
  echo "No version stamp at $stamp. Re-run ./publish.sh $rid." >&2
  exit 1
fi
version="$(cat "$stamp")"
exe="$(exe_name "$rid")"

mkdir -p "$packages_root"

# Every artifact is unsigned, on every platform, so every name says so in the
# same place. The linux archive used to omit it, which made the one mechanism
# that distinguishes signed from unsigned depend on which branch you were in.
artifact="$packages_root/ado-board-sync-$version-$rid-unsigned"

# Staged outside the packages directory on purpose: everything under
# $packages_root is uploaded as a release artifact, and the .app is already
# inside the .dmg. Shipping both doubled the macOS artifact.
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

case "$rid" in
  osx-*)
    require_tool hdiutil

    bundle="$staging/$app_name.app"
    mkdir -p "$bundle/Contents/MacOS"
    cp -R "$source_dir"/* "$bundle/Contents/MacOS/"
    chmod +x "$bundle/Contents/MacOS/$exe"

    # LSMinimumSystemVersion matches the oldest macOS the .NET 10 runtime
    # supports; claiming lower would let the bundle launch and then fail.
    cat > "$bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$app_name</string>
  <key>CFBundleDisplayName</key><string>$app_name</string>
  <key>CFBundleIdentifier</key><string>$bundle_id</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleExecutable</key><string>$exe</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

    artifact="$artifact.dmg"
    rm -f "$artifact"
    # hdiutil images the staging directory directly, so the bundle is copied
    # once rather than built and then copied again to be imaged.
    ln -s /Applications "$staging/Applications"
    hdiutil create -volname "$app_name" -srcfolder "$staging" -ov -format UDZO "$artifact" >/dev/null

    cat <<'SIGNING'

UNSIGNED. To sign and notarise (needs an Apple Developer ID this repository
must never hold):

  codesign --deep --force --options runtime --timestamp \
    --sign "Developer ID Application: <NAME> (<TEAMID>)" "<the .app>"
  xcrun notarytool submit "<the .dmg>" --keychain-profile <profile> --wait
  xcrun stapler staple "<the .dmg>"
SIGNING
    ;;

  win-*)
    # Git-Bash on the Windows runner has no `zip`, which is how this lane first
    # failed. Compress-Archive is the one archiver present on every Windows
    # without depending on what a runner image happens to ship, so it is the
    # only path — a fallback chain would mean shipping branches that never run
    # anywhere and produce subtly different archives when they finally do.
    require_tool powershell

    artifact="$artifact.zip"
    rm -f "$artifact"
    powershell -NoProfile -NonInteractive -Command \
      "Compress-Archive -Path '$(cygpath -w "$source_dir")\\*' \
                        -DestinationPath '$(cygpath -w "$artifact")' -Force"

    cat <<'SIGNING'

UNSIGNED. To sign (needs a code-signing certificate this repository must never
hold):

  signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 \
    /f <cert.pfx> /p <password> AdoBoardSync.Desktop.exe

Per-user by construction: unzip anywhere and run it. No installer, no admin
rights, and nothing written outside the user's own profile.
SIGNING
    ;;

  linux-*)
    root="$staging/ado-board-sync-$version"
    mkdir -p "$root/share/applications" "$root/share/ado-board-sync"

    cp -R "$source_dir"/* "$root/share/ado-board-sync/"
    chmod +x "$root/share/ado-board-sync/$exe"

    cat > "$root/share/applications/ado-board-sync.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=$app_name
Comment=Drive an Azure DevOps board from a Markdown backlog
Exec=%INSTALL%/share/ado-board-sync/$exe
Terminal=false
Categories=Development;
DESKTOP

    # Installs under ~/.local so it needs no root, and rewrites Exec at install
    # time because a .desktop Exec cannot hold a relative path.
    cat > "$root/install.sh" <<'INSTALL'
#!/usr/bin/env bash
set -euo pipefail
prefix="${1:-$HOME/.local}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

mkdir -p "$prefix/share/ado-board-sync" "$prefix/share/applications" "$prefix/bin"
cp -R "$here/share/ado-board-sync/." "$prefix/share/ado-board-sync/"
sed "s|%INSTALL%|$prefix|g" "$here/share/applications/ado-board-sync.desktop" \
  > "$prefix/share/applications/ado-board-sync.desktop"
ln -sf "$prefix/share/ado-board-sync/AdoBoardSync.Desktop" "$prefix/bin/ado-board-sync-desktop"

echo "Installed to $prefix. Run: ado-board-sync-desktop"
echo "Upgrade by re-running this script over the same prefix; uninstall with:"
echo "  rm -rf $prefix/share/ado-board-sync $prefix/share/applications/ado-board-sync.desktop $prefix/bin/ado-board-sync-desktop"
INSTALL
    chmod +x "$root/install.sh"

    artifact="$artifact.tar.gz"
    rm -f "$artifact"
    tar -czf "$artifact" -C "$staging" "ado-board-sync-$version"

    echo
    echo "Install per-user with:  tar -xzf <archive> && ./ado-board-sync-$version/install.sh"
    echo
    echo "UNSIGNED. Linux packages carry no signature here; distribution-level"
    echo "signing is the packager's, and ABSD-601 tracks it with the other two."
    ;;

  *)
    echo "No packaging rule for runtime identifier '$rid'." >&2
    exit 1
    ;;
esac

echo
echo "==> $artifact"
