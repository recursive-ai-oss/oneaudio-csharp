#!/usr/bin/env bash
# Builds the reference `flac` command-line tool from the official release tarball, user-locally, for the tests and
# benchmarks that compare against it. It is only ever run as a black box. Nothing here is committed or shipped.
# Usage: tools/flac-ref.sh [prefix]    (default: ../.oneaudio-tools next to the checkout; binary at <prefix>/bin/flac)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="${1:-$here/../.oneaudio-tools}"
ver=1.4.3
sha=6c58e69cd22348f441b861092b825e591d0b822e106de6eb0ee4d05d27205b70
[ -x "$prefix/bin/flac" ] && { "$prefix/bin/flac" --version; exit 0; }
work="$(mktemp -d "${TMPDIR:-/tmp}/flac-build.XXXXXX")"
trap 'rm -rf "$work"' EXIT
curl -fL --retry 3 -o "$work/flac.tar.xz" "https://ftp.osuosl.org/pub/xiph/releases/flac/flac-$ver.tar.xz"
echo "$sha  $work/flac.tar.xz" | sha256sum -c - >/dev/null
tar -xf "$work/flac.tar.xz" -C "$work"
cd "$work/flac-$ver"
./configure --prefix="$prefix" --disable-ogg --disable-doxygen-docs --disable-xmms-plugin --disable-examples --disable-dependency-tracking >/dev/null
make -j"$(nproc)" >/dev/null
make install >/dev/null
"$prefix/bin/flac" --version
