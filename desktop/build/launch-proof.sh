#!/usr/bin/env bash
#
# ABSD-506 — prove the published binary raises a real window on this machine.
#
# Exit code 0 is not evidence: a XAML load failure also exits 0. The window is
# therefore observed from OUTSIDE the process, per platform:
#
#   macOS    CGWindowList via pyobjc — a layer-0 window whose owner process is
#            this binary, with real bounds. Owner names are public; no screen
#            recording permission is involved.
#   Linux    xdotool --name search on Xvfb (installed here if missing).
#   Windows  PowerShell MainWindowHandle on the launched process.
#
# On macOS and Linux the app itself runs under `env -i` with a PATH that does
# not contain the .NET SDK — the self-contained build must not need it
# (PRD-AC-17). Windows keeps the surrounding environment (a Windows process
# needs SystemRoot) but drops the dotnet directories from PATH.
#
#   ./launch-proof.sh            # detect this machine's RID
#   ./launch-proof.sh osx-arm64  # or: linux-x64, win-x64
#
# shellcheck source=common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

rid="${1:-$(detect_rid)}"

case "$rid" in
  win-*) exe="$publish_root/$rid/AdoBoardSync.Desktop.exe" ;;
  *)     exe="$publish_root/$rid/AdoBoardSync.Desktop" ;;
esac

if [[ ! -f "$exe" ]]; then
  echo "No published binary at $exe. Run ./publish.sh $rid first." >&2
  exit 1
fi

app_log="$(mktemp)"
poll_seconds=90

found_macos() {
  python3 - "$exe" <<'PY'
import sys, time, os

exe = sys.argv[1]
owner_hint = os.path.basename(exe).lower()

try:
    import Quartz
except ImportError:
    print("pyobjc not installed; run: python3 -m pip install pyobjc-framework-Quartz")
    sys.exit(2)

deadline = time.time() + int(os.environ.get("POLL_SECONDS", "90"))
while time.time() < deadline:
    windows = Quartz.CGWindowListCopyWindowInfo(
        Quartz.kCGWindowListOptionOnScreenOnly
        | Quartz.kCGWindowListExcludeDesktopElements,
        Quartz.kCGNullWindowID,
    )
    for w in windows:
        owner = (w.get("kCGWindowOwnerName") or "").lower()
        layer = w.get("kCGWindowLayer", 99)
        bounds = w.get("kCGWindowBounds") or {}
        width = bounds.get("Width", 0)
        height = bounds.get("Height", 0)
        if owner_hint in owner and layer == 0 and width > 200 and height > 200:
            print(f"Window found: owner={owner} bounds={width}x{height}")
            sys.exit(0)
    time.sleep(2)

print(f"No layer-0 window for {owner_hint!r} appeared within the poll window")
sys.exit(1)
PY
}

found_linux() {
  deadline=$((SECONDS + poll_seconds))
  while (( SECONDS < deadline )); do
    ids="$(xdotool search --onlyvisible --name 'ADO Board Sync' 2>/dev/null || true)"
    if [[ -n "$ids" ]]; then
      echo "Window found: xdotool ids $ids"
      return 0
    fi
    sleep 2
  done
  echo "No window named 'ADO Board Sync' appeared on Xvfb"
  return 1
}

found_windows() {
  local deadline=$((SECONDS + poll_seconds))
  while (( SECONDS < deadline )); do
    if powershell -NoProfile -Command \
      "if (Get-Process | Where-Object { \$_.ProcessName -like '*AdoBoardSync*' -and \$_.MainWindowHandle -ne 0 }) { exit 0 } else { exit 1 }"; then
      echo "Window found: process holds a non-zero MainWindowHandle"
      return 0
    fi
    sleep 2
  done
  echo "No AdoBoardSync process with a MainWindowHandle appeared"
  return 1
}

app_pid=""

case "$(uname -s)" in
  Darwin)
    if ! python3 -c "import Quartz" 2>/dev/null; then
      echo "==> Installing pyobjc Quartz bindings for the window check"
      python3 -m pip install --quiet --user pyobjc-framework-Quartz 2>/dev/null \
        || python3 -m pip install --quiet --user --break-system-packages pyobjc-framework-Quartz
    fi
    echo "==> Launching (no .NET on PATH): $exe"
    env -i HOME="$HOME" TMPDIR="${TMPDIR:-/tmp}" PATH="/usr/bin:/bin" \
      POLL_SECONDS="$poll_seconds" "$exe" >"$app_log" 2>&1 &
    app_pid=$!
    if found_macos; then result=0; else result=1; fi
    ;;

  Linux)
    if ! command -v xdotool >/dev/null 2>&1 || ! command -v Xvfb >/dev/null 2>&1; then
      echo "==> Installing Xvfb, xdotool and fontconfig"
      sudo apt-get update -qq
      sudo apt-get install -y -qq xvfb xdotool libfontconfig1
    fi
    echo "==> Starting Xvfb :99"
    Xvfb :99 -screen 0 1280x800x24 >/dev/null 2>&1 &
    xvfb_pid=$!
    export DISPLAY=:99
    sleep 2
    echo "==> Launching (no .NET on PATH): $exe"
    env -i HOME="$HOME" TMPDIR="${TMPDIR:-/tmp}" PATH="/usr/bin:/bin" \
      DISPLAY="$DISPLAY" "$exe" >"$app_log" 2>&1 &
    app_pid=$!
    if found_linux; then result=0; else result=1; fi
    kill "$xvfb_pid" 2>/dev/null || true
    ;;

  MINGW* | MSYS* | CYGWIN*)
    echo "==> Launching (dotnet directories off PATH): $exe"
    exe_win="$(cygpath -w "$exe")"
    env PATH="/c/Windows/System32:/c/Windows" "$exe_win" >"$app_log" 2>&1 &
    app_pid=$!
    if found_windows; then result=0; else result=1; fi
    ;;

  *)
    echo "Unsupported platform for the launch proof: $(uname -s)" >&2
    exit 2
    ;;
esac

if [[ "$result" -ne 0 ]]; then
  echo "==> Launch proof FAILED. The application's output:"
  tail -40 "$app_log" || true
fi

if [[ -n "$app_pid" ]]; then
  kill "$app_pid" 2>/dev/null || true
fi

rm -f "$app_log"
exit "$result"
