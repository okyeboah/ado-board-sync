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
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
desktop="$(cd "$here/.." && pwd)"
publish="$desktop/artifacts/publish"
out="$desktop/artifacts/packages"
app_name="ADO Board Sync"
bundle_id="com.okyeboah.adoboardsync"

version="$(sed -n 's/^version = "\(.*\)"/\1/p' "$desktop/../pyproject.toml" | head -1)"

rid="${1:-}"
if [[ -z "$rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) rid=osx-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64) rid=linux-arm64 ;;
    *) echo "Name a runtime identifier explicitly on this platform." >&2; exit 1 ;;
  esac
fi

source_dir="$publish/$rid"
if [[ ! -d "$source_dir" ]]; then
  echo "No published build at $source_dir. Run ./publish.sh $rid first." >&2
  exit 1
fi

mkdir -p "$out"

case "$rid" in
  osx-*)
    bundle="$out/$app_name.app"
    rm -rf "$bundle"
    mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"

    cp -R "$source_dir"/* "$bundle/Contents/MacOS/"
    chmod +x "$bundle/Contents/MacOS/AdoBoardSync.Desktop"

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
  <key>CFBundleExecutable</key><string>AdoBoardSync.Desktop</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

    dmg="$out/ado-board-sync-$version-$rid-unsigned.dmg"
    rm -f "$dmg"
    if command -v hdiutil >/dev/null 2>&1; then
      staging="$(mktemp -d)"
      cp -R "$bundle" "$staging/"
      ln -s /Applications "$staging/Applications"
      hdiutil create -volname "$app_name" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null
      rm -rf "$staging"
      echo "==> $dmg"
    else
      echo "hdiutil not found; leaving the .app bundle unpackaged at $bundle" >&2
    fi

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
    archive="$out/ado-board-sync-$version-$rid-unsigned.zip"
    rm -f "$archive"
    (cd "$source_dir" && zip -qr "$archive" .)
    echo "==> $archive"

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
    staging="$(mktemp -d)"
    root="$staging/ado-board-sync-$version"
    mkdir -p "$root/bin" "$root/share/applications" "$root/share/ado-board-sync"

    cp -R "$source_dir"/* "$root/share/ado-board-sync/"
    chmod +x "$root/share/ado-board-sync/AdoBoardSync.Desktop"

    cat > "$root/share/applications/ado-board-sync.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=$app_name
Comment=Drive an Azure DevOps board from a Markdown backlog
Exec=%INSTALL%/share/ado-board-sync/AdoBoardSync.Desktop
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

    archive="$out/ado-board-sync-$version-$rid.tar.gz"
    rm -f "$archive"
    tar -czf "$archive" -C "$staging" "ado-board-sync-$version"
    rm -rf "$staging"
    echo "==> $archive"
    echo
    echo "Install per-user with:  tar -xzf <archive> && ./ado-board-sync-$version/install.sh"
    ;;

  *)
    echo "No packaging rule for runtime identifier '$rid'." >&2
    exit 1
    ;;
esac
