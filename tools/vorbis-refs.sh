#!/usr/bin/env bash
# Builds the test-only libvorbisfile harness (tools/vorbis_ref_float.c) and writes reference float output for every
# fetched Xiph Vorbis test vector, so the tests can compare our decoder against libvorbis.
# Needs a C compiler and libvorbisfile.so.3 (the distro package). Nothing here is committed or shipped.
# Usage: tools/vorbis-refs.sh [corpus-dir] [refs-dir]
# Defaults: $ONEAUDIO_CORPUS and $ONEAUDIO_REFS, else ../.oneaudio-corpus and ../.oneaudio-refs next to the checkout.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
corpus="${1:-${ONEAUDIO_CORPUS:-$here/../.oneaudio-corpus}}"
refs="${2:-${ONEAUDIO_REFS:-$here/../.oneaudio-refs}}"
lib="$(ldconfig -p | awk '/libvorbisfile\.so\.3 .*x86-64|libvorbisfile\.so\.3 .*aarch64/ {print $NF; exit}')"
[ -n "$lib" ] || { echo "libvorbisfile.so.3 not found" >&2; exit 1; }
mkdir -p "$refs/bin" "$refs/vorbis"
cc -O2 -o "$refs/bin/vorbis_ref_float" "$here/tools/vorbis_ref_float.c" "$lib"
{ dpkg-query -W -f='libvorbisfile ${Version}\n' libvorbisfile3 2>/dev/null || echo "libvorbisfile (version unknown)"; } > "$refs/vorbis/libvorbis-version.txt"
for dir in "$corpus"/vorbis-xiph-*; do
  [ -d "$dir" ] || continue
  name="${dir##*vorbis-xiph-}"
  [ -f "$dir/$name.ogg" ] || continue
  if [ ! -f "$refs/vorbis/$name.txt" ]; then "$refs/bin/vorbis_ref_float" "$dir/$name.ogg" "$refs/vorbis/$name"; fi
done
echo "references in $refs/vorbis ($(cat "$refs/vorbis/libvorbis-version.txt"))"
