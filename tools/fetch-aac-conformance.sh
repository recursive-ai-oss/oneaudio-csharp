#!/usr/bin/env bash
# Downloads the ISO/IEC 14496-26 AAC conformance streams listed in tools/aac-conformance.manifest (the standard's
# publicly available bitstreams and reference waveforms) into <cache>/aac-iso-lc/{mp4,wav}/ and verifies each SHA-256.
# Nothing fetched here is ever committed. Usage: tools/fetch-aac-conformance.sh [cache-dir]
# The cache directory defaults to $ONEAUDIO_CORPUS, then ../.oneaudio-corpus next to the checkout.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cache="${1:-${ONEAUDIO_CORPUS:-$here/../.oneaudio-corpus}}/aac-iso-lc"
base="https://standards.iso.org/ittf/PubliclyAvailableStandards/ISO_IEC_14496-26_2010_Bitstreams"
mkdir -p "$cache/mp4" "$cache/wav"

fetched=0 checked=0
while read -r kind name dvd sha; do
  case "$kind" in ''|'#'*) continue ;; esac
  if [ "$kind" = mp4 ]; then dir=compressedMp4; ext=mp4; else dir=referencesWav; ext=wav; fi
  dest="$cache/$ext/$name.$ext"
  if [ -f "$dest" ] && [ "$(sha256sum "$dest" | cut -d' ' -f1)" = "$sha" ]; then checked=$((checked + 1)); continue; fi
  curl -fsSL --retry 3 -o "$dest.part" "$base/$dvd/mpeg4audio-conformance/$dir/$name.$ext"
  got="$(sha256sum "$dest.part" | cut -d' ' -f1)"
  if [ "$got" != "$sha" ]; then rm -f "$dest.part"; echo "SHA-256 mismatch for $name.$ext: got $got, pinned $sha" >&2; exit 1; fi
  mv "$dest.part" "$dest"
  fetched=$((fetched + 1))
done < "$here/tools/aac-conformance.manifest"
echo "AAC conformance streams: $fetched downloaded, $checked already present and verified, in $cache"
