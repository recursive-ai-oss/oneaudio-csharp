#!/usr/bin/env bash
# Builds the iOS smoke app with NativeAOT for a physical device, signs it, installs it, runs it with the console attached and uninstalls it.
# Needs a Mac with Xcode, the .NET iOS workload, a paired device in Developer Mode (unlocked) and a development signing setup:
#   IOS_DEVICE        device identifier as printed by `xcrun devicectl list devices`
#   IOS_BUNDLE_ID     bundle id the provisioning profile was made for
#   IOS_PROFILE       path to that development .mobileprovision (it must list the device)
#   IOS_KEYCHAIN      keychain holding the signing identity (unlocked by the caller; default: the search list)
#   IOS_KEYCHAIN_PASS_FILE  optional file with that keychain's password, used to unlock it
#   ONEAUDIO_IOS_WORK scratch folder (default: a new temporary one; removed at the end)
# Prints what the app printed; exit status 0 when it ends with "ios-smoke: ok". The app is uninstalled afterwards.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
: "${IOS_DEVICE:?}" "${IOS_BUNDLE_ID:?}" "${IOS_PROFILE:?}"
work=${ONEAUDIO_IOS_WORK:-$(mktemp -d)}
mkdir -p "$work"
cp "$here/OneAudio.IosSmoke.csproj" "$here/Program.cs" "$work/"
cd "$work"

dotnet publish -c Release -r ios-arm64 -p:AotApp=true -p:EnableCodeSigning=false -p:BuildIpa=false \
  -p:ApplicationId="$IOS_BUNDLE_ID" -p:OneAudioRoot="$root/" -v quiet -clp:NoSummary
app=$(find bin -name "OneAudio.IosSmoke.app" -path "*ios-arm64*" | head -1)
[ -n "$app" ] || { echo "no .app produced"; exit 1; }

kc=${IOS_KEYCHAIN:-}
if [ -n "$kc" ] && [ -n "${IOS_KEYCHAIN_PASS_FILE:-}" ]; then
  security unlock-keychain -p "$(cat "$IOS_KEYCHAIN_PASS_FILE")" "$kc"
fi
security cms -D -i "$IOS_PROFILE" > profile.plist
team=$(/usr/libexec/PlistBuddy -c 'Print :ApplicationIdentifierPrefix:0' profile.plist)
# The identity is the profile's certificate that the keychain holds.
ident=""
i=0
while plutil -extract "DeveloperCertificates.$i" raw -o /dev/null profile.plist 2>/dev/null; do
  sha=$(plutil -extract "DeveloperCertificates.$i" raw -o - profile.plist | base64 -d | openssl dgst -sha1 | awk '{print toupper($NF)}')
  if security find-identity -v -p codesigning ${kc:+"$kc"} | grep -q "$sha"; then ident=$sha; break; fi
  i=$((i + 1))
done
[ -n "$ident" ] || { echo "no keychain identity matches the profile"; exit 1; }
cat > entitlements.plist <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>application-identifier</key><string>$team.$IOS_BUNDLE_ID</string>
<key>com.apple.developer.team-identifier</key><string>$team</string>
<key>get-task-allow</key><true/>
</dict></plist>
EOF
cp "$IOS_PROFILE" "$app/embedded.mobileprovision"
codesign --force --sign "$ident" ${kc:+--keychain "$kc"} --entitlements entitlements.plist --timestamp=none "$app" >/dev/null

cleanup() { xcrun devicectl device uninstall app --device "$IOS_DEVICE" "$IOS_BUNDLE_ID" >/dev/null 2>&1 || true; }
trap cleanup EXIT
xcrun devicectl device install app --device "$IOS_DEVICE" "$app" >/dev/null
log="$work/launch.log"
xcrun devicectl device process launch --device "$IOS_DEVICE" --terminate-existing --console "$IOS_BUNDLE_ID" >"$log" 2>&1 || true
cat "$log"
grep -q 'ios-smoke: ok' "$log"
