#!/usr/bin/env bash
# Builds the Android smoke program with NativeAOT (linux-bionic), pushes it to a phone or emulator over adb and runs it in a shell there.
#   tools/android-smoke/run.sh [--serial SERIAL] [--seconds N] [--input]
# Environment: ADB (path to adb, default `adb`), ANDROID_NDK (an NDK folder, needed to link), OUT (build output folder).
# The program plays a tone at -34 dBFS; it prints the device counters and ends with "android-smoke: ok" (exit status 0) when the checks pass.
# --input also tries to capture; a shell process may not hold the microphone permission, in which case the open failure is printed.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
ADB=${ADB:-adb}
serial=; seconds=4; extra=()
while [ $# -gt 0 ]; do
  case $1 in
    --serial) serial=$2; shift 2;;
    --seconds) seconds=$2; shift 2;;
    --input) extra+=(--input); shift;;
    *) echo "unknown option $1" >&2; exit 2;;
  esac
done
a() { if [ -n "$serial" ]; then "$ADB" -s "$serial" "$@"; else "$ADB" "$@"; fi; }
a get-state >/dev/null 2>&1 || { echo "no device on adb" >&2; exit 1; }
abi=$(a shell getprop ro.product.cpu.abi | tr -d '\r')
case $abi in
  arm64-v8a) rid=linux-bionic-arm64;;
  x86_64) rid=linux-bionic-x64;;
  *) echo "unsupported ABI $abi" >&2; exit 1;;
esac
if [ -z "${ANDROID_NDK:-}" ]; then
  echo "set ANDROID_NDK to an NDK folder (it holds toolchains/llvm)" >&2; exit 1
fi
out=${OUT:-$(mktemp -d)}
echo "device ABI $abi, building $rid into $out"
dotnet publish "$here/OneAudio.AndroidSmoke.csproj" -c Release -r "$rid" -o "$out" -v quiet -clp:NoSummary
remote=/data/local/tmp/oneaudio-android-smoke
a push "$out/oneaudio-android-smoke" "$remote" >/dev/null
a shell chmod 755 "$remote"
log=$(mktemp)
status=0
a shell "$remote" --seconds "$seconds" "${extra[@]}" 2>&1 | tr -d '\r' | tee "$log" || status=$?
a shell rm -f "$remote"
grep -q 'android-smoke: ok' "$log"
