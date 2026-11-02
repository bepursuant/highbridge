#!/usr/bin/env bash
# End-to-end TWAIN test under Wine, without a scanner. Builds tests/twain-wine/fake_wia_source.c,
# a TWAIN 1.9 source that behaves like Windows' WIA-TWAIN layer on the old twain_32.dll (no
# callbacks, transfer-ready only as a window message to hParent, capability sets only from the
# thread that opened it), installs it as Wine's sane.ds, and scans through TwainScannerBackend.
#
# Needs: wine (32-bit support), i686-w64-mingw32-gcc, python3, and a Wine prefix with the x86
# .NET 10 Desktop and ASP.NET Core runtimes installed. Set WINEPREFIX to use a specific prefix.
# Not part of scripts/test.sh: CI agents have no Wine.
set -euo pipefail
cd "$(dirname "$0")/.."
root="$PWD"

: "${WINEPREFIX:=$HOME/.wine}"
export WINEPREFIX WINEDEBUG=-all
work="$(mktemp -d)"

twain_dir="$WINEPREFIX/drive_c/windows/twain_32"
[[ -d "$twain_dir" ]] || { echo "No $twain_dir: is $WINEPREFIX a Wine prefix?" >&2; exit 2; }

echo "== Building the fake source"
i686-w64-mingw32-gcc -shared -O2 -Wall -o "$work/sane.ds" tests/twain-wine/fake_wia_source.c -Wl,--kill-at
# Wine's twain_32 only loads sane.ds and gphoto2.ds, so the fake takes sane.ds's place.
if [[ -f "$twain_dir/sane.ds" && ! -f "$twain_dir/sane.ds.wine-orig" ]]; then
  cp "$twain_dir/sane.ds" "$twain_dir/sane.ds.wine-orig"
fi
cp "$work/sane.ds" "$twain_dir/sane.ds"

echo "== Building the probe and the app"
dotnet publish tests/twain-wine/Probe/Highbridge.TwainProbe.csproj -c Release -o "$work/probe" -nologo -v quiet
dotnet publish src/Highbridge.App/Highbridge.App.csproj -c Release -o "$work/app" -nologo -v quiet

winpath() { echo "Z:${1//\//\\}"; }
failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }

# probe NAME EXPECTED_RESULT [ENV=VALUE...] -- [PROBE ARGS...]
probe() {
  local name="$1" expected="$2"; shift 2
  local env=()
  while [[ $# -gt 0 && "$1" != "--" ]]; do env+=("$1"); shift; done
  shift
  local dir="$work/$name"
  mkdir -p "$dir"
  echo "== $name"
  (cd "$work/probe" && env -u DOTNET_ROOT "${env[@]}" FAKE_TWAIN_LOG="$(winpath "$dir/fake.log")" \
    timeout 180 wine Highbridge.TwainProbe.exe "--out=$(winpath "$dir")" "$@" >"$dir/out.log" 2>&1) || true
  local result
  result="$(grep '^RESULT' "$dir/out.log" | tail -1 | tr -d '\r')"
  echo "   $result"
  [[ "$result" == "RESULT $expected"* ]] || { fail "$name: expected '$expected'"; tail -40 "$dir/out.log"; }
  if grep -q "wrong thread" "$dir/fake.log" 2>/dev/null; then fail "$name: capabilities set from the wrong thread"; fi
  if grep -q "hParent=00000000" "$dir/fake.log" 2>/dev/null; then fail "$name: enabled without a parent window"; fi
}

# check_rgb NAME PAGE: the fake draws red rising left to right and green rising top to bottom.
check_rgb() {
  python3 - "$work/$1/$2" <<'EOF' || fail "$1: image is not the fake source's test pattern"
import struct, sys, zlib
data = open(sys.argv[1], "rb").read()
pos, idat = 8, b""
while pos < len(data):
    length, kind = struct.unpack(">I4s", data[pos:pos + 8])
    body = data[pos + 8:pos + 8 + length]
    if kind == b"IHDR":
        width, height, depth, color = struct.unpack(">IIBB", body[:10])
    elif kind == b"IDAT":
        idat += body
    pos += 12 + length
channels = {0: 1, 2: 3, 4: 2, 6: 4}[color]
assert depth == 8, depth
raw, stride, rows, prev, i = zlib.decompress(idat), width * channels, [], bytearray(width * channels), 0
for _ in range(height):
    kind, line = raw[i], bytearray(raw[i + 1:i + 1 + stride]); i += 1 + stride
    for x in range(stride):
        a = line[x - channels] if x >= channels else 0
        b, c = prev[x], prev[x - channels] if x >= channels else 0
        p = a + b - c
        pred = [0, a, b, (a + b) // 2, a if abs(p - a) <= abs(p - b) and abs(p - a) <= abs(p - c) else b if abs(p - b) <= abs(p - c) else c][kind]
        line[x] = (line[x] + pred) & 255
    rows.append(line); prev = line
px = lambda x, y: tuple(rows[y][x * channels:x * channels + 3])
top_left, top_right, bottom_left = px(0, 0), px(width - 1, 0), px(0, height - 1)
print(f"   {width}x{height}: top-left {top_left}, top-right {top_right}, bottom-left {bottom_left}")
assert top_left[0] < 10 and top_right[0] > 245, "red should rise left to right"
assert top_left[1] < 10 and bottom_left[1] > 200, "green should rise top to bottom"
EOF
}

probe native-bw-feeder-on-flatbed "ok pages=1" -- --color=bw --dpi=200 --source=feeder
grep -q "no CAP_FEEDERENABLED" "$work/native-bw-feeder-on-flatbed/out.log" || fail "feeder request on a flatbed was not reported"

probe native-color "ok pages=1" -- --color=color --dpi=100
check_rgb native-color page-1.png

probe memory-only-color "ok pages=1" FAKE_TWAIN_MEMORY_ONLY=1 -- --color=color --dpi=150
grep -q "transfer Memory" "$work/memory-only-color/out.log" || fail "memory-only: did not fall back to a memory transfer"
check_rgb memory-only-color page-1.png

probe memory-only-gray "ok pages=1" FAKE_TWAIN_MEMORY_ONLY=1 -- --color=gray --dpi=75

probe unsupported-dpi "ok pages=1" -- --color=gray --dpi=250
grep -q "using the nearest, 300" "$work/unsupported-dpi/out.log" || fail "unsupported-dpi: did not pick the nearest resolution"

probe every-set-rejected "ok pages=1" FAKE_TWAIN_REJECT_SETS=1 -- --color=bw --dpi=300 --source=flatbed --max-pages=1
grep -q "the source rejected BlackWhite: Failure, TWCC Bummer" "$work/every-set-rejected/out.log" || fail "every-set-rejected: rejection not logged with its condition code"
check_rgb every-set-rejected page-1.png

probe three-pages "ok pages=3" FAKE_TWAIN_PAGES=3 -- --color=gray --dpi=75
probe max-pages "ok pages=2" FAKE_TWAIN_PAGES=3 -- --color=gray --dpi=75 --max-pages=2

probe never-signals "error code=scan_timeout" FAKE_TWAIN_NO_XFERREADY=1 -- --idle-timeout=3

probe diagnose "ok diagnose" -- --diagnose
grep -q "WIA-TWAIN compatibility layer" "$work/diagnose/out.log" || fail "diagnose: WIA layer not identified"
grep -q "ICapXResolution" "$work/diagnose/out.log" || fail "diagnose: capabilities missing"

echo "== app --diagnose-scanner --quiet"
local_app_data="$(cd "$work/app" && env -u DOTNET_ROOT wine cmd /c echo %LOCALAPPDATA% 2>/dev/null | tr -d '\r')"
logs="$(env -u DOTNET_ROOT wine winepath -u "$local_app_data\\Highbridge\\logs" 2>/dev/null | tr -d '\r')"
rm -f "$logs/scanner-diagnostics.txt"
if (cd "$work/app" && env -u DOTNET_ROOT timeout 180 wine Highbridge.exe --diagnose-scanner --quiet); then
  if grep -q "WIA-highbridge test scanner" "$logs/scanner-diagnostics.txt" 2>/dev/null; then
    echo "   wrote $logs/scanner-diagnostics.txt"
  else
    fail "app: $logs/scanner-diagnostics.txt missing or incomplete"
  fi
else
  fail "app --diagnose-scanner exited with an error"
fi

if [[ $failures -gt 0 ]]; then
  echo "$failures check(s) failed. Probe output and fake-source logs are in $work"
  exit 1
fi
rm -rf "$work"
echo "All TWAIN scenarios passed."
