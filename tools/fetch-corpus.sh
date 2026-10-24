#!/usr/bin/env bash
# Downloads third-party test material into a cache directory outside the repository and verifies SHA-256.
# Nothing fetched here is ever committed. Usage: tools/fetch-corpus.sh [cache-dir] [entry-name ...]
# The cache directory defaults to $ONEAUDIO_CORPUS, then ../.oneaudio-corpus next to the checkout.
# Each entry is: name|url|sha256|archive-type (tgz, zip or raw). Add new corpora as new lines.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cache="${1:-${ONEAUDIO_CORPUS:-$here/../.oneaudio-corpus}}"
shift || true
wanted=("$@")

ENTRIES=(
  "opus-rfc8251|https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz|6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55|tgz"
)

mkdir -p "$cache/_dl"
for entry in "${ENTRIES[@]}"; do
  IFS='|' read -r name url sha kind <<<"$entry"
  if [ ${#wanted[@]} -gt 0 ] && [[ ! " ${wanted[*]} " == *" $name "* ]]; then continue; fi
  dest="$cache/$name"
  if [ -e "$dest/.verified" ] && [ "$(cat "$dest/.verified")" = "$sha" ]; then echo "$name: up to date"; continue; fi
  file="$cache/_dl/$(basename "$url")"
  [ -f "$file" ] || curl -fL --retry 3 -o "$file" "$url"
  echo "$sha  $file" | sha256sum -c - >/dev/null || { echo "$name: SHA-256 mismatch for $file" >&2; rm -f "$file"; exit 1; }
  rm -rf "$dest"; mkdir -p "$dest"
  case "$kind" in
    tgz) tar -xzf "$file" -C "$dest" ;;
    zip) unzip -q "$file" -d "$dest" ;;
    raw) cp "$file" "$dest/" ;;
    *) echo "unknown archive type $kind" >&2; exit 1 ;;
  esac
  echo "$sha" > "$dest/.verified"
  echo "$name: fetched"
done
