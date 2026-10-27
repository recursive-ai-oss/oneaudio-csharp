#!/usr/bin/env bash
# Builds the iOS smoke app and runs it on an iOS simulator (needs a Mac with Xcode, a simulator runtime and the .NET iOS workload).
#   run.sh        build for the simulator, install, launch, print what the app printed; exit status 0 when it ends with "ios-smoke: ok"
#   run.sh aot    only compile for a device with NativeAOT (ios-arm64) to check for trim and AOT warnings; nothing runs
# The simulator build is the Mono runtime build of the app; NativeAOT output exists for devices only.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
# Outside the repository, so its shared build-output settings do not apply to the iOS SDK's own signing steps.
work=${ONEAUDIO_IOS_WORK:-$(mktemp -d)}
mkdir -p "$work"
cp "$here/OneAudio.IosSmoke.csproj" "$here/Program.cs" "$work/"
cd "$work"

if [ "${1:-}" = "aot" ]; then
  dotnet publish -c Release -r ios-arm64 -p:AotApp=true -p:EnableCodeSigning=false -p:BuildIpa=false -p:OneAudioRoot="$root/"
  exit 0
fi

dotnet build -c "${CONFIG:-Debug}" -p:OneAudioRoot="$root/"
app=$(find bin -name "OneAudio.IosSmoke.app" -path "*iossimulator*" | head -1)
device=$(xcrun simctl list devices available | grep -m1 -oE '[0-9A-F]{8}(-[0-9A-F]{4}){3}-[0-9A-F]{12}')
xcrun simctl boot "$device" 2>/dev/null || true
xcrun simctl bootstatus "$device" -b >/dev/null
# Recent systems refuse unsigned code even in the simulator.
codesign --force --deep --sign - "$app"
xcrun simctl install "$device" "$app"
log="$work/launch.log"; : > "$log"
xcrun simctl launch --terminate-running-process --stdout="$log" --stderr="$log" "$device" org.recursiveai.oneaudio.smoke >/dev/null 2>&1 || true
for _ in $(seq 1 120); do
  if grep -qE 'ios-smoke: ok|FAIL' "$log"; then break; fi
  sleep 0.5
done
cat "$log"
grep -q 'ios-smoke: ok' "$log"
